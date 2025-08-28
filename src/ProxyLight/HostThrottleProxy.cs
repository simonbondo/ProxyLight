using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ProxyLight;

public class HostThrottleProxy : IHostThrottleProxy
{
    // TODO: Make configurable
    private const int ConcurrencyPerHost = 4;
    private const int ChannelCapacityPerHost = 25;
    private const string HttpClientName = "ProxyClient";
    private readonly ConcurrentDictionary<string, Channel<ChannelItemMeta>> _channels = new();
    private readonly ILogger<HostThrottleProxy> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public HostThrottleProxy(ILogger<HostThrottleProxy> logger, IServiceScopeFactory serviceScopeFactory)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task<HttpContent> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
            throw new InvalidOperationException("Request URI required.");

        var writer = GetChannelWriter(request.RequestUri.Host);

        var meta = new ChannelItemMeta(request, cancellationToken);

        _logger.LogInformation("Enqueuing request for {RequestUrl}", request.RequestUri);

        // Wait for the channel to have room and enqueue the request for processing
        await writer.WriteAsync(meta, cancellationToken);

        // Throw if request was cancelled, before it could be enqueued
        cancellationToken.ThrowIfCancellationRequested();

        // Wait for the result
        return await meta.GetResultTask();
    }

    private ChannelWriter<ChannelItemMeta> GetChannelWriter(string host)
        => _channels.GetOrAdd(host, OpenChannel).Writer;

    private Channel<ChannelItemMeta> OpenChannel(string host)
    {
        _logger.LogInformation("Creating new channel for host {Host}, capacity:{Capacity}, Concurrency:{Concurrency}", host, ChannelCapacityPerHost, ConcurrencyPerHost);

        var channel = Channel.CreateBounded<ChannelItemMeta>(new BoundedChannelOptions(ChannelCapacityPerHost)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        // TODO: Implement a way to complete (and thereby remove) these channels when idle for a certain period of time
        var cts = new CancellationTokenSource();
        for (var i = 0; i < ConcurrencyPerHost; i++)
            Task.Factory.StartNew(() => ProcessChannelAsync(host, channel.Reader, cts.Token), TaskCreationOptions.LongRunning);

        return channel;
    }

    private async Task ProcessChannelAsync(string host, ChannelReader<ChannelItemMeta> reader, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting channel processor for host {Host}", host);

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        try
        {
            await foreach (var meta in reader.ReadAllAsync(cancellationToken))
            {
                if (meta.RequestCancellationToken.IsCancellationRequested)
                {
                    meta.CancelResult(meta.RequestCancellationToken);
                    continue;
                }

                var http = httpFactory.CreateClient(HttpClientName);

                try
                {
                    _logger.LogInformation("Forwarding request to {RequestUrl}", meta.Request.RequestUri);

                    var response = await http.SendAsync(meta.Request, meta.RequestCancellationToken);
                    response.EnsureSuccessStatusCode();
                    meta.SetResult(response.Content);
                }
                catch (OperationCanceledException) when (meta.RequestCancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Forwarded request to {RequestUrl} was cancelled", meta.Request.RequestUri);
                    meta.CancelResult(meta.RequestCancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Forwarded request to {RequestUrl} failed: StatusCode:{StatusCode}", meta.Request.RequestUri, ex.StatusCode);
                    meta.FailResult(ex);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Forwarded request to {RequestUrl} failed", meta.Request.RequestUri);
                    meta.FailResult(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Channel processing was cancelled
        }

        _logger.LogDebug("Stopping channel processor for host {Host}, Cancelled:{IsCancellationRequested}", host, cancellationToken.IsCancellationRequested);
    }

    private class ChannelItemMeta
    {
        private readonly TaskCompletionSource<HttpContent> taskCompletionSource = new();

        public ChannelItemMeta(HttpRequestMessage request, CancellationToken requestCancellationToken)
        {
            Request = request;
            RequestCancellationToken = requestCancellationToken;
        }

        public HttpRequestMessage Request { get; }
        public CancellationToken RequestCancellationToken { get; }

        public Task<HttpContent> GetResultTask() => taskCompletionSource.Task;

        public void SetResult(HttpContent content) => taskCompletionSource.SetResult(content);
        public void FailResult(Exception exception) => taskCompletionSource.SetException(exception);
        public void CancelResult(CancellationToken cancellationToken = default) => taskCompletionSource.SetCanceled(cancellationToken);
    }
}
