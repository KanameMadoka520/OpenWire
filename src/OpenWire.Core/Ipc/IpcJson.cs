using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenWire.Core.Ipc;

/// <summary>Shared serializer configuration for the IPC wire format.</summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IpcMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static IpcMessage? Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<IpcMessage>(utf8Json, Options);
}
