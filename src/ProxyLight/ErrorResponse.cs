using System.Text.Json.Serialization;

namespace ProxyLight;

internal class ErrorResponse
{
    public required decimal RequestId { get; set; }
    public required string Error { get; set; }
    public string? Details { get; set; }
}

[JsonSerializable(type: typeof(ErrorResponse))]
internal partial class ErrorResponseSerializer : JsonSerializerContext
{
}
