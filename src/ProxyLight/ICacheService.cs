namespace ProxyLight;

internal interface ICacheService
{
    (bool IsEnabled, string Path) GetStatus();
    Task<CachedResponse?> GetAsync(string method, string requestUrl, CancellationToken cancellationToken);
    Task SetOrUpdateAsync(HttpRequestMessage request, HttpContent responseContent, CancellationToken token);
    Task SetOrUpdateAsync(HttpResponseMessage response, string method, string requestUrl, CancellationToken token);
    Task SetOrUpdateAsync(CachedResponse response, CancellationToken token);
    /// <summary> Removes the cached response if it has expired. Returns true if the response was removed, false otherwise.</summary>
    bool RemoveIfExpired(CachedResponse response);
    void Remove(string method, string requestUrl);
    int PruneCache();
}
