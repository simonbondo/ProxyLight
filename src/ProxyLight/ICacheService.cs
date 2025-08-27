namespace ProxyLight;

internal interface ICacheService
{
    (bool IsEnabled, string Path) GetStatus();
    Task<CachedResponse?> GetAsync(string method, string requestUrl, CancellationToken cancellationToken);
    Task<CachedResponse> SetOrUpdateAsync(HttpResponseMessage response, string method, string requestUrl, CancellationToken cancellationToken);
    Task<CachedResponse> SetOrUpdateAsync(CachedResponse response, CancellationToken cancellationToken);
    /// <summary> Removes the cached response if it has expired. Returns true if the response was removed, false otherwise.</summary>
    bool RemoveIfExpired(CachedResponse response);
    void Remove(string method, string requestUrl);
    int PruneCache();
}
