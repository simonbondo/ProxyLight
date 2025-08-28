namespace ProxyLight;

public interface IHostThrottleProxy
{
    Task<HttpContent> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
