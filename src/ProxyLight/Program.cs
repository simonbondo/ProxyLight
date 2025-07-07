using Microsoft.AspNetCore.Mvc;

const string ProxyClientName = "ProxyClient";
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddCors()
    .AddHttpClient(ProxyClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });

var app = builder.Build();
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

app.MapGet("/", async (IHttpClientFactory http, CancellationToken token, [FromQuery(Name = "u")] string remoteUrl = "") =>
{
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
        return Results.Problem(e.Message);
    }
});

app.Run();
