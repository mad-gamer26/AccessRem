using System.IO.Pipes;
using System.Media;
using System.Text;
using AccessRem.Core;
using Microsoft.Win32;

namespace AccessRem.Services;

/// <summary>Registers AccessRem as the handler for nvdaremote:// links (per user).</summary>
public static class UrlProtocol
{
    private const string KeyPath = @"Software\Classes\nvdaremote";

    public static bool IsRegisteredToUs()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command");
        var command = key?.GetValue(null) as string;
        return command is not null && Environment.ProcessPath is { } exe &&
               command.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(null, "URL:NVDA Remote Access link");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon"))
            icon.SetValue(null, $"\"{exe}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(null, $"\"{exe}\" \"%1\"");
    }

    public static void Unregister()
    {
        if (IsRegisteredToUs())
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }
}

/// <summary>Start AccessRem when the user signs in.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AccessRem";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --startup");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        // The startup entry used AccessRem's development name.
        key.DeleteValue("AssistBridge", throwOnMissingValue: false);
    }
}

/// <summary>Keeps a single AccessRem per user, handing links and commands to the running copy.</summary>
public sealed class SingleInstance : IDisposable
{
    // string.GetHashCode is randomised per process, so derive a stable per-user id.
    private static readonly string Id = "AccessRem-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public event Action<string[]>? ArgumentsReceived;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, @"Local\" + Id, out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex);
    }

    public void Listen() => Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(_stop.Token).ConfigureAwait(false);
                ArgumentsReceived?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Send arguments to the running instance. Returns false if it could not be reached.</summary>
    public static bool SendToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var data = Encoding.UTF8.GetBytes(string.Join('\n', args.Length == 0 ? new[] { "--show" } : args));
            client.Write(data);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int _disposed;

    public void Dispose()
    {
        // Called both when the app exits and by Program.Main's using block.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        _mutex.Dispose();
    }
}

/// <summary>Plays NVDA's Remote Access sounds from the bundled copy.</summary>
public sealed class SoundCues
{
    private readonly Func<string?> _wavesDirectory;
    private readonly Func<bool> _enabled;

    public SoundCues(Func<string?> wavesDirectory, Func<bool> enabled)
    {
        _wavesDirectory = wavesDirectory;
        _enabled = enabled;
    }

    public void Play(string? wave)
    {
        if (string.IsNullOrEmpty(wave) || !_enabled())
            return;
        var dir = _wavesDirectory();
        if (dir is null)
            return;
        var path = Path.Combine(dir, wave.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? wave : wave + ".wav");
        if (!File.Exists(path))
            return;
        try
        {
            var player = new SoundPlayer(path);
            player.Play();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Unable to play {path}: {ex.Message}");
        }
    }
}
