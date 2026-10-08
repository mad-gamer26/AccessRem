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
