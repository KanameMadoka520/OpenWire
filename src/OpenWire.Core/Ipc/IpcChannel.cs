using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace OpenWire.Core.Ipc;

/// <summary>
/// Wraps a duplex byte stream (a named-pipe connection) and exchanges
/// length-prefixed UTF-8 JSON <see cref="IpcMessage"/>s over it.
/// Reads and writes are each internally serialized so a channel may be shared
/// between a request/response loop and a background event pump.
/// </summary>
public sealed class IpcChannel : IDisposable
{
    private const int LengthPrefixBytes = sizeof(int);
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _lengthPrefix = new byte[LengthPrefixBytes];
    private readonly byte[] _writePrefix = new byte[LengthPrefixBytes];

    // Reusable send buffer: per-message string + byte[] allocations put every large
    // response (usage/hardware snapshots easily exceed the 85 KB LOH threshold) on
    // the large-object heap once a second, fragmenting it without bound on a
    // long-lived engine. The writer grows once to the largest response and stays.
    private readonly ArrayBufferWriter<byte> _sendBuf = new(64 * 1024);
    private Utf8JsonWriter? _sendJson;
    private bool _disposed;

    public IpcChannel(Stream stream) => _stream = stream;

    /// <summary>Serialize and send one message. Thread-safe against concurrent sends.</summary>
    public async Task SendAsync(IpcMessage message, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sendBuf.ResetWrittenCount();
            _sendJson ??= new Utf8JsonWriter(_sendBuf, new JsonWriterOptions { SkipValidation = true });
            _sendJson.Reset(_sendBuf);
            JsonSerializer.Serialize(_sendJson, message, IpcJson.Options);
            _sendJson.Flush();

            int payloadBytes = _sendBuf.WrittenCount;
            if (payloadBytes <= 0 || payloadBytes > MaxMessageBytes)
                throw new InvalidDataException($"IPC message is outside the 1..{MaxMessageBytes} byte limit.");
            BinaryPrimitives.WriteInt32LittleEndian(_writePrefix, payloadBytes);

            await _stream.WriteAsync(_writePrefix, ct).ConfigureAwait(false);
            await _stream.WriteAsync(_sendBuf.WrittenMemory, ct).ConfigureAwait(false);
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
        if (!await ReadPrefixAsync(ct).ConfigureAwait(false)) return null;

        int payloadBytes = BinaryPrimitives.ReadInt32LittleEndian(_lengthPrefix);
        if (payloadBytes <= 0 || payloadBytes > MaxMessageBytes)
            throw new InvalidDataException($"IPC frame length {payloadBytes} is outside the allowed range.");

        byte[] payload = ArrayPool<byte>.Shared.Rent(payloadBytes);
        try
        {
            int offset = 0;
            while (offset < payloadBytes)
            {
                int read = await _stream.ReadAsync(payload.AsMemory(offset, payloadBytes - offset), ct).ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("IPC stream ended inside a message payload.");
                offset += read;
            }
            return IpcJson.Deserialize(payload.AsSpan(0, payloadBytes))
                ?? throw new InvalidDataException("IPC payload cannot be null.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload, clearArray: true);
        }
    }

    private async Task<bool> ReadPrefixAsync(CancellationToken ct)
    {
        int offset = 0;
        while (offset < LengthPrefixBytes)
        {
            int read = await _stream.ReadAsync(
                _lengthPrefix.AsMemory(offset, LengthPrefixBytes - offset),
                ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new InvalidDataException("IPC stream ended inside a frame prefix.");
            }
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sendJson?.Dispose();
        _writeLock.Dispose();
        _stream.Dispose();
    }
}
