using AssistBridge.Core;

namespace AssistBridge.Tests;

/// <summary>Interoperability with the public relay that NVDA users use. Set ASSISTBRIDGE_SKIP_NETWORK=1 to skip.</summary>
public class PublicRelayTests
{
    private static bool Skip => Environment.GetEnvironmentVariable("ASSISTBRIDGE_SKIP_NETWORK") == "1";

    [Fact]
    public async Task GeneratesKeyAndRelaysThroughNvdaRemoteDotCom()
    {
        if (Skip)
            return;
        var trust = new TrustNothing();
        var key = await RelayTools.GenerateKeyAsync(Protocol.DefaultRelayHost, Protocol.DefaultPort, CertificatePolicy.Verify, trust, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(key));

        await using var follower = new RecordingPeer(new ConnectionInfo(Protocol.DefaultRelayHost, Protocol.DefaultPort, key, ConnectionMode.Follower), CertificatePolicy.Verify, trust);
        await follower.WaitForTypeAsync(Protocol.MsgChannelJoined, TimeSpan.FromSeconds(30));
        await using var leader = new RecordingPeer(new ConnectionInfo(Protocol.DefaultRelayHost, Protocol.DefaultPort, key, ConnectionMode.Leader), CertificatePolicy.Verify, trust);
        var joined = await leader.WaitForTypeAsync(Protocol.MsgChannelJoined, TimeSpan.FromSeconds(30));
        Assert.Contains(leader.Session.Peers, p => p.Mode == ConnectionMode.Follower);
        await follower.WaitForTypeAsync(Protocol.MsgClientJoined, TimeSpan.FromSeconds(30));

        await follower.Session.SendAsync(Protocol.Message(Protocol.MsgSpeak,
            ("sequence", new System.Text.Json.Nodes.JsonArray("AssistBridge interop test")), ("priority", 0)));
        var speak = await leader.WaitForTypeAsync(Protocol.MsgSpeak, TimeSpan.FromSeconds(30));
        Assert.Equal("AssistBridge interop test", speak["sequence"]![0]!.GetValue<string>());
    }
}
