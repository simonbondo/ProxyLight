namespace ProxyLight;

public interface IHostThrottleProxy
{
    Task<HttpContent> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    void ChannelMaintenance(DateTime idleThreshold);
}
