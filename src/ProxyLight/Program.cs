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

app.MapGet("/", async (IHttpClientFactory http, CancellationToken token, [FromQuery(Name = "u")] string remoteUrl = "") =>
{
    if (string.IsNullOrEmpty(remoteUrl))
        return Results.BadRequest<ErrorResponse>(new() { Error = "Parameter 'u' is required." });

    var proxy = http.CreateClient(ProxyClientName);

    try
    {
        var response = await proxy.GetAsync(remoteUrl, token);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(token);
        return Results.Bytes(content, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    }
    catch (HttpRequestException e)
    {
        return Results.BadRequest<ErrorResponse>(new()
        {
            Error = "Request to remove endpoint failed",
            Details = e.Message
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
    public required string Error { get; set; }
    public string? Details { get; set; }
}
