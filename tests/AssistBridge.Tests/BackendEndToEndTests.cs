using System.Text.Json.Nodes;
using AssistBridge.Backend;
using AssistBridge.Core;
using Xunit.Abstractions;

namespace AssistBridge.Tests;

/// <summary>Answers prompts without a person; refuses to replace a running NVDA as a safety net.</summary>
public sealed class TestUi : ISessionUi
{
    public List<string> Errors { get; } = new();
    public Task<CertificateDecision> AskTrustCertificateAsync(CertificateUntrustedException error) => Task.FromResult(CertificateDecision.Cancel);
    public Task<bool> AskReplaceRunningNvdaAsync(RunningNvda running) => Task.FromResult(false);
    public void ShowMotd(string server, string message) { }
    public void ShowError(string title, string message) => Errors.Add($"{title}: {message}");
    public string? GetClipboardText() => "clipboard text";
    public void SetClipboardText(string text) { }
}

/// <summary>
/// Runs the real bundled NVDA with the backend add-on. NVDA is started on a separate, invisible Windows
/// desktop so it cannot replace a screen reader already running for the person using this computer, and
/// cannot see or inject their keystrokes. Requires dist\AssistBridge\nvda (run build.ps1 first).
/// Set ASSISTBRIDGE_SKIP_BACKEND=1 to skip.
/// </summary>
[Collection("Backend")]
public class BackendEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public BackendEndToEndTests(ITestOutputHelper output) => _output = output;

    private static string? NvdaDirectory => Path.Combine(TestUtil.RepoRoot, "dist", "AssistBridge", "nvda") is var d && File.Exists(Path.Combine(d, "nvda_noUIAccess.exe")) ? d : null;

    private static bool ShouldSkip => Environment.GetEnvironmentVariable("ASSISTBRIDGE_SKIP_BACKEND") == "1" || NvdaDirectory is null;

    private (SettingsStore Store, SingleThreadContext Ctx, SessionController Session, TestUi Ui, string DataDir) Create(string name, Action<AppSettings>? configure = null)
    {
        var dataDir = TestUtil.TempDirectory(name);
        Environment.SetEnvironmentVariable("ASSISTBRIDGE_DATA_DIR", dataDir);
        Environment.SetEnvironmentVariable("ASSISTBRIDGE_BACKEND_DESKTOP", "AssistBridgeTest-" + Guid.NewGuid().ToString("N")[..6]);
        Environment.SetEnvironmentVariable("ASSISTBRIDGE_NVDA_LOGLEVEL", "10");
        var store = new SettingsStore(dataDir);
        store.Current.PlaySounds = false;
        store.Current.NvdaPathOverride = NvdaDirectory;
        configure?.Invoke(store.Current);
        var ctx = new SingleThreadContext();
        var ui = new TestUi();
        var session = ctx.Get(() => new SessionController(store, ui, ctx));
        session.Announced += text => _output.WriteLine($"announce: {text}");
        return (store, ctx, session, ui, dataDir);
    }

    private void DumpLogs(string dataDir)
    {
        foreach (var file in new[] { "nvda-backend.log", "assistbridge.log" })
        {
            var path = Path.Combine(dataDir, "logs", file);
            if (!File.Exists(path))
                continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            _output.WriteLine($"----- {file} (last 60 lines) -----");
            foreach (var line in lines.TakeLast(60))
                _output.WriteLine(line);
        }
    }

    [Fact]
    public async Task FollowerForwardsNvdaSpeechAndBrailleToLeader()
    {
        if (ShouldSkip)
            return;
        var (store, ctx, session, ui, dataDir) = Create("follower");
        var port = TestUtil.FreePort();
        try
        {
            await ctx.RunAsync(() => session.ConnectAsync(new ConnectionInfo("localhost", port, "e2e", ConnectionMode.Follower, true), null, hostLocally: true));
            await TestUtil.WaitUntilAsync(() => ui.Errors.Count > 0 ? throw new Exception(string.Join("; ", ui.Errors)) : ctx.Get(() => session.IsConnected), TimeSpan.FromSeconds(90), "follower to connect");
            _output.WriteLine($"Backend NVDA {session.BackendVersion} connected as follower.");

            await using var leader = new RecordingPeer(new ConnectionInfo("localhost", port, "e2e", ConnectionMode.Leader),
                new CertificatePolicy(Insecure: true), new TrustNothing());
            await leader.WaitForTypeAsync(Protocol.MsgChannelJoined);
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.ControllingPeers) == 1, TimeSpan.FromSeconds(10), "follower to see the leader");

            // The leader has a 40-cell braille display; NVDA negotiates its display size down to it.
            await leader.Session.SendAsync(Protocol.Message(Protocol.MsgSetBrailleInfo, ("name", "testDisplay"), ("numCells", 40)));
            await Task.Delay(500);

            // Speech produced by NVDA on this computer is serialised by NVDA's FollowerSession and relayed.
            await ctx.RunAsync(() => session.SpeakTestAsync("Hello from the controlled computer", asLocalOutput: true));
            var speak = await leader.WaitForAsync(m => Protocol.TypeOf(m) == Protocol.MsgSpeak &&
                                                        m["sequence"]!.ToJsonString().Contains("Hello from the controlled computer"),
                TimeSpan.FromSeconds(20), "forwarded speech");
            _output.WriteLine($"leader received: {speak.ToJsonString()}");
            Assert.NotNull(Protocol.GetInt(speak, "priority"));

            var display = await leader.WaitForTypeAsync(Protocol.MsgDisplay, TimeSpan.FromSeconds(20));
            var cells = display["cells"]!.AsArray();
            _output.WriteLine($"leader received {cells.Count} braille cells");
            Assert.True(cells.Count is > 0 and <= 40);
            Assert.Contains(ctx.Get(() => session.Transcript.ToList()), t => t.Text.Contains("Hello from the controlled computer") && t.Direction == TranscriptDirection.Sent);

            // Clipboard pushed by the helper lands here.
            await leader.Session.SendAsync(Protocol.Message(Protocol.MsgSetClipboardText, ("text", "from helper")));
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.Events.Any(e => e.Text.Contains("Received clipboard"))), TimeSpan.FromSeconds(10), "clipboard");

            // This computer hosted the session, so disconnecting closes the server and drops the helper.
            await ctx.RunAsync(() => session.DisconnectAsync());
            await TestUtil.WaitUntilAsync(() => !leader.Session.IsConnected, TimeSpan.FromSeconds(10), "the helper to be disconnected");
            Assert.Empty(ui.Errors);
            Assert.False(ctx.Get(() => session.IsActive));

            // NVDA saved its configuration on exit: Caps Lock and both Insert keys act as the NVDA key.
            var nvdaIni = File.ReadAllText(Path.Combine(dataDir, "nvdaConfig", "nvda.ini"));
            Assert.Matches(@"NVDAModifierKeys\s*=\s*7", nvdaIni);
        }
        catch
        {
            DumpLogs(dataDir);
            throw;
        }
        finally
        {
            await ctx.RunAsync(async () => await session.DisposeAsync());
            ctx.Dispose();
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>Every utterance NVDA logs must come after the silent relay voice is loaded.</summary>
    private static void AssertSilentFromTheStart(string nvdaLog)
    {
        var silentLoaded = nvdaLog.IndexOf("Loaded synthDriver assistBridgeSilent", StringComparison.Ordinal);
        Assert.True(silentLoaded >= 0, "The silent relay voice was never loaded.");
        var firstSpeech = nvdaLog.IndexOf("speech.speech.speak", StringComparison.Ordinal);
        Assert.True(firstSpeech < 0 || firstSpeech > silentLoaded, "NVDA spoke before switching to the silent relay voice.");
    }

    [Fact]
    public async Task FollowerIsSilentRefusesExitAndRecoversFromCrash()
    {
        if (ShouldSkip)
            return;
        var (store, ctx, session, ui, dataDir) = Create("recovery");
        var announcements = new List<string>();
        session.Announced += text => { lock (announcements) announcements.Add(text); };
        var showRequested = false;
        session.ShowRequested += () => showRequested = true;
        var port = TestUtil.FreePort();
        var nvdaLogPath = Path.Combine(dataDir, "logs", "nvda-backend.log");
        try
        {
            await ctx.RunAsync(() => session.ConnectAsync(new ConnectionInfo("localhost", port, "e2e", ConnectionMode.Follower, true), null, hostLocally: true));
            await TestUtil.WaitUntilAsync(() => ui.Errors.Count > 0 ? throw new Exception(string.Join("; ", ui.Errors)) : ctx.Get(() => session.IsConnected), TimeSpan.FromSeconds(90), "follower to connect");
            await using var leader = new RecordingPeer(new ConnectionInfo("localhost", port, "e2e", ConnectionMode.Leader),
                new CertificatePolicy(Insecure: true), new TrustNothing());
            await leader.WaitForTypeAsync(Protocol.MsgChannelJoined);
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.ControllingPeers) == 1, TimeSpan.FromSeconds(10), "the leader to join");
            await leader.Session.SendAsync(Protocol.Message(Protocol.MsgSetBrailleInfo, ("name", "testDisplay"), ("numCells", 20)));
            await Task.Delay(500);

            // 1. Nothing was spoken aloud: the silent voice was chosen before NVDA said anything.
            AssertSilentFromTheStart(ReadShared(nvdaLogPath));

            // 2. NVDA+Q (and every other exit route) is refused during a session.
            var pid = ctx.Get(() => session.BackendProcessId)!.Value;
            await ctx.RunAsync(() => session.TryExitBackendAsync());
            await TestUtil.WaitUntilAsync(() => { lock (announcements) return announcements.Any(a => a.Contains("belongs to AssistBridge")); },
                TimeSpan.FromSeconds(10), "the exit refusal");
            await Task.Delay(1500);
            Assert.False(System.Diagnostics.Process.GetProcessById(pid).HasExited);
            Assert.True(showRequested);

            // 3. If NVDA crashes, it is restarted while the network connection stays up.
            System.Diagnostics.Process.GetProcessById(pid).Kill();
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.Events.Any(e => e.Text.Contains("session restored"))),
                TimeSpan.FromSeconds(90), "the speech engine to be restarted");
            Assert.NotEqual(pid, ctx.Get(() => session.BackendProcessId));
            Assert.True(ctx.Get(() => session.IsConnected));
            Assert.Equal(1, leader.Session.SuccessfulConnects);
            Assert.Empty(ui.Errors);

            // The restarted NVDA knows the helper (speech is forwarded) and its 20-cell display (braille is sized to it).
            leader.Clear();
            await ctx.RunAsync(() => session.SpeakTestAsync("Back after a restart", asLocalOutput: true));
            await leader.WaitForAsync(m => Protocol.TypeOf(m) == Protocol.MsgSpeak && m.ToJsonString().Contains("Back after a restart"),
                TimeSpan.FromSeconds(20), "speech after the restart");
            var display = await leader.WaitForTypeAsync(Protocol.MsgDisplay, TimeSpan.FromSeconds(20));
            Assert.Equal(20, display["cells"]!.AsArray().Count);
            AssertSilentFromTheStart(ReadShared(nvdaLogPath));

            await ctx.RunAsync(() => session.DisconnectAsync());
        }
        catch
        {
            DumpLogs(dataDir);
            throw;
        }
        finally
        {
            await ctx.RunAsync(async () => await session.DisposeAsync());
            ctx.Dispose();
        }
    }

    /// <summary>The configuration installed mode writes for secure screens is accepted by the bundled NVDA, and enables its Remote Access.</summary>
    [Fact]
    public async Task SecureScreenConfigurationIsAcceptedByNvda()
    {
        if (ShouldSkip)
            return;
        var fakeNvdaDir = TestUtil.TempDirectory("secureconfig");
        Services.SystemInstall.WriteSecureScreenConfig(fakeNvdaDir, speakLocally: false);
        var configDir = Path.Combine(fakeNvdaDir, "systemConfig");
        var logPath = Path.Combine(fakeNvdaDir, "nvda.log");
        var exe = Path.Combine(NvdaDirectory!, "nvda_noUIAccess.exe");
        var desktop = "AssistBridgeCfgTest-" + Guid.NewGuid().ToString("N")[..6];

        // With Remote Access enabled, NVDA registers itself for nvdaremote:// links; put back whatever was there.
        const string handlerKey = @"Software\Classes\nvdaremote\shell\open\command";
        string? previousHandler;
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(handlerKey))
            previousHandler = key?.GetValue(null) as string;
        var hDesktop = Backend.Native.CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, Backend.Native.DESKTOP_ALL, IntPtr.Zero);
        try
        {
            RunOnDesktop(desktop, exe, $"--minimal --no-sr-flag --config-path \"{configDir}\" --log-file \"{logPath}\" --log-level 10");
            await TestUtil.WaitUntilAsync(() => File.Exists(logPath) && ReadShared(logPath).Contains("NVDA initialized"), TimeSpan.FromSeconds(60), "NVDA to start");
            var log = ReadShared(logPath);
            _output.WriteLine(string.Join("\n", log.Split('\n').Where(l => l.Contains("emote") || l.Contains("onfig")).Take(30)));
            Assert.Contains("Initializing Remote Access", log);
            Assert.DoesNotContain("Remote Access disabled", log);
            Assert.DoesNotContain("Unable to validate", log);
            Assert.DoesNotContain("configuration file contains errors", log, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Loaded synthDriver silence", log);
        }
        finally
        {
            // Ask that NVDA (the only one on the test desktop) to quit cleanly.
            RunOnDesktop(desktop, exe, "-q");
            await Task.Delay(4000);
            Backend.Native.CloseDesktop(hDesktop);
            if (previousHandler is null)
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\nvdaremote", throwOnMissingSubKey: false);
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(handlerKey);
                key.SetValue(null, previousHandler);
            }
        }
    }

    private static void RunOnDesktop(string desktop, string exe, string arguments)
    {
        var si = new Backend.Native.STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<Backend.Native.STARTUPINFO>(), lpDesktop = desktop };
        var cmd = new System.Text.StringBuilder($"\"{exe}\" {arguments}");
        if (!Backend.Native.CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
            throw Backend.Native.LastError("CreateProcess");
        Backend.Native.CloseHandle(pi.hThread);
        Backend.Native.CloseHandle(pi.hProcess);
    }

    [Fact]
    public async Task LeaderRunsNvdaLeaderSessionAndTogglesControl()
    {
        if (ShouldSkip)
            return;
        // The NVDA "No speech" synthesizer keeps the test silent.
        var (store, ctx, session, ui, dataDir) = Create("leader", s => s.Synth = "silence");
        var port = TestUtil.FreePort();
        try
        {
            await ctx.RunAsync(() => session.ConnectAsync(new ConnectionInfo("localhost", port, "e2e", ConnectionMode.Leader, true), null, hostLocally: true));
            await TestUtil.WaitUntilAsync(() => ui.Errors.Count > 0 ? throw new Exception(string.Join("; ", ui.Errors)) : ctx.Get(() => session.IsConnected), TimeSpan.FromSeconds(90), "leader to connect");

            // Toggling control with nobody to control is refused, as in NVDA.
            await ctx.RunAsync(() => session.ToggleControlAsync());
            await Task.Delay(500);
            Assert.False(ctx.Get(() => session.SendingKeys));

            await using var follower = new RecordingPeer(new ConnectionInfo("localhost", port, "e2e", ConnectionMode.Follower),
                new CertificatePolicy(Insecure: true), new TrustNothing());
            await follower.WaitForTypeAsync(Protocol.MsgChannelJoined);
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.ControlledPeers) == 1, TimeSpan.FromSeconds(10), "leader to see the follower");

            // NVDA's LeaderSession announces its braille display as soon as a follower joins.
            var info = await follower.WaitForTypeAsync(Protocol.MsgSetBrailleInfo, TimeSpan.FromSeconds(20));
            _output.WriteLine($"follower received: {info.ToJsonString()}");
            Assert.NotNull(Protocol.GetInt(info, "numCells"));

            // Remote speech is rendered by NVDA and shown in the transcript.
            await follower.Session.SendAsync(JsonNode.Parse("""{"sequence": ["Remote says hello", ["EndUtteranceCommand", {}]], "priority": 0, "type": "speak"}""")!.AsObject());
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.Transcript.Any(t => t.Text == "Remote says hello" && t.Direction == TranscriptDirection.Received)),
                TimeSpan.FromSeconds(10), "transcript");
            await follower.Session.SendAsync(Protocol.Message(Protocol.MsgTone, ("hz", 440), ("length", 20), ("left", 50), ("right", 50)));
            await follower.Session.SendAsync(Protocol.Message(Protocol.MsgCancel));

            // Switch the keyboard to the remote computer and back.
            await ctx.RunAsync(() => session.SetControlAsync(true));
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.SendingKeys), TimeSpan.FromSeconds(10), "control of the remote computer");
            await ctx.RunAsync(() => session.ToggleMuteAsync());
            await TestUtil.WaitUntilAsync(() => ctx.Get(() => session.Muted), TimeSpan.FromSeconds(10), "mute");
            await ctx.RunAsync(() => session.SetControlAsync(false));
            await TestUtil.WaitUntilAsync(() => !ctx.Get(() => session.SendingKeys), TimeSpan.FromSeconds(10), "control of this computer");

            // Control+Alt+Delete is relayed to the controlled computer.
            await ctx.RunAsync(() => session.SendSasAsync());
            await follower.WaitForTypeAsync(Protocol.MsgSendSas, TimeSpan.FromSeconds(10));

            await ctx.RunAsync(() => session.DisconnectAsync());
            Assert.Empty(ui.Errors);

            var nvdaLog = File.ReadAllText(Path.Combine(dataDir, "logs", "nvda-backend.log"));
            Assert.DoesNotContain("Error handling AssistBridge control message", nvdaLog);
            Assert.Contains("assistBridge", nvdaLog);
        }
        catch
        {
            DumpLogs(dataDir);
            throw;
        }
        finally
        {
            await ctx.RunAsync(async () => await session.DisposeAsync());
            ctx.Dispose();
        }
    }
}
