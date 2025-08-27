using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

const string ProxyClientName = "ProxyClient";
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddCors()
    .AddHttpClient(ProxyClientName, client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // Add ErrorResponse to the JSON TypeInfoResolver
    options.SerializerOptions.TypeInfoResolverChain.Add(ErrorResponseSerializer.Default);
});

builder.Services.Add(ServiceDescriptor.Singleton<ICacheService, CacheService>());
// TODO: Add a hosted service to periodically clean up expired cache entries

var app = builder.Build();
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

// Prune cache on startup
var prunedItemCount = app.Services.GetRequiredService<ICacheService>().PruneCache();
if (prunedItemCount > 0)
    app.Logger.LogInformation("Pruned {Count} cached items", prunedItemCount);

ulong requestId = 0;

app.MapGet("/", async (IHttpClientFactory http, ICacheService cacheService, CancellationToken token, [FromQuery(Name = "u")] string remoteUrl = "") =>
{
    var id = Interlocked.Increment(ref requestId);
    // TODO: Move prune logic to a background thread and/or inside the cache service
    var pruneFreq = app.Configuration.GetValue("ProxyLight:Cache:PruneFrequency", 100ul);
    if (id % pruneFreq == 0)
    {
        var prunedItemCount = cacheService.PruneCache();
        app.Logger.LogInformation("Pruned {Count} cached items", prunedItemCount);
    }

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
    using var proxy = http.CreateClient(ProxyClientName);

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
