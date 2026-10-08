using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AccessRem.Core;

/// <summary>One-off operations against relay servers: key generation and port checks.</summary>
public static class RelayTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Ask a relay server to generate a fresh channel key (as NVDA's "Generate key" button does).</summary>
    public static async Task<string> GenerateKeyAsync(string host, int port, CertificatePolicy policy, ICertificateTrust trust, CancellationToken ct)
    {
        await using var connection = await RelayConnection.ConnectAsync(host, port, policy, trust, ct).ConfigureAwait(false);
        await connection.SendAsync(Protocol.Message(Protocol.MsgProtocolVersion, ("version", Protocol.ProtocolVersion)), ct).ConfigureAwait(false);
        await connection.SendAsync(Protocol.Message(Protocol.MsgGenerateKey), ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var line = await connection.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("The server closed the connection before sending a key.");
            if (JsonNode.Parse(line) is not JsonObject msg)
                continue;
            var type = Protocol.TypeOf(msg);
            if (type == Protocol.MsgGenerateKey && Protocol.GetString(msg, "key") is { Length: > 0 } key)
                return key;
            if (type == Protocol.MsgError)
                throw new IOException($"The server reported an error: {Protocol.GetString(msg, "message") ?? "unknown error"}.");
        }
    }

    public sealed record PortCheckResult(
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("open")] bool Open);

    /// <summary>Find this computer's external IP and whether a port is reachable (NVDA's "Get external IP").</summary>
    public static async Task<PortCheckResult> CheckPortAsync(int port, CancellationToken ct)
    {
        var result = await Http.GetFromJsonAsync<PortCheckResult>($"https://portcheck.nvdaremote.com/port/{port}", ct).ConfigureAwait(false);
        return result ?? throw new IOException("The port check service returned no data.");
    }
}
