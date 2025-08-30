namespace ProxyLight;

internal interface ICacheService
{
    (bool IsEnabled, string Path) GetStatus();
    Task<CachedResponse?> GetAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    Task SetOrUpdateAsync(HttpRequestMessage request, HttpContent responseContent, CancellationToken token);
    /// <summary> Removes the cached response if it has expired. Returns true if the response was removed, false otherwise.</summary>
    bool RemoveIfExpired(CachedResponse response);
    void Remove(string method, string requestUrl);
    int PruneCache();
}
