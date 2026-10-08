using System.Diagnostics;
using System.Text.Json.Nodes;
using AssistBridge.Core;

namespace AssistBridge.Tests;

public class RelayTests
{
    private static async Task<LocalRelayServer> StartServerAsync(int port, string key)
    {
        var cert = LocalRelayCertificate.LoadOrCreate(TestUtil.TempDirectory("cert"));
        var server = new LocalRelayServer(port, key, cert);
        server.Start();
        await Task.Delay(50);
        return server;
    }

    [Fact]
    public async Task LocalRelayRoutesBetweenLeaderAndFollower()
    {
        var port = TestUtil.FreePort();
        await using var server = await StartServerAsync(port, "k1");
        var policy = new CertificatePolicy(PinnedFingerprint: server.Fingerprint);
        await using var follower = new RecordingPeer(new ConnectionInfo("localhost", port, "k1", ConnectionMode.Follower), policy, new TrustNothing());
        var joined = await follower.WaitForTypeAsync(Protocol.MsgChannelJoined);
        Assert.Equal("k1", Protocol.GetString(joined, "channel"));
        Assert.Null(joined["clients"]); // NVDA omits an empty clients list.

        await using var leader = new RecordingPeer(new ConnectionInfo("localhost", port, "k1", ConnectionMode.Leader), policy, new TrustNothing());
        var leaderJoined = await leader.WaitForTypeAsync(Protocol.MsgChannelJoined);
        var clients = leaderJoined["clients"]!.AsArray();
        Assert.Single(clients);
        Assert.Equal("slave", clients[0]!["connection_type"]!.GetValue<string>());
        Assert.Single(leader.Session.Peers);
        Assert.Equal(ConnectionMode.Follower, leader.Session.Peers[0].Mode);

        var clientJoined = await follower.WaitForTypeAsync(Protocol.MsgClientJoined);
        Assert.Equal("master", clientJoined["client"]!["connection_type"]!.GetValue<string>());
        var leaderId = clientJoined["client"]!["id"]!.GetValue<int>();
        Assert.Equal(leaderId, Protocol.GetInt(clientJoined, "user_id"));

        // Messages are relayed verbatim with the sender's id as origin.
        await leader.Session.SendAsync(Protocol.Message(Protocol.MsgKey, ("vk_code", 65), ("extended", false), ("pressed", true)));
        var key = await follower.WaitForTypeAsync(Protocol.MsgKey);
        Assert.Equal(65, Protocol.GetInt(key, "vk_code"));
        Assert.Equal(leaderId, Protocol.GetInt(key, "origin"));

        await follower.Session.SendAsync(JsonNode.Parse("""{"sequence":["Hello", ["EndUtteranceCommand", {}]],"priority":0,"type":"speak"}""")!.AsObject());
        var speak = await leader.WaitForTypeAsync(Protocol.MsgSpeak);
        Assert.Equal("Hello", speak["sequence"]![0]!.GetValue<string>());

        await leader.DisposeAsync();
        var left = await follower.WaitForTypeAsync(Protocol.MsgClientLeft);
        Assert.Equal(leaderId, left["client"]!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task LocalRelayRejectsWrongKey()
    {
        var port = TestUtil.FreePort();
        await using var server = await StartServerAsync(port, "right");
        await using var peer = new RecordingPeer(new ConnectionInfo("localhost", port, "wrong", ConnectionMode.Leader),
            new CertificatePolicy(PinnedFingerprint: server.Fingerprint), new TrustNothing());
        var error = await peer.WaitForTypeAsync(Protocol.MsgError);
        Assert.Equal("incorrect_password", Protocol.GetString(error, "message"));
    }

    [Fact]
    public async Task UnverifiableCertificateIsReportedWithFingerprint()
    {
        var port = TestUtil.FreePort();
        await using var server = await StartServerAsync(port, "k");
        var ex = await Assert.ThrowsAsync<CertificateUntrustedException>(() =>
            RelayConnection.ConnectAsync("localhost", port, CertificatePolicy.Verify, new TrustNothing(), CancellationToken.None));
        Assert.Equal(server.Fingerprint, ex.Fingerprint);

        // Once trusted for that address, verification passes (NVDA's trustedCertificates).
        var trust = new SettingsStore(TestUtil.TempDirectory("trust"));
        trust.Trust(ex.Address, ex.Fingerprint!);
        await using var ok = await RelayConnection.ConnectAsync("localhost", port, CertificatePolicy.Verify, trust, CancellationToken.None);
        Assert.Equal(server.Fingerprint, ok.Fingerprint);

        // A pinned fingerprint that does not match is refused.
        await Assert.ThrowsAsync<CertificateUntrustedException>(() =>
            RelayConnection.ConnectAsync("localhost", port, new CertificatePolicy(true, new string('0', 64)), new TrustNothing(), CancellationToken.None));
    }

    [Fact]
    public async Task ProtocolVersionOnePeersGetNoRoutingFields()
    {
        var port = TestUtil.FreePort();
        await using var server = await StartServerAsync(port, "k");
        await using var raw = await RelayConnection.ConnectAsync("localhost", port, new CertificatePolicy(PinnedFingerprint: server.Fingerprint), null, CancellationToken.None);
        // No protocol_version message: the server must treat this client as version 1.
        await raw.SendAsync(Protocol.Message(Protocol.MsgJoin, ("channel", "k"), ("connection_type", "slave")));
        var first = JsonNode.Parse((await raw.ReadLineAsync(CancellationToken.None))!)!.AsObject();
        Assert.Equal(Protocol.MsgChannelJoined, Protocol.TypeOf(first));

        await using var leader = new RecordingPeer(new ConnectionInfo("localhost", port, "k", ConnectionMode.Leader),
            new CertificatePolicy(PinnedFingerprint: server.Fingerprint), new TrustNothing());
        await leader.WaitForTypeAsync(Protocol.MsgChannelJoined);
        var joined = JsonNode.Parse((await raw.ReadLineAsync(CancellationToken.None))!)!.AsObject();
        Assert.Equal(Protocol.MsgClientJoined, Protocol.TypeOf(joined));
        Assert.Null(joined["client"]);
        Assert.Null(joined["origin"]);
        Assert.NotNull(joined["user_id"]);
    }

    /// <summary>A Python client using ssl.PROTOCOL_TLSv1_2, exactly like NVDA's transport, joins our relay.</summary>
    [Fact]
    public async Task PythonNvdaStyleClientInteroperates()
    {
        var port = TestUtil.FreePort();
        await using var server = await StartServerAsync(port, "pykey");
        await using var leader = new RecordingPeer(new ConnectionInfo("localhost", port, "pykey", ConnectionMode.Leader),
            new CertificatePolicy(PinnedFingerprint: server.Fingerprint), new TrustNothing());
        await leader.WaitForTypeAsync(Protocol.MsgChannelJoined);

        var script = Path.Combine(AppContext.BaseDirectory, "nvda_peer.py");
        using var py = Process.Start(new ProcessStartInfo("python", $"-I \"{script}\" 127.0.0.1 {port} pykey slave")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        try
        {
            var connected = await py.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains("__connected", connected);
            Assert.Contains("TLSv1.2", connected);
            var joined = JsonNode.Parse((await py.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!)!.AsObject();
            Assert.Equal(Protocol.MsgChannelJoined, Protocol.TypeOf(joined));
            Assert.Equal("master", joined["clients"]![0]!["connection_type"]!.GetValue<string>());

            await py.StandardInput.WriteLineAsync("""{"sequence": ["from python"], "priority": 0, "type": "speak"}""");
            await py.StandardInput.FlushAsync();
            var speak = await leader.WaitForAsync(m => Protocol.TypeOf(m) == Protocol.MsgSpeak, what: "speak from python");
            Assert.Equal("from python", speak["sequence"]![0]!.GetValue<string>());

            await leader.Session.SendAsync(Protocol.Message(Protocol.MsgSetClipboardText, ("text", "clip")));
            string? line;
            do
            {
                line = await py.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            } while (line is not null && !line.Contains("set_clipboard_text"));
            Assert.NotNull(line);
            Assert.Contains("\"origin\"", line);
        }
        finally
        {
            py.StandardInput.Close();
            if (!py.WaitForExit(5000))
                py.Kill();
        }
    }
}
