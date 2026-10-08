using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AssistBridge.Core;

/// <summary>Self-signed certificate used when this computer hosts a direct connection.</summary>
public static class LocalRelayCertificate
{
    private const int ValidityDays = 365;
    private const int RenewalThresholdDays = 30;

    /// <summary>Load the persisted certificate, creating or renewing it if needed.</summary>
    public static X509Certificate2 LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "LocalRelay.pfx.protected");
        if (File.Exists(path))
        {
            try
            {
                var pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                var existing = Import(pfx);
                if (existing.NotAfter.ToUniversalTime() - DateTime.UtcNow > TimeSpan.FromDays(RenewalThresholdDays) &&
                    existing.NotBefore.ToUniversalTime() <= DateTime.UtcNow && existing.HasPrivateKey)
                    return existing;
                existing.Dispose();
            }
            catch (Exception)
            {
                // Corrupt or unreadable; generate a new one below.
            }
        }
        using var rsa = RSA.Create(2048);
        var name = new X500DistinguishedName("CN=AssistBridge Local Relay, O=AssistBridge");
        var request = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(ValidityDays));
        var bytes = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        return Import(bytes);
    }

    private static X509Certificate2 Import(byte[] pfx) =>
        // SChannel cannot use ephemeral keys for server authentication, so import into the user key store.
        new(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
}

/// <summary>
/// A Remote Access relay running on this computer, for direct connections
/// (mirrors NVDA's server.LocalRelayServer, including protocol version 1 compatibility).
/// </summary>
public sealed class LocalRelayServer : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(300);

    private readonly X509Certificate2 _certificate;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _clientsLock = new();
    private readonly List<RelayClient> _clients = new();
    private int _nextId;
    private Task? _acceptLoop;
    private Task? _pingLoop;

    public LocalRelayServer(int port, string password, X509Certificate2 certificate)
    {
        Port = port;
        Password = password;
        _certificate = certificate;
        Fingerprint = RelayConnection.FingerprintOf(certificate);
        _listener = new TcpListener(IPAddress.IPv6Any, port);
        _listener.Server.DualMode = true;
    }

    public int Port { get; }
    public string Password { get; }
    public string Fingerprint { get; }

    public event Action<string>? Log;

    public int ClientCount
    {
        get
        {
            lock (_clientsLock)
                return _clients.Count(c => c.Authenticated);
        }
    }

    public void Start()
    {
        _listener.Start(16);
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _pingLoop = Task.Run(PingLoopAsync);
        Log?.Invoke($"Hosting a direct connection on port {Port}.");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(tcp));
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        tcp.NoDelay = true;
        RelayConnection.ConfigureKeepAlive(tcp.Client);
        var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var ssl = new SslStream(tcp.GetStream(), false);
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, handshakeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"A connection from {remote} failed the secure handshake: {ex.Message}");
            await ssl.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            return;
        }
        var client = new RelayClient(Interlocked.Increment(ref _nextId), new LineStream(ssl), tcp, remote);
        lock (_clientsLock)
            _clients.Add(client);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = await client.Lines.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                JsonObject? msg;
                try
                {
                    msg = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    Log?.Invoke($"Client {client.Id} sent malformed data and was disconnected.");
                    break;
                }
                if (msg is null || Protocol.TypeOf(msg) is not { } type)
                    continue;
                if (client.Authenticated)
                {
                    await SendToOthersAsync(client, msg).ConfigureAwait(false);
                    continue;
                }
                if (type == Protocol.MsgProtocolVersion)
                {
                    if (Protocol.GetInt(msg, "version") is { } v && v > 0)
                        client.ProtocolVersion = v;
                }
                else if (type == Protocol.MsgJoin)
                {
                    if (!await JoinAsync(client, msg).ConfigureAwait(false))
                        break;
                }
            }
        }
        catch (Exception) when (!_stop.IsCancellationRequested)
        {
            // Treated as a disconnect.
        }
        catch (OperationCanceledException)
        {
        }
        await RemoveAsync(client).ConfigureAwait(false);
    }

    private async Task<bool> JoinAsync(RelayClient client, JsonObject msg)
    {
        if (Protocol.GetString(msg, "channel") != Password)
        {
            Log?.Invoke($"Client {client.Id} ({client.Remote}) used an incorrect key.");
            await client.SendAsync(Protocol.Message(Protocol.MsgError, ("message", "incorrect_password")), null, null, null).ConfigureAwait(false);
            return false;
        }
        client.ConnectionType = Protocol.GetString(msg, "connection_type");
        client.Authenticated = true;
        List<RelayClient> others;
        lock (_clientsLock)
            others = _clients.Where(c => c != client && c.Authenticated).ToList();
        var clients = new JsonArray(others.Select(o => (JsonNode)o.AsJson()).ToArray());
        var ids = new JsonArray(others.Select(o => (JsonNode)JsonValue.Create(o.Id)).ToArray());
        await client.SendAsync(
            Protocol.Message(Protocol.MsgChannelJoined, ("channel", Password), ("user_ids", ids)),
            origin: null, clients: clients, client: null).ConfigureAwait(false);
        await SendToOthersAsync(client, Protocol.Message(Protocol.MsgClientJoined, ("user_id", client.Id)), client.AsJson()).ConfigureAwait(false);
        Log?.Invoke($"A computer joined the hosted session ({DescribeType(client.ConnectionType)}) from {client.Remote}.");
        return true;
    }

    private static string DescribeType(string? type) =>
        ConnectionModeExtensions.FromWire(type) == ConnectionMode.Leader ? "controlling" : "controlled";

    private async Task SendToOthersAsync(RelayClient sender, JsonObject msg, JsonObject? clientInfo = null)
    {
        List<RelayClient> targets;
        lock (_clientsLock)
            targets = _clients.Where(c => c != sender && c.Authenticated).ToList();
        var origin = Protocol.GetInt(msg, "origin") ?? sender.Id;
        foreach (var target in targets)
            await target.SendAsync((JsonObject)msg.DeepClone(), origin, null, clientInfo).ConfigureAwait(false);
    }

    private async Task RemoveAsync(RelayClient client)
    {
        bool removed;
        lock (_clientsLock)
            removed = _clients.Remove(client);
        await client.DisposeAsync().ConfigureAwait(false);
        if (removed && client.Authenticated)
        {
            Log?.Invoke($"A {DescribeType(client.ConnectionType)} computer left the hosted session.");
            await SendToOthersAsync(client, Protocol.Message(Protocol.MsgClientLeft, ("user_id", client.Id)), client.AsJson()).ConfigureAwait(false);
        }
    }

    private async Task PingLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PingInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            List<RelayClient> targets;
            lock (_clientsLock)
                targets = _clients.Where(c => c.Authenticated).ToList();
            foreach (var target in targets)
                await target.SendAsync(Protocol.Message(Protocol.MsgPing), null, null, null).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
            return;
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }
        List<RelayClient> all;
        lock (_clientsLock)
        {
            all = _clients.ToList();
            _clients.Clear();
        }
        foreach (var c in all)
            await c.DisposeAsync().ConfigureAwait(false);
        foreach (var task in new[] { _acceptLoop, _pingLoop })
        {
            if (task is null)
                continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        Log?.Invoke("Stopped hosting the direct connection.");
    }

    private sealed class RelayClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private int _disposed;

        public RelayClient(int id, LineStream lines, TcpClient tcp, string remote)
        {
            Id = id;
            Lines = lines;
            _tcp = tcp;
            Remote = remote;
        }

        public int Id { get; }
        public LineStream Lines { get; }
        public string Remote { get; }
        public bool Authenticated { get; set; }
        public string? ConnectionType { get; set; }
        public int ProtocolVersion { get; set; } = 1;

        public JsonObject AsJson() => new() { ["id"] = Id, ["connection_type"] = ConnectionType };

        /// <summary>Send, adding v2 routing fields only for clients that declared protocol version 2.</summary>
        public async Task SendAsync(JsonObject msg, int? origin, JsonArray? clients, JsonObject? client)
        {
            msg.Remove("origin");
            if (ProtocolVersion > 1)
            {
                if (origin is { } o && o != 0)
                    msg["origin"] = o;
                if (clients is { Count: > 0 })
                    msg["clients"] = clients.DeepClone();
                if (client is not null)
                    msg["client"] = client.DeepClone();
            }
            // Keep "type" last, matching NVDA's serializer output.
            if (msg.TryGetPropertyValue("type", out var type))
            {
                msg.Remove("type");
                msg["type"] = type;
            }
            try
            {
                await Lines.WriteAsync(Protocol.Encode(msg)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            await Lines.DisposeAsync().ConfigureAwait(false);
            _tcp.Dispose();
        }
    }
}
