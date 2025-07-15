using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

const string ProxyClientName = "ProxyClient";
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddCors()
    .AddHttpClient(ProxyClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // Add ErrorResponse to the JSON TypeInfoResolver
    options.SerializerOptions.TypeInfoResolverChain.Add(ErrorResponseSerializer.Default);
});

builder.Services.Add(ServiceDescriptor.Scoped<ICacheService, CacheService>());
// TODO: Add a hosted service to periodically clean up expired cache entries

var app = builder.Build();
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

ulong requestId = 0;

app.MapGet("/", async (IHttpClientFactory http, ICacheService cacheService, CancellationToken token, [FromQuery(Name = "u")] string remoteUrl = "") =>
{
    var id = Interlocked.Increment(ref requestId);
    using var _ = app.Logger.BeginScope("RequestId: {Id}", id);

    if (string.IsNullOrEmpty(remoteUrl))
        return Results.BadRequest<ErrorResponse>(new() { RequestId = id, Error = "Parameter 'u' is required." });

    var cacheItem = await cacheService.GetAsync("GET", remoteUrl, token);
    if (cacheItem is not null)
    {
        app.Logger.LogInformation("[{Id}] Cache hit for {RemoteUrl}, returning cached response", id, remoteUrl);
        return Results.Bytes(cacheItem.Content, cacheItem.ContentType);
    }

    app.Logger.LogInformation("[{Id}] Proxying GET request to {RemoteUrl}", id, remoteUrl);
    var proxy = http.CreateClient(ProxyClientName);

    try
    {
        var response = await proxy.GetAsync(remoteUrl, token);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(token);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        app.Logger.LogInformation("[{Id}] Received {ContentLength} bytes of type {ContentType} from {RemoteUrl}", id, content.Length, contentType, remoteUrl);

        // Cache the response
        await cacheService.SetOrUpdateAsync(response, "GET", remoteUrl, token);

        // TODO: How to use status code from the response?
        return Results.Bytes(content, contentType);
    }
    catch (HttpRequestException ex)
    {
        app.Logger.LogError(ex, "[{Id}] Error while proxying request to {RemoteUrl}", id, remoteUrl);
        return Results.BadRequest<ErrorResponse>(new()
        {
            RequestId = id,
            Error = "Request to remote endpoint failed",
            Details = ex.Message
        });
    }
});

var cacheStatus = app.Services.GetRequiredService<ICacheService>().GetStatus();
if (cacheStatus.IsEnabled)
    app.Logger.LogInformation("Cache is enabled at path: {CachePath}", cacheStatus.Path);
else
    app.Logger.LogInformation("Cache is disabled");

app.Run();

internal interface ICacheService
{
    (bool IsEnabled, string Path) GetStatus();
    Task<CachedResponse?> GetAsync(string method, string requestUrl, CancellationToken cancellationToken);
    Task<CachedResponse> SetOrUpdateAsync(HttpResponseMessage response, string method, string requestUrl, CancellationToken cancellationToken);
    Task<CachedResponse> SetOrUpdateAsync(CachedResponse response, CancellationToken cancellationToken);
    /// <summary> Removes the cached response if it has expired. Returns true if the response was removed, false otherwise.</summary>
    bool RemoveIfExpired(CachedResponse response);
    void Remove(string method, string requestUrl);
}

internal class CacheService : ICacheService
{
    private readonly string _cachePath;
    private readonly TimeSpan _cacheSlidingAge;
    private readonly bool _cacheEnabled;

    public CacheService(IConfiguration configuration)
    {
        _cacheEnabled = configuration.GetValue("ProxyLight:Cache:Enabled", true);
        _cachePath = configuration.GetValue("ProxyLight:Cache:Path", string.Empty);
        _cacheSlidingAge = configuration.GetValue("ProxyLight:Cache:SlidingAge", TimeSpan.FromMinutes(30));

        if (_cacheEnabled)
        {
            if (string.IsNullOrEmpty(_cachePath))
                throw new InvalidOperationException("Cache path must be specified when cache is enabled.");
            if (!Directory.Exists(_cachePath))
                Directory.CreateDirectory(_cachePath);
        }
    }

    public (bool IsEnabled, string Path) GetStatus() => (_cacheEnabled, _cachePath);

    public async Task<CachedResponse?> GetAsync(string method, string requestUrl, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled)
            return null;

        var cacheKey = GetCacheKey(method, requestUrl);
        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");
        if (!File.Exists(cacheFilePath))
            return null;

        CachedResponse? cacheItem;
        try
        {
            using (var fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                cacheItem = await JsonSerializer.DeserializeAsync(fileStream, CachedResponseSerializer.Default.CachedResponse, cancellationToken);
        }
        catch
        {
            Remove(method, requestUrl);
            return null;
        }

        if (cacheItem is null || RemoveIfExpired(cacheItem))
            return null;

        return await SetOrUpdateAsync(cacheItem, cancellationToken);
    }

    public async Task<CachedResponse> SetOrUpdateAsync(HttpResponseMessage response, string method, string requestUrl, CancellationToken token)
    {
        var cacheItem = new CachedResponse
        {
            Timestamp = default,
            Method = method.ToUpperInvariant(),
            RequestUrl = requestUrl,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            Content = await response.Content.ReadAsByteArrayAsync(token)
        };
        return await SetOrUpdateAsync(cacheItem, token);
    }

    public async Task<CachedResponse> SetOrUpdateAsync(CachedResponse response, CancellationToken token)
    {
        response.Timestamp = DateTimeOffset.UtcNow;
        if (!_cacheEnabled)
            return response;

        var cacheKey = GetCacheKey(response.Method, response.RequestUrl);
        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");

        using (var fileStream = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(fileStream, response, CachedResponseSerializer.Default.CachedResponse, token);

        return response;
    }

    public bool RemoveIfExpired(CachedResponse response)
    {
        if (response.Timestamp + _cacheSlidingAge >= DateTimeOffset.UtcNow)
            return false;

        // If the cached response is older than the sliding age, remove it
        Remove(response.Method, response.RequestUrl);
        return true;
    }

    public void Remove(string method, string requestUrl)
    {
        var cacheKey = GetCacheKey(method, requestUrl);
        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");
        if (File.Exists(cacheFilePath))
            File.Delete(cacheFilePath);
    }

    private static string GetCacheKey(string method, string requestUrl)
        => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}:{requestUrl}")));
}

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

[JsonSerializable(type: typeof(CachedResponse))]
internal partial class CachedResponseSerializer : JsonSerializerContext
{
}
