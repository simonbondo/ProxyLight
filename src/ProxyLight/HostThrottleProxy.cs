using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ProxyLight;

public class HostThrottleProxy : IHostThrottleProxy
{
    // TODO: Make configurable
    private const int ConcurrencyPerHost = 4;
    private const int ChannelCapacityPerHost = 25;
    private const int ChannelCompletionTimeoutSeconds = 30;
    private const string HttpClientName = "ProxyClient";
    private readonly ConcurrentDictionary<string, ChannelMeta> _channels = new();
    private readonly ILogger<HostThrottleProxy> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public HostThrottleProxy(ILogger<HostThrottleProxy> logger, IServiceScopeFactory serviceScopeFactory, TimeProvider timeProvider)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task<HttpContent> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
            throw new InvalidOperationException("Request URI required.");

        var writer = GetChannelWriter(request.RequestUri.Host);

        var meta = new ChannelItem(request, cancellationToken);

        _logger.LogInformation("Enqueuing request for {RequestUrl}", request.RequestUri);

        // Wait for the channel to have room and enqueue the request for processing
        await writer.WriteAsync(meta, cancellationToken);

        // Throw if request was cancelled, before it could be enqueued
        cancellationToken.ThrowIfCancellationRequested();

        // Wait for the result
        return await meta.GetResultTask();
    }

    public void ChannelMaintenance(TimeSpan maxIdleTime)
    {
        var idleThreshold = _timeProvider.GetUtcNow() - maxIdleTime;
        KeyValuePair<string, ChannelMeta>? GetIdleChannel()
            => _channels.FirstOrDefault(kvp => kvp.Value.GetLastActivityTimeStamp() < idleThreshold);

        while (GetIdleChannel() is { } kvp)
        {
            _logger.LogInformation("Signalling idle channel for host {Host} as completed", kvp.Key);

            if (_channels.TryRemove(kvp.Key, out var meta))
            {
                // Force the channel to complete after a delay, if it doesn't close by completion
                meta.CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(ChannelCompletionTimeoutSeconds));
                meta.Writer.TryComplete();
            }
        }
    }

    private ChannelWriter<ChannelItem> GetChannelWriter(string host)
    {
        var meta = _channels.GetOrAdd(host, OpenChannel);
        meta.UpdateActivity();
        return meta.Writer;
    }

    private ChannelMeta OpenChannel(string host)
    {
        _logger.LogInformation("Creating new channel for host {Host}, capacity:{Capacity}, Concurrency:{Concurrency}", host, ChannelCapacityPerHost, ConcurrencyPerHost);

        var channel = Channel.CreateBounded<ChannelItem>(new BoundedChannelOptions(ChannelCapacityPerHost)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        var meta = new ChannelMeta(channel, _timeProvider);
        for (var i = 0; i < ConcurrencyPerHost; i++)
            Task.Factory.StartNew(() => ProcessChannelAsync(host, channel.Reader, meta.CancellationTokenSource.Token), TaskCreationOptions.LongRunning);

        return meta;
    }

    private async Task ProcessChannelAsync(string host, ChannelReader<ChannelItem> reader, CancellationToken channelCancellationToken)
    {
        _logger.LogDebug("Starting channel processor for host {Host}", host);

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        try
        {
            await foreach (var item in reader.ReadAllAsync(channelCancellationToken))
            {
                var token = CancellationTokenSource.CreateLinkedTokenSource(channelCancellationToken, item.RequestCancellationToken).Token;
                if (token.IsCancellationRequested)
                {
                    item.CancelResult(token);
                    continue;
                }

                var http = httpFactory.CreateClient(HttpClientName);

                try
                {
                    _logger.LogInformation("Forwarding request to {RequestUrl}", item.Request.RequestUri);

                    var response = await http.SendAsync(item.Request, token);
                    response.EnsureSuccessStatusCode();
                    item.SetResult(response.Content);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _logger.LogDebug("Forwarded request to {RequestUrl} was cancelled", item.Request.RequestUri);
                    item.CancelResult(token);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Forwarded request to {RequestUrl} failed: StatusCode:{StatusCode}", item.Request.RequestUri, ex.StatusCode);
                    item.FailResult(ex);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Forwarded request to {RequestUrl} failed", item.Request.RequestUri);
                    item.FailResult(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Channel processing was cancelled
        }

        _logger.LogDebug("Stopping channel processor for host {Host}, Cancelled:{IsCancellationRequested}", host, channelCancellationToken.IsCancellationRequested);
    }

    private class ChannelMeta
    {
        private readonly Channel<ChannelItem> _channel;
        private readonly TimeProvider _timeProvider;
        private long _lastActivityTimestamp;

        public ChannelMeta(Channel<ChannelItem> channel, TimeProvider timeProvider)
        {
            _channel = channel;
            _timeProvider = timeProvider;
            _lastActivityTimestamp = timeProvider.GetUtcNow().DateTime.ToBinary();
        }

        public ChannelWriter<ChannelItem> Writer => _channel.Writer;
        public CancellationTokenSource CancellationTokenSource { get; } = new();
        public void UpdateActivity() => Interlocked.Exchange(ref _lastActivityTimestamp, _timeProvider.GetUtcNow().DateTime.ToBinary());
        public DateTime GetLastActivityTimeStamp() => DateTime.FromBinary(Interlocked.Read(ref _lastActivityTimestamp));
    }

    private class ChannelItem
    {
        private readonly TaskCompletionSource<HttpContent> taskCompletionSource = new();

        public ChannelItem(HttpRequestMessage request, CancellationToken requestCancellationToken)
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
