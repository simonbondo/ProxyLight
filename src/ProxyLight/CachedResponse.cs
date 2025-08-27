using System.Text.Json.Serialization;

namespace ProxyLight;

// TODO: Maybe split cache metadata and content into separate files, to make querying more efficient
internal class CachedResponse
{
    public required DateTimeOffset Timestamp { get; set; }
    public required string Method { get; set; }
    public required string RequestUrl { get; set; }
    public required string ContentType { get; set; }
    public string ContentBase64
    {
        get => Convert.ToBase64String(Content);
        set => Content = string.IsNullOrEmpty(value) ? [] : Convert.FromBase64String(value);
    }

    [JsonIgnore]
    internal byte[] Content { get; set; } = [];
}

[JsonSerializable(type: typeof(CachedResponse))]
internal partial class CachedResponseSerializer : JsonSerializerContext
{
}
