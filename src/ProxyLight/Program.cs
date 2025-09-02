using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

const string ProxyClientName = "ProxyClient";
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddSingleton(TimeProvider.System)
    .AddCors()
    .AddHttpClient(ProxyClientName, client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler((handler, provider) =>
    {
        if (handler is HttpClientHandler clientHandler)
        {
            // Disable built-in cookie handling
            clientHandler.UseCookies = false;
        }
    }).Services
    .ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // Add ErrorResponse to the JSON TypeInfoResolver
        options.SerializerOptions.TypeInfoResolverChain.Add(ErrorResponseSerializer.Default);
    })
    .AddSingleton<ICacheService, CacheService>()
    .AddSingleton<IHostThrottleProxy, HostThrottleProxy>();

var app = builder.Build();
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

ulong requestId = 0;

app.MapGet("/", async (HttpRequest request, IHttpClientFactory http, ICacheService cacheService, IHostThrottleProxy hostThrottleProxy, CancellationToken token, [FromQuery(Name = "u")] string remoteUrl = "") =>
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

    var forwardRequest = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
    // TODO: Copy headers and other properties from the original request
    if (request.Headers.UserAgent.Count > 0)
    {
        forwardRequest.Headers.UserAgent.Clear();
        forwardRequest.Headers.UserAgent.ParseAdd(request.Headers.UserAgent);
    }
    if (!StringValues.IsNullOrEmpty(request.Headers.Cookie))
    {
        forwardRequest.Headers.Add("Cookie", request.Headers.Cookie.ToString());
    }

    var cacheKey = cacheService.GetCacheKey(forwardRequest);
    var cacheItem = await cacheService.GetAsync(cacheKey, token);
    if (cacheItem is not null)
    {
        app.Logger.LogInformation("[{Id}] Cache hit for {RemoteUrl}", id, remoteUrl);
        return Results.Bytes(cacheItem.Content, cacheItem.ContentType);
    }

    try
    {
        var responseContent = await hostThrottleProxy.SendRequestAsync(forwardRequest, token);

        var contentData = await responseContent.ReadAsByteArrayAsync(token);
        var contentType = responseContent.Headers.ContentType?.ToString() ?? "application/octet-stream";
        app.Logger.LogInformation("[{Id}] Received {ContentLength} bytes of type {ContentType} from {RemoteUrl}", id, contentData.Length, contentType, remoteUrl);

        // Cache the response
        await cacheService.SetOrUpdateAsync(cacheKey, forwardRequest, responseContent, token);

        // TODO: How to use status code from the response?
        return Results.Bytes(contentData, contentType);
    }
    catch (OperationCanceledException)
    {
        app.Logger.LogInformation("[{Id}] Request to {RemoteUrl} was cancelled", id, remoteUrl);
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (HttpRequestException ex)
    {
        if (ex.StatusCode is >= (HttpStatusCode)500 and < (HttpStatusCode)600)
            app.Logger.LogWarning(ex, "[{Id}] Remote error while proxying request to {RemoteUrl}: [Code:{StatusCode}] {Message}", id, remoteUrl, ex.StatusCode, ex.Message);
        else if (ex.StatusCode is >= (HttpStatusCode)400 and < (HttpStatusCode)500)
            app.Logger.LogInformation(ex, "[{Id}] Client error while proxying request to {RemoteUrl}: [Code:{StatusCode}] {Message}", id, remoteUrl, ex.StatusCode, ex.Message);
        else
            app.Logger.LogError(ex, "[{Id}] Error while proxying request to {RemoteUrl}: [Code:{StatusCode}] {Message}", id, remoteUrl, ex.StatusCode, ex.Message);
        return Results.BadRequest<ErrorResponse>(new()
        {
            RequestId = id,
            Error = "Request to remote endpoint failed",
            Details = ex.Message
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "[{Id}] Unexpected error while proxying request to {RemoteUrl}", id, remoteUrl);
        return Results.InternalServerError<ErrorResponse>(new()
        {
            RequestId = id,
            Error = "Unexpected error",
            Details = ex.Message
        });
    }
});

var cacheStatus = app.Services.GetRequiredService<ICacheService>().GetStatus();
if (cacheStatus.IsEnabled)
    app.Logger.LogInformation("Cache is enabled at path: {CachePath}", cacheStatus.Path);
else
    app.Logger.LogInformation("Cache is disabled");

// TODO: Move to HostedService
await Task.Factory.StartNew(async (state) =>
{
    var cancellationToken = (CancellationToken)state!;
    await using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
    var cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
    var hostThrottleProxy = scope.ServiceProvider.GetRequiredService<IHostThrottleProxy>();

    var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
    var maxIdleTime = TimeSpan.FromMinutes(30);
    do
    {
        var prunedItemCount = cacheService.PruneCache();
        if (prunedItemCount > 0)
            app.Logger.LogInformation("Pruned {Count} cached items", prunedItemCount);

        // Perform channel maintenance for channels idle for more than 30 minutes
        hostThrottleProxy.ChannelMaintenance(maxIdleTime);
    } while (await timer.WaitForNextTickAsync(cancellationToken));
}, app.Lifetime.ApplicationStopping, TaskCreationOptions.LongRunning);

app.Run();
