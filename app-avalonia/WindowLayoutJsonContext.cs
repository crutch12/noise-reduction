using System.Text.Json.Serialization;

namespace NoiseReduction.Avalonia
{
    [JsonSerializable(typeof(WindowGeometry))]
    internal partial class WindowLayoutJsonContext : JsonSerializerContext
    {
    }
}
