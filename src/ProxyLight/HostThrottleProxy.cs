using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ProxyLight;

public class HostThrottleProxy : IHostThrottleProxy
{
    // TODO: Make configurable
    private const int ConcurrencyPerHost = 4;
    private const int ChannelCapacityPerHost = 25;
    private readonly ConcurrentDictionary<string, Channel<ChannelItemMeta>> _channels = new();
    private readonly IServiceScopeFactory serviceScopeFactory;

    public HostThrottleProxy(IServiceScopeFactory serviceScopeFactory)
    {
        this.serviceScopeFactory = serviceScopeFactory;
    }

    public async Task<HttpContent> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
            throw new InvalidOperationException("Request URI required.");

        var writer = GetChannelWriter(request.RequestUri.Host);

        var meta = new ChannelItemMeta(request, cancellationToken);

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
        var channel = Channel.CreateBounded<ChannelItemMeta>(new BoundedChannelOptions(ChannelCapacityPerHost)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        // TODO: Log channel creation?

        // TODO: Implement a way to complete (and thereby remove) these channels when idle for a certain period of time
        var cts = new CancellationTokenSource();
        for (int i = 0; i < ConcurrencyPerHost; i++)
            Task.Factory.StartNew(() => ProcessChannelAsync(channel.Reader, cts.Token), TaskCreationOptions.LongRunning);

        return channel;
    }

    private async Task ProcessChannelAsync(ChannelReader<ChannelItemMeta> reader, CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        const string ProxyClientName = "ProxyClient";

        try
        {
            await foreach (var meta in reader.ReadAllAsync(cancellationToken))
            {
                // TODO: Logging
                var http = httpFactory.CreateClient(ProxyClientName);

                try
                {
                    var response = await http.SendAsync(meta.Request, meta.RequestCancellationToken);
                    response.EnsureSuccessStatusCode();
                    meta.SetResult(response.Content);
                }
                catch (Exception ex)
                {
                    meta.FailResult(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Channel processing was cancelled
        }
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

public interface IHostThrottleProxy
{
    Task<HttpContent> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
