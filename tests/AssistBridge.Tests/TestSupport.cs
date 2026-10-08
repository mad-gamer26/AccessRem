using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using AssistBridge.Core;

namespace AssistBridge.Tests;

/// <summary>A NetworkSession that records everything it receives, for assertions.</summary>
public sealed class RecordingPeer : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonObject> _messages = new();
    private readonly SemaphoreSlim _signal = new(0);

    public RecordingPeer(ConnectionInfo info, CertificatePolicy policy, ICertificateTrust trust)
    {
        Session = new NetworkSession(info, policy, trust, retryInitialFailures: true);
        Session.MessageReceived += (msg, _) =>
        {
            _messages.Enqueue(msg);
            _signal.Release();
        };
        Session.StateChanged += (state, _) => _signal.Release();
        Session.Start();
    }

    public NetworkSession Session { get; }
    public IReadOnlyList<JsonObject> Messages => _messages.ToList();

    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> predicate, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var match = _messages.FirstOrDefault(predicate);
            if (match is not null)
                return match;
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        throw new TimeoutException($"Timed out waiting for {what ?? "message"}. Received: {string.Join(" | ", _messages.Select(m => m.ToJsonString()))}");
    }

    public Task<JsonObject> WaitForTypeAsync(string type, TimeSpan? timeout = null) =>
        WaitForAsync(m => Protocol.TypeOf(m) == type, timeout, type);

    public async Task WaitConnectedAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!Session.IsConnected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Peer did not connect (state {Session.State}).");
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(200));
        }
    }

    public void Clear()
    {
        while (_messages.TryDequeue(out _))
        {
        }
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

public sealed class TrustNothing : ICertificateTrust
{
    public bool IsTrusted(string address, string fingerprint) => false;
}

/// <summary>Runs posted callbacks on one dedicated thread, standing in for the UI thread.</summary>
public sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
            }
        }) { IsBackground = true, Name = "TestUiThread" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        using var done = new ManualResetEventSlim();
        Post(_ => { d(state); done.Set(); }, null);
        done.Wait();
    }

    /// <summary>Run an async function on the context and wait for it.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                tcs.SetResult(await func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public Task RunAsync(Func<Task> func) => RunAsync(async () => { await func(); return true; });

    public T Get<T>(Func<T> func)
    {
        T result = default!;
        Send(_ => result = func(), null);
        return result;
    }

    public void Dispose() => _queue.CompleteAdding();
}

public static class TestUtil
{
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static string TempDirectory(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AssistBridgeTests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.ps1")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(100);
        }
    }
}
