using System.Runtime.InteropServices;
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

    private readonly byte[] _readBuf = new byte[64 * 1024];
    private readonly List<byte> _pending = new(64 * 1024);
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
        while (true)
        {
            int nl = _pending.IndexOf(Delimiter);
            if (nl == 0)
            {
                _pending.RemoveAt(0); // stray empty frame — skip
                continue;
            }
            if (nl > 0)
            {
                var msg = IpcJson.Deserialize(CollectionsMarshal.AsSpan(_pending)[..nl]);
                _pending.RemoveRange(0, nl + 1);
                return msg;
            }

            int read = await _stream.ReadAsync(_readBuf.AsMemory(0, _readBuf.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (_pending.Count == 0) return null;
                var msg = IpcJson.Deserialize(CollectionsMarshal.AsSpan(_pending));
                _pending.Clear();
                return msg;
            }

            _pending.AddRange(_readBuf.AsSpan(0, read));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        _stream.Dispose();
    }
}
