using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccessRem.Core;

public enum NetState
{
    Connecting,
    Connected,
    Reconnecting,
    Failed,
    Closed,
}

/// <summary>Another computer in the channel, as reported by the relay server.</summary>
public sealed record PeerInfo(int Id, ConnectionMode? Mode);

/// <summary>
/// Joins a Remote Access channel on a relay server and keeps the connection alive,
/// reconnecting every five seconds after a drop exactly as NVDA's ConnectorThread does.
/// Events are raised on background threads.
/// </summary>
public sealed class NetworkSession : IAsyncDisposable
{
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly ICertificateTrust _trust;
    private readonly bool _retryInitialFailures;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _peersLock = new();
    private readonly Dictionary<int, PeerInfo> _peers = new();
    private RelayConnection? _connection;
    private Task? _loop;

    public NetworkSession(ConnectionInfo info, CertificatePolicy policy, ICertificateTrust trust, bool retryInitialFailures)
    {
        Info = info;
        Policy = policy;
        _trust = trust;
        _retryInitialFailures = retryInitialFailures;
    }

    public ConnectionInfo Info { get; }
    public CertificatePolicy Policy { get; }
    public NetState State { get; private set; } = NetState.Connecting;
    public int SuccessfulConnects { get; private set; }
    public int FailedAttempts { get; private set; }
    public string? ServerFingerprint { get; private set; }

    public event Action<NetState, string?>? StateChanged;
    /// <summary>Every message from the relay except pings: parsed object and the original line.</summary>
    public event Action<JsonObject, byte[]>? MessageReceived;
    public event Action<CertificateUntrustedException>? CertificateRejected;
    public event Action? PeersChanged;

    public IReadOnlyList<PeerInfo> Peers
    {
        get
        {
            lock (_peersLock)
                return _peers.Values.OrderBy(p => p.Id).ToList();
        }
    }

    public bool IsConnected => State == NetState.Connected;

    public void Start() => _loop ??= Task.Run(RunAsync);

    private void SetState(NetState state, string? detail = null)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            RelayConnection connection;
            try
            {
                SetState(SuccessfulConnects == 0 && FailedAttempts == 0 ? NetState.Connecting : NetState.Reconnecting);
                connection = await RelayConnection.ConnectAsync(Info.Host, Info.Port, Policy, _trust, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (CertificateUntrustedException ex)
            {
                SetState(NetState.Failed, ex.Message);
                CertificateRejected?.Invoke(ex);
                return;
            }
            catch (Exception ex)
            {
                FailedAttempts++;
                if (SuccessfulConnects == 0 && !_retryInitialFailures)
                {
                    SetState(NetState.Failed, Describe(ex));
                    return;
                }
                SetState(NetState.Reconnecting, Describe(ex));
                if (!await DelayAsync(ReconnectDelay, ct).ConfigureAwait(false))
                    break;
                continue;
            }

            _connection = connection;
            ServerFingerprint = connection.Fingerprint;
            string? dropReason = null;
            try
            {
                await connection.SendAsync(Protocol.Message(Protocol.MsgProtocolVersion, ("version", Protocol.ProtocolVersion)), ct).ConfigureAwait(false);
                await connection.SendAsync(Protocol.Message(Protocol.MsgJoin,
                    ("channel", Info.Key),
                    ("connection_type", Info.Mode.WireValue())), ct).ConfigureAwait(false);
                SuccessfulConnects++;
                FailedAttempts = 0;
                SetState(NetState.Connected);
                await ReadLoopAsync(connection, ct).ConfigureAwait(false);
                dropReason = "The server closed the connection.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                dropReason = Describe(ex);
            }
            finally
            {
                _connection = null;
                await connection.DisposeAsync().ConfigureAwait(false);
                ClearPeers();
            }
            if (ct.IsCancellationRequested)
                break;
            SetState(NetState.Reconnecting, dropReason);
            if (!await DelayAsync(ReconnectDelay, ct).ConfigureAwait(false))
                break;
        }
        SetState(NetState.Closed);
    }

    private async Task ReadLoopAsync(RelayConnection connection, CancellationToken ct)
    {
        while (true)
        {
            var line = await connection.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return;
            if (line.Length == 0)
                continue;
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (message is null)
                continue;
            var type = Protocol.TypeOf(message);
            if (type is null || type == Protocol.MsgPing)
                continue;
            TrackPeers(type, message);
            MessageReceived?.Invoke(message, line);
        }
    }

    private void TrackPeers(string type, JsonObject message)
    {
        var changed = false;
        lock (_peersLock)
        {
            switch (type)
            {
                case Protocol.MsgChannelJoined:
                    _peers.Clear();
                    if (message["clients"] is JsonArray clients)
                    {
                        foreach (var c in clients.OfType<JsonObject>())
                            AddPeer(c);
                    }
                    else if (message["user_ids"] is JsonArray ids)
                    {
                        foreach (var id in ids)
                            if (id is JsonValue v && v.TryGetValue<int>(out var i))
                                _peers[i] = new PeerInfo(i, null);
                    }
                    changed = true;
                    break;
                case Protocol.MsgClientJoined:
                    if (message["client"] is JsonObject joined)
                        AddPeer(joined);
                    else if (Protocol.GetInt(message, "user_id") is { } uid)
                        _peers[uid] = new PeerInfo(uid, null);
                    changed = true;
                    break;
                case Protocol.MsgClientLeft:
                    var leftId = message["client"] is JsonObject left ? Protocol.GetInt(left, "id") : Protocol.GetInt(message, "user_id");
                    if (leftId is { } lid)
                        _peers.Remove(lid);
                    changed = true;
                    break;
            }
        }
        if (changed)
            PeersChanged?.Invoke();
    }

    private void AddPeer(JsonObject client)
    {
        if (Protocol.GetInt(client, "id") is not { } id)
            return;
        _peers[id] = new PeerInfo(id, ConnectionModeExtensions.FromWire(Protocol.GetString(client, "connection_type")));
    }

    private void ClearPeers()
    {
        lock (_peersLock)
            _peers.Clear();
        PeersChanged?.Invoke();
    }

    /// <summary>Send a message if connected; silently dropped otherwise (as NVDA does).</summary>
    public async Task<bool> SendAsync(JsonObject message)
    {
        var connection = _connection;
        if (connection is null || State != NetState.Connected)
            return false;
        try
        {
            await connection.SendAsync(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> SendLineAsync(byte[] line)
    {
        var connection = _connection;
        if (connection is null || State != NetState.Connected)
            return false;
        try
        {
            await connection.SendLineAsync(line).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException se => se.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData =>
                "The server name could not be found. Check the host name and your internet connection.",
            System.Net.Sockets.SocketError.ConnectionRefused =>
                "The server refused the connection. Check the host and port, and that the server is running.",
            System.Net.Sockets.SocketError.TimedOut => "The connection timed out.",
            System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable =>
                "The network is unreachable. Check your internet connection.",
            _ => se.Message,
        },
        TimeoutException => "The connection timed out.",
        System.IO.IOException io when io.InnerException is System.Net.Sockets.SocketException inner => Describe(inner),
        _ => ex.Message,
    };

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
            _stop.Cancel();
        var connection = _connection;
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down regardless.
            }
        }
    }
}
