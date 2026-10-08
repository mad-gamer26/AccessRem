using System.Buffers;

namespace AssistBridge.Core;

/// <summary>Newline-delimited message framing over any stream, with serialised writes.</summary>
public sealed class LineStream : IAsyncDisposable
{
    private const int MaxLineBytes = 16 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private readonly ArrayBufferWriter<byte> _pending = new();
    private int _scanFrom;

    public LineStream(Stream stream) => _stream = stream;

    public Stream BaseStream => _stream;

    /// <summary>Read the next line without its terminator, or null at end of stream.</summary>
    public async Task<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryExtractLine(out var line))
                return line;
            if (_pending.WrittenCount > MaxLineBytes)
                throw new InvalidDataException("Received a message that is too large.");
            var read = await _stream.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
            if (read == 0)
                return null;
            _pending.Write(_readBuffer.AsSpan(0, read));
        }
    }

    private bool TryExtractLine(out byte[] line)
    {
        var written = _pending.WrittenSpan;
        var idx = written[_scanFrom..].IndexOf((byte)'\n');
        if (idx < 0)
        {
            _scanFrom = written.Length;
            line = Array.Empty<byte>();
            return false;
        }
        var end = _scanFrom + idx;
        line = written[..end].ToArray();
        var rest = written[(end + 1)..].ToArray();
        _pending.Clear();
        _pending.Write(rest);
        _scanFrom = 0;
        if (line.Length > 0 && line[^1] == 13) // carriage return
            Array.Resize(ref line, line.Length - 1);
        return true;
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing a broken connection may throw; it is closed either way.
        }
    }
}
