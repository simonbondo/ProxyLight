namespace ProxyLight;

internal interface ICacheService
{
    (bool IsEnabled, string Path) GetStatus();
    string GetCacheKey(HttpRequestMessage request);
    Task<CachedResponse?> GetAsync(string cacheKey, CancellationToken cancellationToken);
    Task SetOrUpdateAsync(string cacheKey, HttpRequestMessage request, HttpContent responseContent, CancellationToken token);
    /// <summary> Removes the cached response if it has expired. Returns true if the response was removed, false otherwise.</summary>
    bool RemoveIfExpired(string cacheKey, CachedResponse response);
    void Remove(string cacheKey);
    int PruneCache();
}
