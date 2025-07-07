using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddCors()
    .AddHttpClient("ProxyClient", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });

var app = builder.Build();
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

app.MapGet("/", async (HttpClient proxy, CancellationToken token, [FromQuery(Name = "u")] string remoteUrl) =>
{
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
