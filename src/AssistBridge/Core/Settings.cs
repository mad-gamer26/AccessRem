using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssistBridge.Core;

public enum ServerKind
{
    /// <summary>Connect to a relay server, or directly to a computer hosting a connection.</summary>
    Relay,
    /// <summary>Host the connection on this computer; the other person connects to this computer's address.</summary>
    HostLocally,
}

/// <summary>A saved computer/server: a host, port and key, plus how to use it.</summary>
public sealed class Machine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ServerKind Kind { get; set; } = ServerKind.Relay;
    public string Host { get; set; } = Protocol.DefaultRelayHost;
    public int Port { get; set; } = Protocol.DefaultPort;

    /// <summary>The key, protected with Windows DPAPI for the current user when saved.</summary>
    [JsonIgnore]
    public string Key { get; set; } = "";

    [JsonPropertyName("key")]
    public string? ProtectedKey
    {
        get => Secret.Protect(Key);
        set => Key = Secret.Unprotect(value);
    }

    public ConnectionMode DefaultMode { get; set; } = ConnectionMode.Follower;
    public bool AutoConnect { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset? LastConnected { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (Kind == ServerKind.HostLocally ? $"Hosted on this computer, port {Port}" : Protocol.HostPortToAddress(Host, Port)) : Name;

    [JsonIgnore]
    public string Summary => Kind == ServerKind.HostLocally
        ? $"Hosted on this computer · port {Port}"
        : $"{Protocol.HostPortToAddress(Host, Port)}";

    [JsonIgnore]
    public string DefaultActionText => DefaultMode == ConnectionMode.Follower ? "Usually: get help" : "Usually: give help";

    [JsonIgnore]
    public string AccessibleName => $"{DisplayName}, {Summary}, {DefaultActionText}{(AutoConnect ? ", connects automatically" : "")}";

    public Machine Clone()
    {
        var copy = (Machine)MemberwiseClone();
        return copy;
    }

    public ConnectionInfo ToConnectionInfo(ConnectionMode mode) => Kind == ServerKind.HostLocally
        ? new ConnectionInfo("localhost", Port, Key, mode, Insecure: true)
        : new ConnectionInfo(Host.Trim(), Port, Key, mode);

    public override string ToString() => DisplayName;
}

public sealed class AppSettings
{
    public List<Machine> Machines { get; set; } = new();

    // Trusted self-signed certificates: address (as NVDA formats it) -> SHA-256 fingerprint.
    public Dictionary<string, string> TrustedCertificates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Message of the day hashes already shown, per server.
    public Dictionary<string, string> SeenMotds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Behaviour (mirrors NVDA's [remote] settings where applicable).
    public bool ConfirmDisconnectAsFollower { get; set; } = true;
    public bool MuteOnLocalControl { get; set; }
    public bool PlaySounds { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    // Being controlled (follower).
    public bool SpeakLocallyWhenControlled { get; set; }
    public bool LocalSoundsWhenControlled { get; set; }
    public int EstimatedRemoteRate { get; set; } = 50;
    public bool AllowCtrlAltDel { get; set; } = true;

    // Controlling (leader).
    public bool ReadLocalScreen { get; set; }
    public string Synth { get; set; } = "auto";
    public int? SpeechRate { get; set; }
    public int? SpeechVolume { get; set; }
    public bool ControlRemoteOnConnect { get; set; }

    // Gestures understood by the bundled NVDA while connected (NVDA gesture identifiers).
    public List<string> ToggleControlGestures { get; set; } = new() { "kb:NVDA+alt+tab", "kb:control+alt+shift+f11" };
    public List<string> PushClipboardGestures { get; set; } = new() { "kb:NVDA+control+shift+c" };
    public List<string> SendSasGestures { get; set; } = new();
    public List<string> ToggleMuteGestures { get; set; } = new();

    // Application.
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool HandleNvdaRemoteLinks { get; set; }
    public bool KeepTranscript { get; set; } = true;
    public int TranscriptLimit { get; set; } = 500;
    public string? NvdaPathOverride { get; set; }
    public bool ReplaceRunningNvdaWithoutAsking { get; set; }
    public bool RestoreReplacedNvda { get; set; } = true;
    public Guid? LastMachineId { get; set; }
}

/// <summary>Per-user DPAPI protection for secrets stored in the settings file.</summary>
public static class Secret
{
    private const string Prefix = "dpapi:";

    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AssistBridge machine key v1");
}

public sealed class SettingsStore : ICertificateTrust
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _lock = new();

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? AppPaths.DataDirectory;
        System.IO.Directory.CreateDirectory(Directory);
        Current = Load();
    }

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "settings.json");
    public AppSettings Current { get; private set; }

    public event Action? Saved;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (loaded is not null)
                {
                    loaded.TrustedCertificates = new Dictionary<string, string>(loaded.TrustedCertificates, StringComparer.OrdinalIgnoreCase);
                    loaded.SeenMotds = new Dictionary<string, string>(loaded.SeenMotds, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // Keep a copy of the unreadable file rather than silently losing it.
            try
            {
                File.Copy(FilePath, FilePath + ".unreadable", overwrite: true);
            }
            catch (Exception)
            {
            }
            AppLog.Write($"Settings could not be read and were reset: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        Saved?.Invoke();
    }

    public bool IsTrusted(string address, string fingerprint)
    {
        lock (_lock)
            return Current.TrustedCertificates.TryGetValue(address, out var fp) &&
                   string.Equals(fp, fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public void Trust(string address, string fingerprint)
    {
        lock (_lock)
            Current.TrustedCertificates[address] = fingerprint;
        Save();
    }

    /// <summary>True the first time a given message of the day is seen for a server (as NVDA tracks it).</summary>
    public bool ShouldDisplayMotd(string host, int port, string motd)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(motd))).ToLowerInvariant();
        var address = $"{host}:{port}";
        lock (_lock)
        {
            if (Current.SeenMotds.TryGetValue(address, out var seen) && seen == hash)
                return false;
            Current.SeenMotds[address] = hash;
        }
        Save();
        return true;
    }
}

public static class AppPaths
{
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("ASSISTBRIDGE_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AssistBridge");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string NvdaConfigDirectory => Path.Combine(DataDirectory, "nvdaConfig");
    public static string CertificateDirectory => Path.Combine(DataDirectory, "certificates");
    public static string AppDirectory => AppContext.BaseDirectory;
}

/// <summary>Small rolling application log for troubleshooting.</summary>
public static class AppLog
{
    private static readonly object Lock = new();

    public static string FilePath => Path.Combine(AppPaths.LogDirectory, "assistbridge.log");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                System.IO.Directory.CreateDirectory(AppPaths.LogDirectory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }
}
