using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace AccessRem.Core;

/// <summary>Constants and helpers for the NVDA Remote Access protocol (protocol version 2).</summary>
public static class Protocol
{
    public const int ProtocolVersion = 2;
    public const int DefaultPort = 6837;
    public const string UrlScheme = "nvdaremote";
    public const string UrlPrefix = "nvdaremote://";
    public const string DefaultRelayHost = "nvdaremote.com";

    // Message types (mirrors _remoteClient.protocol.RemoteMessageType).
    public const string MsgProtocolVersion = "protocol_version";
    public const string MsgJoin = "join";
    public const string MsgChannelJoined = "channel_joined";
    public const string MsgClientJoined = "client_joined";
    public const string MsgClientLeft = "client_left";
    public const string MsgGenerateKey = "generate_key";
    public const string MsgKey = "key";
    public const string MsgSpeak = "speak";
    public const string MsgCancel = "cancel";
    public const string MsgPauseSpeech = "pause_speech";
    public const string MsgTone = "tone";
    public const string MsgWave = "wave";
    public const string MsgSendSas = "send_SAS";
    public const string MsgIndex = "index";
    public const string MsgDisplay = "display";
    public const string MsgBrailleInput = "braille_input";
    public const string MsgSetBrailleInfo = "set_braille_info";
    public const string MsgSetDisplaySize = "set_display_size";
    public const string MsgSetClipboardText = "set_clipboard_text";
    public const string MsgMotd = "motd";
    public const string MsgVersionMismatch = "version_mismatch";
    public const string MsgPing = "ping";
    public const string MsgError = "error";
    public const string MsgNvdaNotConnected = "nvda_not_connected";

    public static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    /// <summary>Serialise a message as a newline-terminated UTF-8 JSON line.</summary>
    public static byte[] Encode(JsonObject message) =>
        Encoding.UTF8.GetBytes(message.ToJsonString(CompactJson) + "\n");

    public static JsonObject Message(string type, params (string Key, JsonNode? Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields)
            obj[key] = value;
        obj["type"] = type;
        return obj;
    }

    public static string? TypeOf(JsonObject message) =>
        message.TryGetPropertyValue("type", out var t) && t is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? GetString(JsonObject message, string key) =>
        message.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool GetBool(JsonObject message, string key, bool fallback = false) =>
        message.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public static int? GetInt(JsonObject message, string key)
    {
        if (!message.TryGetPropertyValue(key, out var n) || n is not JsonValue v)
            return null;
        if (v.TryGetValue<int>(out var i))
            return i;
        if (v.TryGetValue<long>(out var l))
            return (int)l;
        if (v.TryGetValue<double>(out var d))
            return (int)d;
        return null;
    }

    /// <summary>Parse "host", "host:port" or "[v6]:port" (mirrors protocol.addressToHostPort).</summary>
    public static (string Host, int Port) AddressToHostPort(string address)
    {
        address = address.Trim();
        if (address.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var info = ConnectionInfo.FromUrl(address);
            return (info.Host, info.Port);
        }
        if (Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var host = uri.Host.Trim('[', ']');
            var port = uri.IsDefaultPort || uri.Port <= 0 ? DefaultPort : uri.Port;
            return (host, port);
        }
        return (address, DefaultPort);
    }

    /// <summary>Format a host and port, omitting the default port (mirrors protocol.hostPortToAddress).</summary>
    public static string HostPortToAddress(string host, int port)
    {
        if (host.Contains(':'))
            host = $"[{host}]";
        return port != DefaultPort ? $"{host}:{port}" : host;
    }

    /// <summary>Random numeric key, as NVDA generates for self-hosted servers.</summary>
    public static string GenerateLocalKey()
    {
        var sb = new StringBuilder();
        sb.Append(Random.Shared.Next(1, 9));
        for (var i = 0; i < 6; i++)
            sb.Append(Random.Shared.Next(0, 9));
        return sb.ToString();
    }
}

/// <summary>Leader ("master") controls; follower ("slave") is controlled. Wire values match NVDA.</summary>
public enum ConnectionMode
{
    Leader,
    Follower,
}

public static class ConnectionModeExtensions
{
    public static string WireValue(this ConnectionMode mode) => mode == ConnectionMode.Leader ? "master" : "slave";

    public static ConnectionMode Opposite(this ConnectionMode mode) =>
        mode == ConnectionMode.Leader ? ConnectionMode.Follower : ConnectionMode.Leader;

    public static ConnectionMode? FromWire(string? value) => value?.ToLowerInvariant() switch
    {
        "master" or "leader" => ConnectionMode.Leader,
        "slave" or "follower" => ConnectionMode.Follower,
        _ => null,
    };

    public static string Describe(this ConnectionMode mode) =>
        mode == ConnectionMode.Leader ? "Controlling another computer" : "Letting this computer be controlled";
}

/// <summary>Everything needed to join a Remote Access channel (mirrors connectionInfo.ConnectionInfo).</summary>
public sealed record ConnectionInfo(string Host, int Port, string Key, ConnectionMode Mode, bool Insecure = false)
{
    public string Address => Protocol.HostPortToAddress(Host, Port);

    public static ConnectionInfo FromUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Protocol.UrlScheme, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("This is not an nvdaremote:// link.");
        var host = uri.Host.Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host))
            throw new FormatException("The link does not include a host.");
        var query = HttpUtility.ParseQueryString(uri.Query);
        var key = query["key"];
        if (string.IsNullOrEmpty(key))
            throw new FormatException("The link does not include a key.");
        var modeText = query["mode"];
        if (string.IsNullOrEmpty(modeText))
            throw new FormatException("The link does not include a mode.");
        var mode = ConnectionModeExtensions.FromWire(modeText)
            ?? throw new FormatException($"The link has an unknown mode: {modeText}.");
        var insecure = string.Equals(query["insecure"], "true", StringComparison.OrdinalIgnoreCase);
        var port = uri.IsDefaultPort || uri.Port <= 0 ? Protocol.DefaultPort : uri.Port;
        return new ConnectionInfo(host, port, key, mode, insecure);
    }

    private string BuildUrl(ConnectionMode mode)
    {
        var query = $"key={Uri.EscapeDataString(Key)}&mode={mode.WireValue()}";
        if (Insecure)
            query += "&insecure=true";
        return $"{Protocol.UrlPrefix}{Address}?{query}";
    }

    /// <summary>The link for this exact connection.</summary>
    public string GetUrl() => BuildUrl(Mode);

    /// <summary>The link another person opens to join this session in the opposite role.</summary>
    public string GetUrlToConnect() => BuildUrl(Mode.Opposite());
}
