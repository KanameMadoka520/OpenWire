using System.Text;

namespace OpenWire.Core.Ipc;

/// <summary>
/// Wraps a duplex byte stream (a named-pipe connection) and exchanges
/// newline-framed UTF-8 JSON <see cref="IpcMessage"/>s over it.
/// Reads and writes are each internally serialized so a channel may be shared
/// between a request/response loop and a background event pump.
/// </summary>
public sealed class IpcChannel : IDisposable
{
    private const byte Delimiter = (byte)'\n';

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private byte[] _buffer = new byte[16 * 1024];
    private int _bufStart;
    private int _bufEnd;
    private bool _disposed;

    public IpcChannel(Stream stream) => _stream = stream;

    /// <summary>Serialize and send one message. Thread-safe against concurrent sends.</summary>
    public async Task SendAsync(IpcMessage message, CancellationToken ct = default)
    {
        var json = IpcJson.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);
        var framed = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, framed, 0, payload.Length);
        framed[payload.Length] = Delimiter;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(framed, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Read the next framed message, or null on end-of-stream.
    /// A single reader must own this method (it is not re-entrant).
    /// </summary>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var line = new List<byte>(256);

        while (true)
        {
            // Consume any buffered bytes, looking for the frame delimiter.
            if (_bufStart < _bufEnd)
            {
                int idx = Array.IndexOf(_buffer, Delimiter, _bufStart, _bufEnd - _bufStart);
                if (idx >= 0)
                {
                    for (int i = _bufStart; i < idx; i++) line.Add(_buffer[i]);
                    _bufStart = idx + 1;
                    return Decode(line);
                }

                for (int i = _bufStart; i < _bufEnd; i++) line.Add(_buffer[i]);
                _bufStart = _bufEnd = 0;
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
                return line.Count == 0 ? null : Decode(line);

            _bufStart = 0;
            _bufEnd = read;
        }
    }

    private static IpcMessage? Decode(List<byte> line)
    {
        if (line.Count == 0) return null;
        return IpcJson.Deserialize(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        _stream.Dispose();
    }
}
