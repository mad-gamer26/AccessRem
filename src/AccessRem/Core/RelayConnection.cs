using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace AccessRem.Core;

/// <summary>Thrown when a server's certificate cannot be verified and has not been trusted.</summary>
public sealed class CertificateUntrustedException : Exception
{
    public CertificateUntrustedException(string host, int port, string? fingerprint, SslPolicyErrors errors)
        : base($"The certificate presented by {Protocol.HostPortToAddress(host, port)} could not be verified ({errors}).")
    {
        Host = host;
        Port = port;
        Fingerprint = fingerprint;
        Errors = errors;
    }

    public string Host { get; }
    public int Port { get; }
    public string? Fingerprint { get; }
    public SslPolicyErrors Errors { get; }
    public string Address => Protocol.HostPortToAddress(Host, Port);
}

/// <summary>Decides whether an unverifiable certificate fingerprint has been trusted for an address.</summary>
public interface ICertificateTrust
{
    bool IsTrusted(string address, string fingerprint);
}

/// <summary>How to treat the server certificate (mirrors NVDA's insecure / trustedFingerprint handling).</summary>
public sealed record CertificatePolicy(bool Insecure = false, string? PinnedFingerprint = null)
{
    public static readonly CertificatePolicy Verify = new();
}

/// <summary>A TLS connection to a Remote Access relay server, exchanging JSON lines.</summary>
public sealed class RelayConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly LineStream _lines;
    private int _disposed;

    private RelayConnection(TcpClient client, SslStream ssl, string host, int port, string fingerprint)
    {
        _client = client;
        _lines = new LineStream(ssl);
        Host = host;
        Port = port;
        Fingerprint = fingerprint;
    }

    public string Host { get; }
    public int Port { get; }

    /// <summary>SHA-256 fingerprint (lowercase hex) of the server's certificate, as NVDA computes it.</summary>
    public string Fingerprint { get; }

    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    public static async Task<RelayConnection> ConnectAsync(
        string host,
        int port,
        CertificatePolicy policy,
        ICertificateTrust? trust,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        client.Client.DualMode = true;
        try
        {
            await ConnectSocketAsync(client, host, port, timeoutCts.Token).ConfigureAwait(false);
            ConfigureKeepAlive(client.Client);
            string? seenFingerprint = null;
            var seenErrors = SslPolicyErrors.None;
            var address = Protocol.HostPortToAddress(host, port);
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, cert, _, errors) =>
            {
                if (cert is null)
                    return false;
                seenFingerprint = FingerprintOf(cert);
                seenErrors = errors;
                if (policy.PinnedFingerprint is { } pinned)
                    return string.Equals(pinned, seenFingerprint, StringComparison.OrdinalIgnoreCase);
                if (policy.Insecure)
                    return true;
                if (errors == SslPolicyErrors.None)
                    return true;
                return trust?.IsTrusted(address, seenFingerprint) == true;
            });
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException) when (seenFingerprint is not null)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw new CertificateUntrustedException(host, port, seenFingerprint, seenErrors);
            }
            return new RelayConnection(client, ssl, host, port, seenFingerprint ?? "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Timed out connecting to {Protocol.HostPortToAddress(host, port)}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task ConnectSocketAsync(TcpClient client, string host, int port, CancellationToken ct)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer loopback IPv4 first; the local relay listens dual-stack.
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, ct).ConfigureAwait(false);
            return;
        }
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
    }

    internal static void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
        }
        catch (SocketException)
        {
            // Keep-alive tuning is best effort.
        }
    }

    public Task SendAsync(JsonObject message, CancellationToken ct = default) =>
        _lines.WriteAsync(Protocol.Encode(message), ct);

    /// <summary>Send an already serialised line (must not contain a newline).</summary>
    public Task SendLineAsync(byte[] lineWithoutTerminator, CancellationToken ct = default)
    {
        var data = new byte[lineWithoutTerminator.Length + 1];
        lineWithoutTerminator.CopyTo(data, 0);
        data[^1] = (byte)'\n';
        return _lines.WriteAsync(data, ct);
    }

    public Task<byte[]?> ReadLineAsync(CancellationToken ct) => _lines.ReadLineAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _lines.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
