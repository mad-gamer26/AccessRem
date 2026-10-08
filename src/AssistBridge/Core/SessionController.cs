using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AssistBridge.Backend;
using AssistBridge.Services;

namespace AssistBridge.Core;

public enum SessionPhase
{
    Idle,
    StartingBackend,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
}

public enum CertificateDecision
{
    Cancel,
    ConnectOnce,
    TrustAlways,
}

/// <summary>Decisions and notices that need the person at this computer.</summary>
public interface ISessionUi
{
    Task<CertificateDecision> AskTrustCertificateAsync(CertificateUntrustedException error);
    Task<bool> AskReplaceRunningNvdaAsync(RunningNvda running);
    void ShowMotd(string server, string message);
    void ShowError(string title, string message);
    string? GetClipboardText();
    void SetClipboardText(string text);
}

public enum TranscriptDirection
{
    /// <summary>Speech from the computer being controlled, heard here.</summary>
    Received,
    /// <summary>Speech from this computer, sent to the controlling computer.</summary>
    Sent,
}

public sealed record TranscriptEntry(DateTime Time, TranscriptDirection Direction, string Text)
{
    public string TimeText => Time.ToString("T");
    public string DirectionText => Direction == TranscriptDirection.Received ? "Remote" : "This computer";
    public override string ToString() => $"{TimeText} {DirectionText}: {Text}";
}

public sealed record EventEntry(DateTime Time, string Text)
{
    public string TimeText => Time.ToString("T");
    public override string ToString() => $"{TimeText} {Text}";
}

/// <summary>
/// Runs one Remote Access session: the bundled NVDA backend, the relay connection, and (for direct
/// connections) the local relay server. All public members must be used from the UI thread;
/// state changes are marshalled back to it.
/// </summary>
public sealed class SessionController : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly ISessionUi _ui;
    private readonly SynchronizationContext _ctx;
    private readonly SoundCues _sounds;
    private NvdaBackend? _backend;
    private NetworkSession? _network;
    private LocalRelayServer? _localServer;
    private RunningNvda? _replacedNvda;
    private CertificatePolicy _policy = CertificatePolicy.Verify;
    private bool _reportedFollowerFailure;
    private int _generation;

    private SessionPhase _phase;
    private ConnectionInfo? _info;
    private Machine? _machine;
    private bool _hosted;
    private string _status = "Not connected";
    private string? _statusDetail;
    private bool _sendingKeys;
    private bool _muted;
    private int _controllingPeers;
    private int _controlledPeers;
    private string _brailleText = "";
    private string? _externalAddress;
    private bool _receivingBraille;

    public SessionController(SettingsStore store, ISessionUi ui, SynchronizationContext ctx)
    {
        _store = store;
        _ui = ui;
        _ctx = ctx;
        _sounds = new SoundCues(() => WavesDirectory, () => _store.Current.PlaySounds);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A short message that should be presented to the user (status area, live region, notification).</summary>
    public event Action<string>? Announced;

    /// <summary>The bundled NVDA asked for the AssistBridge window to be brought forward.</summary>
    public event Action? ShowRequested;

    public ObservableCollection<TranscriptEntry> Transcript { get; } = new();
    public ObservableCollection<EventEntry> Events { get; } = new();

    public SessionPhase Phase { get => _phase; private set => Set(ref _phase, value); }
    public ConnectionInfo? Info { get => _info; private set => Set(ref _info, value); }
    public Machine? Machine { get => _machine; private set => Set(ref _machine, value); }
    public bool Hosted { get => _hosted; private set => Set(ref _hosted, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string? StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public bool SendingKeys { get => _sendingKeys; private set => Set(ref _sendingKeys, value); }
    public bool Muted { get => _muted; private set => Set(ref _muted, value); }
    public bool ReceivingBraille { get => _receivingBraille; private set => Set(ref _receivingBraille, value); }
    public int ControllingPeers { get => _controllingPeers; private set => Set(ref _controllingPeers, value); }
    public int ControlledPeers { get => _controlledPeers; private set => Set(ref _controlledPeers, value); }
    public string BrailleText { get => _brailleText; private set => Set(ref _brailleText, value); }
    public string? ExternalAddress { get => _externalAddress; set => Set(ref _externalAddress, value); }
    public string? BackendVersion => _backend?.NvdaVersion;
    internal int? BackendProcessId => _backend?.ProcessId;

    /// <summary>Test hook: ask NVDA to exit the way NVDA+Q would (it must refuse during a session).</summary>
    internal Task TryExitBackendAsync() => _backend?.SendControlAsync("try_exit") ?? Task.CompletedTask;
    public IReadOnlyList<(string Name, string Description)> AvailableSynths => _backend?.Synths ?? Array.Empty<(string, string)>();

    public ConnectionMode? Mode => Info?.Mode;
    public bool IsIdle => Phase == SessionPhase.Idle;
    public bool IsActive => Phase != SessionPhase.Idle;
    public bool IsConnected => Phase == SessionPhase.Connected;
    public bool IsLeader => IsActive && Info?.Mode == ConnectionMode.Leader;
    public bool IsFollower => IsActive && Info?.Mode == ConnectionMode.Follower;
    public bool CanControl => IsConnected && IsLeader && ControlledPeers > 0;
    public int OtherPeers => ControllingPeers + ControlledPeers;

    public string PeerSummary
    {
        get
        {
            if (!IsConnected)
                return "";
            var parts = new List<string>();
            if (ControlledPeers > 0)
                parts.Add(ControlledPeers == 1 ? "1 controlled computer" : $"{ControlledPeers} controlled computers");
            if (ControllingPeers > 0)
                parts.Add(ControllingPeers == 1 ? "1 controlling computer" : $"{ControllingPeers} controlling computers");
            return parts.Count == 0 ? "No one else is connected yet" : string.Join(", ", parts) + " connected";
        }
    }

    public string? WavesDirectory
    {
        get
        {
            var nvda = NvdaBackend.LocateBundledNvda(_store.Current.NvdaPathOverride);
            return nvda is null ? null : Path.Combine(nvda, "waves");
        }
    }

    public string? HostedFingerprint => _localServer?.Fingerprint;

    // Connecting

    public async Task ConnectAsync(ConnectionInfo info, Machine? machine, bool hostLocally)
    {
        if (IsActive)
        {
            _ui.ShowError("Already connected", "A Remote Access session is already in progress. Disconnect before starting a new session.");
            return;
        }
        var generation = ++_generation;
        Transcript.Clear();
        BrailleText = "";
        Info = info;
        Machine = machine;
        Hosted = hostLocally;
        _reportedFollowerFailure = false;
        _policy = info.Insecure ? new CertificatePolicy(Insecure: true) : CertificatePolicy.Verify;
        Phase = SessionPhase.StartingBackend;
        SetStatus("Starting speech engine…");
        Log($"Starting a session: {info.Mode.Describe()} on {(hostLocally ? $"this computer, port {info.Port}" : info.Address)}.");

        try
        {
            if (!await EnsureBackendAsync())
            {
                await ResetAsync("Cancelled");
                return;
            }
            if (generation != _generation)
                return;
            if (hostLocally)
            {
                var cert = LocalRelayCertificate.LoadOrCreate(AppPaths.CertificateDirectory);
                _localServer = new LocalRelayServer(info.Port, info.Key, cert);
                _localServer.Log += message => Post(() => Log(message));
                try
                {
                    _localServer.Start();
                }
                catch (SocketException ex)
                {
                    throw new InvalidOperationException($"Port {info.Port} could not be opened on this computer: {ex.Message}", ex);
                }
                _policy = new CertificatePolicy(PinnedFingerprint: _localServer.Fingerprint);
                OnPropertyChanged(nameof(HostedFingerprint));
            }
            await _backend!.SendControlAsync("session_start", new JsonObject
            {
                ["mode"] = info.Mode.WireValue(),
                ["hostname"] = info.Host,
                ["port"] = info.Port,
                ["key"] = info.Key,
            });
            StartNetwork(info);
            if (machine is not null)
            {
                machine.LastConnected = DateTimeOffset.Now;
                _store.Current.LastMachineId = machine.Id;
                _store.Save();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Connect failed: {ex}");
            await ResetAsync("Not connected");
            _ui.ShowError("Unable to connect", ex.Message);
        }
    }

    private async Task<bool> EnsureBackendAsync()
    {
        if (_backend is { IsReady: true })
        {
            await _backend.ConfigureAsync(_store.Current);
            return true;
        }
        var nvdaDir = NvdaBackend.LocateBundledNvda(_store.Current.NvdaPathOverride)
            ?? throw new BackendUnavailableException("The bundled copy of NVDA is missing. Reinstall AssistBridge, or choose an NVDA folder in Settings › Advanced.");
        var desktop = Environment.GetEnvironmentVariable("ASSISTBRIDGE_BACKEND_DESKTOP");
        if (string.IsNullOrEmpty(desktop))
        {
            var running = NvdaBackend.DetectRunningNvda(nvdaDir);
            if (running is { IsBundledCopy: false })
            {
                if (!_store.Current.ReplaceRunningNvdaWithoutAsking && !await _ui.AskReplaceRunningNvdaAsync(running))
                    return false;
                _replacedNvda = running;
                Log("NVDA was already running and will be restored when the session ends.");
            }
        }
        await StartBackendAsync(nvdaDir);
        return true;
    }

    private async Task StartBackendAsync(string nvdaDir)
    {
        var desktop = Environment.GetEnvironmentVariable("ASSISTBRIDGE_BACKEND_DESKTOP");
        var backend = new NvdaBackend(nvdaDir, AppPaths.NvdaConfigDirectory, string.IsNullOrEmpty(desktop) ? null : desktop);
        WireBackend(backend);
        _backend = backend;
        await backend.StartAsync(Info?.Mode, _store.Current, CancellationToken.None);
        await backend.ConfigureAsync(_store.Current);
        OnPropertyChanged(nameof(BackendVersion));
        OnPropertyChanged(nameof(AvailableSynths));
        Log($"Speech engine ready (NVDA {backend.NvdaVersion}{(backend.UsesUiAccess ? ", installed, secure screens supported" : "")}).");
        UpdateSecureScreenRegistration(backend, sessionActive: true);
        if (backend.UsesUiAccess && Info?.Mode == ConnectionMode.Follower)
        {
            var error = await SasHelper.SetSecureScreenSpeechAsync(_store.Current.SpeakLocallyWhenControlled);
            if (error is not null)
                Log($"Unable to update secure screen speech: {error}");
        }
    }

    /// <summary>
    /// Decide which screen reader Windows starts on User Account Control and sign-in screens during the session.
    /// Installed mode: AssistBridge's NVDA, which joins the session through NVDA's secure desktop handshake.
    /// A separately installed NVDA is held back while AssistBridge's copy is in charge: it could not join the
    /// session and would only speak aloud on this computer.
    /// </summary>
    private void UpdateSecureScreenRegistration(NvdaBackend? backend, bool sessionActive)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASSISTBRIDGE_BACKEND_DESKTOP")))
            return; // Isolated test desktop: never touch the real Ease of Access state.
        try
        {
            var states = new Dictionary<string, int>();
            if (backend?.UsesUiAccess == true || (!sessionActive && SystemInstall.IsRunningInstalledCopy))
                states[SystemInstall.AtName] = sessionActive && Info?.Mode == ConnectionMode.Follower ? 3 : 2;
            if (sessionActive && SystemInstall.IsAtRegistered(SystemInstall.NvdaAtName))
                states[SystemInstall.NvdaAtName] = 2;
            AtBroker.Notify(states);
        }
        catch (Exception ex)
        {
            Log($"Unable to update secure screen settings: {ex.Message}");
        }
    }

    // Recovery when the bundled NVDA stops during a session

    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(2);
    private const int MaxRestartsInWindow = 3;
    private readonly List<DateTime> _restartTimes = new();
    private readonly Dictionary<int, byte[]> _leaderBrailleInfo = new();

    private async Task RecoverBackendAsync(NvdaBackend failed, string reason, bool planned)
    {
        if (failed != _backend)
            return;
        _backend = null;
        await failed.DisposeAsync();
        if (!IsActive)
            return;
        AppLog.Write(reason);
        var now = DateTime.UtcNow;
        _restartTimes.RemoveAll(t => now - t > RestartWindow);
        if (!planned && _restartTimes.Count >= MaxRestartsInWindow)
        {
            await DisconnectAsync(silent: true);
            _ui.ShowError("Session ended",
                $"{reason} It stopped {MaxRestartsInWindow} times within {RestartWindow.TotalMinutes:0} minutes, so the session was disconnected. Details are in {failed.LogFile}.");
            return;
        }
        if (!planned)
            _restartTimes.Add(now);
        Log(planned ? "Restarting the speech engine." : $"{reason} Restarting it; the connection stays open.");
        Announce(planned ? "Restarting speech" : "Speech stopped unexpectedly. Restarting");
        SendingKeys = false;
        try
        {
            var nvdaDir = NvdaBackend.LocateBundledNvda(_store.Current.NvdaPathOverride)
                ?? throw new BackendUnavailableException("The bundled copy of NVDA is missing.");
            await StartBackendAsync(nvdaDir);
            await RestoreBackendSessionAsync();
            Log("The speech engine was restarted and the session restored.");
            Announce("Speech restored");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Restarting the speech engine failed: {ex}");
            await DisconnectAsync(silent: true);
            _ui.ShowError("Session ended", $"The speech engine could not be restarted, so the session was disconnected.\n\n{ex.Message}");
        }
    }

    /// <summary>Bring a fresh NVDA up to date with the session that is already running on the network.</summary>
    private async Task RestoreBackendSessionAsync()
    {
        var backend = _backend;
        var info = Info;
        if (backend is null || info is null)
            return;
        await backend.SendControlAsync("session_start", new JsonObject
        {
            ["mode"] = info.Mode.WireValue(),
            ["hostname"] = info.Host,
            ["port"] = info.Port,
            ["key"] = info.Key,
        });
        if (_network is not { IsConnected: true } network)
            return;
        await backend.SendControlAsync("net_state", new JsonObject { ["connected"] = true });
        // Replay who is in the channel, as the relay does when joining.
        var peers = network.Peers.Where(p => p.Mode is not null).ToList();
        var joined = Protocol.Message(Protocol.MsgChannelJoined,
            ("channel", info.Key),
            ("user_ids", new JsonArray(peers.Select(p => (JsonNode)JsonValue.Create(p.Id)).ToArray())),
            ("clients", new JsonArray(peers.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["connection_type"] = p.Mode!.Value.WireValue() }).ToArray())));
        await backend.SendProtocolLineAsync(Protocol.Encode(joined)[..^1]);
        // And the controlling computers' braille display sizes.
        List<byte[]> brailleInfo;
        lock (_leaderBrailleInfo)
            brailleInfo = _leaderBrailleInfo.Values.ToList();
        foreach (var line in brailleInfo)
            await backend.SendProtocolLineAsync(line);
    }

    private void WireBackend(NvdaBackend backend)
    {
        backend.ProtocolMessage += (line, msg) => OnBackendProtocolMessage(backend, line, msg);
        backend.StateChanged += state => Post(() =>
        {
            if (backend != _backend)
                return;
            SendingKeys = state.SendingKeys;
            Muted = state.Muted;
            ReceivingBraille = state.ReceivingBraille;
        });
        backend.Announcement += text => Post(() =>
        {
            Announce(text);
            Log(text);
        });
        backend.Cue += (name, wave, message) => Post(() =>
        {
            _sounds.Play(wave);
            if (!string.IsNullOrEmpty(message))
                Announce(message);
        });
        backend.Request += action => Post(async () =>
        {
            switch (action)
            {
                case "push_clipboard":
                    await PushClipboardAsync();
                    break;
                case "show":
                    ShowRequested?.Invoke();
                    break;
                case "restart_backend":
                    await RecoverBackendAsync(backend, "NVDA asked to restart.", planned: true);
                    break;
            }
        });
        backend.Exited += reason => Post(() => RecoverBackendAsync(backend, reason, planned: false));
    }

    private void StartNetwork(ConnectionInfo info)
    {
        // The controlled computer keeps retrying in the background, as NVDA does for unattended use.
        var network = new NetworkSession(info, _policy, _store, retryInitialFailures: info.Mode == ConnectionMode.Follower || Hosted);
        network.StateChanged += (state, detail) => Post(() => OnNetworkState(network, state, detail));
        network.MessageReceived += (msg, line) => OnNetworkMessage(network, msg, line);
        network.PeersChanged += () => Post(() => OnPeersChanged(network));
        network.CertificateRejected += ex => Post(() => OnCertificateRejected(network, ex));
        _network = network;
        Phase = SessionPhase.Connecting;
        SetStatus($"Connecting to {DescribeServer()}…");
        network.Start();
    }

    private string DescribeServer() => Hosted ? "the session hosted on this computer" : Info?.Address ?? "the server";

    private async void OnNetworkState(NetworkSession network, NetState state, string? detail)
    {
        if (network != _network)
            return;
        switch (state)
        {
            case NetState.Connected:
                var first = network.SuccessfulConnects == 1;
                Phase = SessionPhase.Connected;
                SetStatus(Info!.Mode == ConnectionMode.Leader ? "Connected — ready to control" : "Connected — waiting for a helper", null);
                if (_backend is not null)
                    await _backend.SendControlAsync("net_state", new JsonObject { ["connected"] = true });
                if (Info.Mode == ConnectionMode.Leader)
                {
                    _sounds.Play("connected");
                    Announce(first ? "Connected" : "Reconnected");
                }
                else
                {
                    _sounds.Play("controlled");
                    Announce(first ? "Connected as controlled computer" : "Reconnected as controlled computer");
                }
                Log(first ? $"Connected to {DescribeServer()}." : $"Reconnected to {DescribeServer()}.");
                break;
            case NetState.Reconnecting:
                if (Phase == SessionPhase.Connected)
                {
                    _sounds.Play("disconnected");
                    Announce("Connection lost. Reconnecting");
                    Log($"Connection lost: {detail}");
                }
                else if (Info?.Mode == ConnectionMode.Follower && !_reportedFollowerFailure && network.SuccessfulConnects == 0)
                {
                    _reportedFollowerFailure = true;
                    Announce("Unable to connect to the Remote Access server. Retrying");
                    Log($"Unable to connect: {detail} Retrying every 5 seconds.");
                }
                Phase = SessionPhase.Reconnecting;
                SetStatus("Reconnecting…", detail);
                if (_backend is not null)
                    await _backend.SendControlAsync("net_state", new JsonObject { ["connected"] = false });
                break;
            case NetState.Failed:
                if (_network == network && network.SuccessfulConnects == 0 && !_awaitingCertificateDecision)
                {
                    Log($"Unable to connect: {detail}");
                    await DisconnectAsync(silent: true);
                    _ui.ShowError("Error connecting", $"Unable to connect to the remote computer.\n\n{detail}");
                }
                break;
        }
    }

    private bool _awaitingCertificateDecision;

    private async void OnCertificateRejected(NetworkSession network, CertificateUntrustedException ex)
    {
        if (network != _network)
            return;
        _awaitingCertificateDecision = true;
        Log($"The server's certificate could not be verified (fingerprint {ex.Fingerprint}).");
        CertificateDecision decision;
        try
        {
            decision = await _ui.AskTrustCertificateAsync(ex);
        }
        finally
        {
            _awaitingCertificateDecision = false;
        }
        if (network != _network)
            return;
        await network.DisposeAsync();
        _network = null;
        switch (decision)
        {
            case CertificateDecision.TrustAlways when ex.Fingerprint is not null:
                _store.Trust(ex.Address, ex.Fingerprint);
                _policy = CertificatePolicy.Verify;
                Log($"Trusted the certificate for {ex.Address}.");
                StartNetwork(Info!);
                break;
            case CertificateDecision.ConnectOnce when ex.Fingerprint is not null:
                _policy = new CertificatePolicy(Insecure: true, PinnedFingerprint: ex.Fingerprint);
                StartNetwork(Info!);
                break;
            default:
                await DisconnectAsync(silent: true);
                break;
        }
    }

    private void OnPeersChanged(NetworkSession network)
    {
        if (network != _network)
            return;
        var peers = network.Peers;
        var controlling = peers.Count(p => p.Mode == ConnectionMode.Leader);
        var controlled = peers.Count(p => p.Mode == ConnectionMode.Follower);
        if (IsConnected)
        {
            if (controlling > ControllingPeers)
                Log(Info?.Mode == ConnectionMode.Follower ? "A helper connected and can now control this computer." : "Another controlling computer joined.");
            else if (controlling < ControllingPeers)
                Log(Info?.Mode == ConnectionMode.Follower ? "The helper disconnected." : "A controlling computer left.");
            if (controlled > ControlledPeers)
                Log(Info?.Mode == ConnectionMode.Leader ? "The remote computer is connected and ready to be controlled." : "Another controlled computer joined.");
            else if (controlled < ControlledPeers)
                Log(Info?.Mode == ConnectionMode.Leader ? "The remote computer disconnected." : "A controlled computer left.");
        }
        var hadNoFollowers = ControlledPeers == 0;
        ControllingPeers = controlling;
        ControlledPeers = controlled;
        if (IsLeader && hadNoFollowers && controlled > 0 && _store.Current.ControlRemoteOnConnect && !SendingKeys)
            _ = SetControlAsync(remote: true);
    }

    // Message routing

    /// <summary>Network → here. Runs on a network thread.</summary>
    private void OnNetworkMessage(NetworkSession network, JsonObject msg, byte[] line)
    {
        var type = Protocol.TypeOf(msg);
        switch (type)
        {
            case Protocol.MsgMotd:
                var motd = Protocol.GetString(msg, "motd") ?? "";
                var force = Protocol.GetBool(msg, "force_display");
                Post(() =>
                {
                    if (force || _store.ShouldDisplayMotd(network.Info.Host, network.Info.Port, motd))
                        _ui.ShowMotd(network.Info.Address, motd);
                });
                return;
            case Protocol.MsgVersionMismatch:
                Post(async () =>
                {
                    await DisconnectAsync(silent: true);
                    _ui.ShowError("Incompatible server", "The Remote Access server you have connected to is not compatible with this version of NVDA Remote Access. Please use a different server.");
                });
                return;
            case Protocol.MsgError:
                var error = Protocol.GetString(msg, "message") ?? "unknown error";
                Post(async () =>
                {
                    Log($"The server reported an error: {error}");
                    if (error == "incorrect_password")
                    {
                        await DisconnectAsync(silent: true);
                        _ui.ShowError("Key not accepted", "The key was not accepted by the computer hosting this session. Check the key and try again.");
                    }
                });
                return;
            case Protocol.MsgGenerateKey:
                return;
            case Protocol.MsgSetClipboardText:
                var text = Protocol.GetString(msg, "text");
                if (text is not null)
                    Post(() =>
                    {
                        _ui.SetClipboardText(text);
                        _sounds.Play("clipboardReceive");
                        Announce("Clipboard received");
                        Log($"Received clipboard text ({text.Length} characters).");
                    });
                return;
            case Protocol.MsgSendSas:
                if (Info?.Mode == ConnectionMode.Follower)
                    Post(HandleIncomingSas);
                return;
            case Protocol.MsgSetBrailleInfo when Info?.Mode == ConnectionMode.Follower:
                // Remembered so a restarted NVDA can be told the helpers' braille display sizes.
                if (Protocol.GetInt(msg, "origin") is { } brailleOrigin)
                    lock (_leaderBrailleInfo)
                        _leaderBrailleInfo[brailleOrigin] = line;
                break;
            case Protocol.MsgClientLeft:
                var leftId = msg["client"] is JsonObject leftClient ? Protocol.GetInt(leftClient, "id") : Protocol.GetInt(msg, "user_id");
                if (leftId is { } id)
                    lock (_leaderBrailleInfo)
                        _leaderBrailleInfo.Remove(id);
                break;
            case Protocol.MsgSpeak when Info?.Mode == ConnectionMode.Leader:
                AddTranscript(TranscriptDirection.Received, msg);
                break;
            case Protocol.MsgDisplay when Info?.Mode == ConnectionMode.Leader:
                UpdateBraille(msg);
                break;
        }
        var backend = _backend;
        if (backend is not null)
            _ = backend.SendProtocolLineAsync(line);
    }

    /// <summary>NVDA → network. Runs on the backend link thread.</summary>
    private void OnBackendProtocolMessage(NvdaBackend backend, byte[] line, JsonObject msg)
    {
        if (backend != _backend)
            return;
        var type = Protocol.TypeOf(msg);
        if (Info?.Mode == ConnectionMode.Follower)
        {
            if (type == Protocol.MsgSpeak)
                AddTranscript(TranscriptDirection.Sent, msg);
            else if (type == Protocol.MsgDisplay)
                UpdateBraille(msg);
        }
        var network = _network;
        if (network is not null)
            _ = network.SendLineAsync(line);
    }

    private async void HandleIncomingSas()
    {
        if (!_store.Current.AllowCtrlAltDel)
        {
            Log("The helper asked for Control+Alt+Delete, but this is turned off in Settings.");
            return;
        }
        Log("The helper sent Control+Alt+Delete.");
        var error = await SasHelper.TrySendAsync();
        if (error is not null)
        {
            Announce("Unable to trigger control+alt+delete");
            Log(error);
        }
    }

    private void AddTranscript(TranscriptDirection direction, JsonObject msg)
    {
        if (!_store.Current.KeepTranscript || msg["sequence"] is not JsonArray sequence)
            return;
        var text = SpeechText(sequence);
        if (string.IsNullOrWhiteSpace(text))
            return;
        Post(() =>
        {
            Transcript.Add(new TranscriptEntry(DateTime.Now, direction, text));
            var limit = Math.Max(50, _store.Current.TranscriptLimit);
            while (Transcript.Count > limit)
                Transcript.RemoveAt(0);
        });
    }

    /// <summary>The spoken text of a serialised NVDA speech sequence (strings; commands are [name, fields]).</summary>
    public static string SpeechText(JsonArray sequence)
    {
        var sb = new StringBuilder();
        foreach (var item in sequence)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s))
            {
                s = s.Trim();
                if (s.Length == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(s);
            }
        }
        return sb.ToString();
    }

    private void UpdateBraille(JsonObject msg)
    {
        if (msg["cells"] is not JsonArray cells)
            return;
        var sb = new StringBuilder(cells.Count);
        foreach (var cell in cells)
            if (cell is JsonValue v && v.TryGetValue<int>(out var dots))
                sb.Append((char)(0x2800 + (dots & 0xFF)));
        var text = sb.ToString().TrimEnd('⠀');
        Post(() => BrailleText = text);
    }

    // Commands

    public async Task ToggleControlAsync()
    {
        if (_backend is null || !IsLeader)
            return;
        await _backend.SendControlAsync("toggle_control");
    }

    public async Task SetControlAsync(bool remote)
    {
        if (_backend is null || !IsLeader)
            return;
        await _backend.SendControlAsync("set_control", new JsonObject { ["remote"] = remote });
    }

    public async Task ToggleMuteAsync()
    {
        if (_backend is null || !IsLeader)
            return;
        await _backend.SendControlAsync("toggle_mute");
    }

    public async Task PushClipboardAsync()
    {
        if (_network is not { IsConnected: true } network)
        {
            Announce("Not connected");
            return;
        }
        if (OtherPeers < 1)
        {
            Announce("No one else is connected");
            return;
        }
        var text = _ui.GetClipboardText();
        if (string.IsNullOrEmpty(text))
        {
            Announce("The clipboard does not contain text");
            return;
        }
        if (await network.SendAsync(Protocol.Message(Protocol.MsgSetClipboardText, ("text", text))))
        {
            _sounds.Play("clipboardPush");
            Announce("Clipboard sent");
            Log($"Sent clipboard text ({text.Length} characters).");
        }
        else
        {
            Announce("Unable to send clipboard");
        }
    }

    public async Task SendSasAsync()
    {
        if (_network is not { IsConnected: true } network || !IsLeader)
        {
            Announce(IsFollower ? "Not the controlling computer" : "Not connected");
            return;
        }
        await network.SendAsync(Protocol.Message(Protocol.MsgSendSas));
        Log("Sent Control+Alt+Delete to the remote computer.");
        Announce("Sent control+alt+delete");
    }

    /// <summary>A link the other person can open to join this session in the opposite role.</summary>
    public string? GetShareLink()
    {
        if (Info is null)
            return null;
        if (!Hosted)
            return Info.GetUrlToConnect();
        var host = ExternalAddress ?? GuessLocalAddress() ?? "localhost";
        return (Info with { Host = host }).GetUrlToConnect();
    }

    public string? GetInvitationText()
    {
        if (Info is null)
            return null;
        var link = GetShareLink();
        var role = Info.Mode == ConnectionMode.Follower ? "help me by controlling my computer" : "let me control your computer";
        var server = Hosted ? (ExternalAddress ?? GuessLocalAddress() ?? "this computer") + (Info.Port == Protocol.DefaultPort ? "" : $", port {Info.Port}") : Info.Address;
        var mode = Info.Mode == ConnectionMode.Follower ? "Control another computer" : "Allow this computer to be controlled";
        return $"Please {role} with NVDA Remote Access.\r\n\r\nOpen this link: {link}\r\n\r\nOr connect manually in NVDA (NVDA+Alt+R):\r\n  Mode: {mode}\r\n  Host: {server}\r\n  Key: {Info.Key}";
    }

    public static string? GuessLocalAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public async Task OpenNvdaSettingsAsync(string panel)
    {
        if (_backend is not { IsReady: true })
        {
            _ui.ShowError("Speech engine not running", "NVDA's speech and braille settings can be changed while a session is active.");
            return;
        }
        await _backend.SendControlAsync("open_settings", new JsonObject { ["panel"] = panel });
    }

    public async Task ApplySettingsAsync()
    {
        if (_backend is { IsReady: true })
            await _backend.ConfigureAsync(_store.Current);
    }

    /// <summary>Speak text through the bundled NVDA (test button in Settings).</summary>
    public async Task SpeakTestAsync(string text, bool asLocalOutput = false)
    {
        if (_backend is { IsReady: true })
            await _backend.SendControlAsync("speak", new JsonObject { ["text"] = text, ["asLocalOutput"] = asLocalOutput });
    }

    // Disconnecting

    public async Task DisconnectAsync(bool silent = false)
    {
        if (!IsActive)
            return;
        _generation++;
        Phase = SessionPhase.Disconnecting;
        SetStatus("Disconnecting…");
        var wasConnected = _network?.SuccessfulConnects > 0;
        await ResetAsync("Not connected");
        if (!silent || wasConnected)
        {
            _sounds.Play("disconnected");
            Announce("Disconnected");
        }
        Log("Disconnected.");
    }

    private async Task ResetAsync(string status)
    {
        var network = _network;
        _network = null;
        var server = _localServer;
        _localServer = null;
        var backend = _backend;
        _backend = null;
        if (backend is { IsReady: true })
            await backend.SendControlAsync("session_stop", new JsonObject { ["silent"] = true });
        if (network is not null)
            await network.DisposeAsync();
        if (server is not null)
            await server.DisposeAsync();
        // Stop the bundled NVDA between sessions so it never affects this computer while idle.
        if (backend is not null)
        {
            await backend.DisposeAsync();
            UpdateSecureScreenRegistration(null, sessionActive: false);
        }
        lock (_leaderBrailleInfo)
            _leaderBrailleInfo.Clear();
        _restartTimes.Clear();
        if (_replacedNvda is { } replaced)
        {
            _replacedNvda = null;
            if (_store.Current.RestoreReplacedNvda)
            {
                try
                {
                    NvdaBackend.RestartReplacedNvda(replaced);
                    Log("Restarted the NVDA that was running before the session.");
                }
                catch (Exception ex)
                {
                    Log($"Unable to restart NVDA: {ex.Message}");
                }
            }
        }
        SendingKeys = false;
        Muted = false;
        ReceivingBraille = false;
        ControllingPeers = 0;
        ControlledPeers = 0;
        BrailleText = "";
        Phase = SessionPhase.Idle;
        Info = null;
        Machine = null;
        Hosted = false;
        OnPropertyChanged(nameof(HostedFingerprint));
        SetStatus(status);
    }

    // Helpers

    private void SetStatus(string status, string? detail = null)
    {
        Status = status;
        StatusDetail = detail;
    }

    public void Log(string text)
    {
        Events.Add(new EventEntry(DateTime.Now, text));
        while (Events.Count > 500)
            Events.RemoveAt(0);
        AppLog.Write(text);
    }

    private void Announce(string text)
    {
        Announced?.Invoke(text);
    }

    private void Post(Action action) => _ctx.Post(_ => action(), null);

    private void Post(Func<Task> action) => _ctx.Post(async _ =>
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Unhandled error: {ex}");
        }
    }, null);

    private static readonly string[] DerivedProperties =
    {
        nameof(IsActive), nameof(IsConnected), nameof(IsIdle), nameof(CanControl),
        nameof(IsLeader), nameof(IsFollower), nameof(Mode), nameof(PeerSummary),
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
        foreach (var derived in DerivedProperties)
            OnPropertyChanged(derived);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async ValueTask DisposeAsync()
    {
        if (IsActive)
            await ResetAsync("Not connected");
    }
}
