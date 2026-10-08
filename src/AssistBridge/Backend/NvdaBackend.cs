using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssistBridge.Core;

namespace AssistBridge.Backend;

/// <summary>State reported by the backend add-on.</summary>
public sealed record BackendState(
    ConnectionMode? Mode,
    bool SendingKeys,
    bool Muted,
    bool ReceivingBraille,
    string? BrailleDisplay,
    int BrailleCells,
    string? Synth);

/// <summary>An NVDA that is already running on this computer, which the bundled copy would replace.</summary>
public sealed record RunningNvda(int ProcessId, string? ExecutablePath, bool IsBundledCopy);

public sealed class BackendUnavailableException : Exception
{
    public BackendUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Launches the bundled portable NVDA (the RIM approach) and talks to the AssistBridge backend add-on
/// running inside it over a loopback link authenticated with a per-launch token.
/// </summary>
public sealed class NvdaBackend : IAsyncDisposable
{
    public const string AddonName = "assistBridgeBackend";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private readonly string _nvdaDirectory;
    private readonly string _configDirectory;
    private readonly string? _desktopName;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private LineStream? _link;
    private IntPtr _job;
    private IntPtr _desktop;
    private Process? _process;
    private Task? _readLoop;
    private int _exitedRaised;
    private volatile bool _stopping;

    public NvdaBackend(string nvdaDirectory, string configDirectory, string? desktopName = null)
    {
        _nvdaDirectory = nvdaDirectory;
        _configDirectory = configDirectory;
        _desktopName = desktopName;
    }

    public string NvdaDirectory => _nvdaDirectory;
    public string LogFile => Path.Combine(AppPaths.LogDirectory, "nvda-backend.log");
    public bool IsReady { get; private set; }
    public string? NvdaVersion { get; private set; }
    public IReadOnlyList<(string Name, string Description)> Synths { get; private set; } = Array.Empty<(string, string)>();
    public BackendState? State { get; private set; }

    /// <summary>Remote Access protocol messages produced by NVDA, to be sent to the network. (line, parsed)</summary>
    public event Action<byte[], JsonObject>? ProtocolMessage;
    public event Action<BackendState>? StateChanged;
    public event Action<string>? Announcement;
    public event Action<string, string?, string?>? Cue;
    public event Action<string>? Request;
    public event Action<string>? Exited;

    /// <summary>Find the bundled NVDA next to the app (or at an override path).</summary>
    public static string? LocateBundledNvda(string? overridePath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(overridePath))
            candidates.Add(overridePath);
        candidates.Add(Path.Combine(AppPaths.AppDirectory, "nvda"));
        // Development layout: dist folder produced by the build script.
        var dir = new DirectoryInfo(AppPaths.AppDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "dist", "AssistBridge", "nvda"));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "nvda_noUIAccess.exe")) || File.Exists(Path.Combine(c, "nvda.exe")));
    }

    public static string? LocateAddonSource()
    {
        var candidates = new List<string> { Path.Combine(AppPaths.AppDirectory, "backend-addon") };
        var dir = new DirectoryInfo(AppPaths.AppDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "addon"));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "manifest.ini")) &&
                                               Directory.Exists(Path.Combine(c, "globalPlugins", "assistBridge")));
    }

    /// <summary>Detect an NVDA already running on this desktop. Starting another one would replace it.</summary>
    public static RunningNvda? DetectRunningNvda(string? bundledDirectory)
    {
        var hwnd = Native.FindWindow("wxWindowClassNR", "NVDA");
        if (hwnd == IntPtr.Zero)
            return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        string? path = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            path = p.MainModule?.FileName;
        }
        catch (Exception)
        {
            // Access may be denied (for example an elevated NVDA); the path is only used to restart it.
        }
        var bundled = path is not null && bundledDirectory is not null &&
                      Path.GetFullPath(path).StartsWith(Path.GetFullPath(bundledDirectory), StringComparison.OrdinalIgnoreCase);
        return new RunningNvda((int)pid, path, bundled);
    }

    /// <summary>Restart an NVDA that was replaced by the bundled copy.</summary>
    public static void RestartReplacedNvda(RunningNvda replaced)
    {
        var exe = replaced.ExecutablePath;
        if (exe is null)
            return;
        // Installed copies must be started through nvda.exe, which selects the UI Access build.
        var launcher = Path.Combine(Path.GetDirectoryName(exe)!, "nvda.exe");
        if (File.Exists(launcher))
            exe = launcher;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var exe = Path.Combine(_nvdaDirectory, "nvda_noUIAccess.exe");
        if (!File.Exists(exe))
            exe = Path.Combine(_nvdaDirectory, "nvda.exe");
        if (!File.Exists(exe))
            throw new BackendUnavailableException($"The bundled copy of NVDA was not found in {_nvdaDirectory}.");

        PrepareConfiguration();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        Directory.CreateDirectory(AppPaths.LogDirectory);
        var args = new[]
        {
            "--minimal",
            "--no-sr-flag",
            "--config-path", _configDirectory,
            "--log-file", LogFile,
            "--log-level", "20",
        };
        Launch(exe, args, port, token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(StartupTimeout);
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var link = new LineStream(client.GetStream());
                var line = await link.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                var hello = line is null ? null : JsonNode.Parse(line) as JsonObject;
                if (hello is not null && Protocol.TypeOf(hello) == "bridge_hello" &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Protocol.GetString(hello, "token") ?? ""), Encoding.UTF8.GetBytes(token)))
                {
                    _link = link;
                    NvdaVersion = Protocol.GetString(hello, "nvdaVersion");
                    if (hello["synths"] is JsonArray synths)
                    {
                        Synths = synths.OfType<JsonArray>()
                            .Where(a => a.Count >= 2)
                            .Select(a => (a[0]!.GetValue<string>(), a[1]!.GetValue<string>()))
                            .ToList();
                    }
                    break;
                }
                await link.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await KillAsync().ConfigureAwait(false);
            throw new BackendUnavailableException($"The bundled copy of NVDA did not start within {StartupTimeout.TotalSeconds:0} seconds. See {LogFile} for details.");
        }
        finally
        {
            _listener.Stop();
            _listener = null;
        }
        IsReady = true;
        _readLoop = Task.Run(ReadLoopAsync);
        AppLog.Write($"Bundled NVDA {NvdaVersion} is ready (pid {_process?.Id}).");
    }

    /// <summary>Lay out the portable configuration: install the backend add-on and keep it current.</summary>
    private void PrepareConfiguration()
    {
        Directory.CreateDirectory(_configDirectory);
        var addonSource = LocateAddonSource()
            ?? throw new BackendUnavailableException("The AssistBridge backend add-on is missing from this installation.");
        var addonsDir = Path.Combine(_configDirectory, "addons");
        var target = Path.Combine(addonsDir, AddonName);
        Directory.CreateDirectory(addonsDir);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        CopyDirectory(addonSource, target);
        // Stale caches from an older add-on version must not be used.
        foreach (var cache in Directory.EnumerateDirectories(target, "__pycache__", SearchOption.AllDirectories).ToList())
            Directory.Delete(cache, true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(dir) == "__pycache__")
                continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private void Launch(string exe, string[] args, int port, string token)
    {
        // The child inherits our environment; the add-on reads the link details from it.
        Environment.SetEnvironmentVariable("ASSISTBRIDGE_PORT", port.ToString());
        Environment.SetEnvironmentVariable("ASSISTBRIDGE_TOKEN", token);
        var commandLine = new StringBuilder(Native.Quote(exe));
        foreach (var arg in args)
            commandLine.Append(' ').Append(Native.Quote(arg));

        _job = Native.CreateJobObject(IntPtr.Zero, null);
        if (_job != IntPtr.Zero)
        {
            // If AssistBridge exits or crashes, Windows closes the job and the bundled NVDA goes with it.
            var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info));
        }

        var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>() };
        if (!string.IsNullOrEmpty(_desktopName))
        {
            // Developer/testing aid: run the backend on an isolated desktop so it cannot
            // replace a screen reader that is already running on the interactive desktop.
            _desktop = Native.CreateDesktop(_desktopName, IntPtr.Zero, IntPtr.Zero, 0, Native.DESKTOP_ALL, IntPtr.Zero);
            if (_desktop == IntPtr.Zero)
                throw Native.LastError("CreateDesktop");
            si.lpDesktop = _desktopName;
        }
        try
        {
            if (!Native.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_SUSPENDED | Native.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, _nvdaDirectory, ref si, out var pi))
                throw new BackendUnavailableException("The bundled copy of NVDA could not be started.", Native.LastError("CreateProcess"));
            if (_job != IntPtr.Zero)
                Native.AssignProcessToJobObject(_job, pi.hProcess);
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            _process = Process.GetProcessById(pi.dwProcessId);
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => RaiseExited("The bundled copy of NVDA exited.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTBRIDGE_PORT", null);
            Environment.SetEnvironmentVariable("ASSISTBRIDGE_TOKEN", null);
        }
    }

    private void RaiseExited(string reason)
    {
        IsReady = false;
        if (!_stopping && Interlocked.Exchange(ref _exitedRaised, 1) == 0)
            Exited?.Invoke(reason);
    }

    private async Task ReadLoopAsync()
    {
        var link = _link!;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = await link.ReadLineAsync(_stop.Token).ConfigureAwait(false);
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
                    continue;
                }
                if (msg is null || Protocol.TypeOf(msg) is not { } type)
                    continue;
                if (type.StartsWith("bridge_", StringComparison.Ordinal))
                    HandleControl(type["bridge_".Length..], msg);
                else
                    ProtocolMessage?.Invoke(line, msg);
            }
        }
        catch (Exception) when (!_stop.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        if (!_stop.IsCancellationRequested)
            RaiseExited("The link to the bundled copy of NVDA was lost.");
    }

    private void HandleControl(string name, JsonObject msg)
    {
        switch (name)
        {
            case "state":
                var state = new BackendState(
                    ConnectionModeExtensions.FromWire(Protocol.GetString(msg, "mode")),
                    Protocol.GetBool(msg, "sendingKeys"),
                    Protocol.GetBool(msg, "muted"),
                    Protocol.GetBool(msg, "receivingBraille"),
                    Protocol.GetString(msg, "brailleDisplay"),
                    Protocol.GetInt(msg, "brailleCells") ?? 0,
                    Protocol.GetString(msg, "synth"));
                State = state;
                StateChanged?.Invoke(state);
                break;
            case "announce":
                if (Protocol.GetString(msg, "text") is { Length: > 0 } text)
                    Announcement?.Invoke(text);
                break;
            case "cue":
                Cue?.Invoke(Protocol.GetString(msg, "name") ?? "", Protocol.GetString(msg, "wave"), Protocol.GetString(msg, "message"));
                break;
            case "request":
                if (Protocol.GetString(msg, "action") is { Length: > 0 } action)
                    Request?.Invoke(action);
                break;
        }
    }

    /// <summary>Send a control message to the add-on.</summary>
    public Task SendControlAsync(string name, JsonObject? fields = null)
    {
        var msg = fields ?? new JsonObject();
        msg["type"] = "bridge_" + name;
        return SendAsync(Protocol.Encode(msg));
    }

    /// <summary>Hand a Remote Access protocol line received from the network to NVDA.</summary>
    public Task SendProtocolLineAsync(byte[] line)
    {
        var data = new byte[line.Length + 1];
        line.CopyTo(data, 0);
        data[^1] = (byte)'\n';
        return SendAsync(data);
    }

    private async Task SendAsync(byte[] data)
    {
        var link = _link;
        if (link is null)
            return;
        try
        {
            await link.WriteAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Unable to write to the backend link: {ex.Message}");
        }
    }

    public Task ConfigureAsync(AppSettings s) => SendControlAsync("config", new JsonObject
    {
        ["values"] = new JsonObject
        {
            ["readLocalScreen"] = s.ReadLocalScreen,
            ["speakLocallyWhenControlled"] = s.SpeakLocallyWhenControlled,
            ["localSoundsWhenControlled"] = s.LocalSoundsWhenControlled,
            ["synth"] = string.IsNullOrWhiteSpace(s.Synth) ? "auto" : s.Synth,
            ["rate"] = s.SpeechRate,
            ["volume"] = s.SpeechVolume,
            ["muteOnLocalControl"] = s.MuteOnLocalControl,
            ["estimatedRate"] = s.EstimatedRemoteRate,
            ["toggleGestures"] = new JsonArray(s.ToggleControlGestures.Select(g => (JsonNode)g).ToArray()),
            ["pushClipboardGestures"] = new JsonArray(s.PushClipboardGestures.Select(g => (JsonNode)g).ToArray()),
            ["sasGestures"] = new JsonArray(s.SendSasGestures.Select(g => (JsonNode)g).ToArray()),
            ["toggleMuteGestures"] = new JsonArray(s.ToggleMuteGestures.Select(g => (JsonNode)g).ToArray()),
        },
    });

    private async Task KillAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Ask NVDA to exit cleanly, then make sure it has.</summary>
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        if (IsReady)
        {
            await SendControlAsync("quit").ConfigureAwait(false);
            try
            {
                if (_process is not null)
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _stop.Cancel();
        IsReady = false;
        await KillAsync().ConfigureAwait(false);
        if (_link is not null)
            await _link.DisposeAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_job != IntPtr.Zero)
        {
            Native.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
        if (_desktop != IntPtr.Zero)
        {
            Native.CloseDesktop(_desktop);
            _desktop = IntPtr.Zero;
        }
        _process?.Dispose();
    }
}
