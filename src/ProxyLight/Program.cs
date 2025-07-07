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

// Add ErrorResponse to the JSON TypeInfoResolver
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.TypeInfoResolverChain.Add(ErrorResponseSerializer.Default);
});

var app = builder.Build();
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

ulong requestId = 0;

app.MapGet("/", async (IHttpClientFactory http, CancellationToken token, [FromQuery(Name = "u")] string remoteUrl = "") =>
{
    var id = Interlocked.Increment(ref requestId);
    using var _ = app.Logger.BeginScope("RequestId: {Id}", id);

    if (string.IsNullOrEmpty(remoteUrl))
        return Results.BadRequest<ErrorResponse>(new() { RequestId = id, Error = "Parameter 'u' is required." });

    app.Logger.LogInformation("[{Id}] Proxying GET request to {RemoteUrl}", id, remoteUrl);

    var proxy = http.CreateClient(ProxyClientName);

    try
    {
        var response = await proxy.GetAsync(remoteUrl, token);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(token);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        app.Logger.LogInformation("[{Id}] Received {ContentLength} bytes of type {ContentType} from {RemoteUrl}", id, content.Length, contentType, remoteUrl);

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

app.Run();

[JsonSerializable(type: typeof(ErrorResponse))]
internal partial class ErrorResponseSerializer : JsonSerializerContext
{
}

internal class ErrorResponse
{
    public required decimal RequestId { get; set; }
    public required string Error { get; set; }
    public string? Details { get; set; }
}
