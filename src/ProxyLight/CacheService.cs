using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProxyLight;

internal class CacheService : ICacheService
{
    private readonly string _cachePath;
    private readonly TimeSpan _cacheSlidingAge;
    private readonly bool _cacheEnabled;
    private readonly TimeProvider _timeProvider;

    public CacheService(IConfiguration configuration, TimeProvider timeProvider)
    {
        _cacheEnabled = configuration.GetValue("ProxyLight:Cache:Enabled", true);
        _cachePath = configuration.GetValue("ProxyLight:Cache:Path", string.Empty);
        _cacheSlidingAge = configuration.GetValue("ProxyLight:Cache:SlidingAge", TimeSpan.FromMinutes(30));
        _timeProvider = timeProvider;

        if (_cacheEnabled)
        {
            if (string.IsNullOrEmpty(_cachePath))
                throw new InvalidOperationException("Cache path must be specified when cache is enabled.");
            if (!Directory.Exists(_cachePath))
                Directory.CreateDirectory(_cachePath);
        }
    }

    public (bool IsEnabled, string Path) GetStatus() => (_cacheEnabled, _cachePath);

    public async Task<CachedResponse?> GetAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled)
            return null;

        ArgumentNullException.ThrowIfNull(request?.RequestUri);

        return await GetAsync(request.Method.Method, request.RequestUri.ToString(), cancellationToken);
    }

    private async Task<CachedResponse?> GetAsync(string method, string requestUrl, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled)
            return null;

        ArgumentException.ThrowIfNullOrEmpty(requestUrl);

        var cacheKey = GetCacheKey(method, requestUrl);
        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");
        if (!File.Exists(cacheFilePath))
            return null;

        CachedResponse? cacheItem;
        try
        {
            using var fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            cacheItem = await JsonSerializer.DeserializeAsync(fileStream, CachedResponseSerializer.Default.CachedResponse, cancellationToken);
        }
        catch
        {
            Remove(method, requestUrl);
            return null;
        }

        if (cacheItem is null || RemoveIfExpired(cacheItem))
            return null;

        await SetOrUpdateAsync(cacheItem, cancellationToken);
        return cacheItem;
    }

    public async Task SetOrUpdateAsync(HttpRequestMessage request, HttpContent responseContent, CancellationToken token)
    {
        if (!_cacheEnabled)
            return;

        ArgumentNullException.ThrowIfNull(request?.RequestUri);

        var cacheItem = new CachedResponse
        {
            Timestamp = default,
            Method = request.Method.Method,
            RequestUrl = request.RequestUri.ToString(),
            ContentType = responseContent.Headers.ContentType?.ToString() ?? "application/octet-stream",
            Content = await responseContent.ReadAsByteArrayAsync(token)
        };
        await SetOrUpdateAsync(cacheItem, token);
    }

    private async Task SetOrUpdateAsync(CachedResponse response, CancellationToken token)
    {
        if (!_cacheEnabled)
            return;

        response.Timestamp = _timeProvider.GetUtcNow();

        var cacheKey = GetCacheKey(response.Method, response.RequestUrl);
        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");

        using var fileStream = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(fileStream, response, CachedResponseSerializer.Default.CachedResponse, token);
    }

    public bool RemoveIfExpired(CachedResponse response)
    {
        if (response.Timestamp + _cacheSlidingAge >= _timeProvider.GetUtcNow())
            return false;

        // If the cached response is older than the sliding age, remove it
        Remove(response.Method, response.RequestUrl);
        return true;
    }

    public void Remove(string method, string requestUrl)
    {
        var cacheKey = GetCacheKey(method, requestUrl);
        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");
        if (File.Exists(cacheFilePath))
            File.Delete(cacheFilePath);
    }

    private static string GetCacheKey(string method, string requestUrl)
        => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}:{requestUrl}")));

    public int PruneCache()
    {
        if (!_cacheEnabled || string.IsNullOrEmpty(_cachePath) || !Directory.Exists(_cachePath))
            return 0;

        var files = Directory.GetFiles(_cachePath, "*.json");
        int removedCount = 0;
        foreach (var file in files)
        {
            try
            {
                var lastWriteTime = File.GetLastWriteTimeUtc(file);
                if (_timeProvider.GetUtcNow() - lastWriteTime > _cacheSlidingAge)
                {
                    File.Delete(file);
                    removedCount++;
                }
            }
            catch (Exception ex)
            {
                // Log the error but continue processing other files
                Console.Error.WriteLine($"Error removing file {file}: {ex.Message}");
            }
        }

        return removedCount;
    }
}
