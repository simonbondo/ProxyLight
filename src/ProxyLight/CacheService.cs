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

    public async Task<CachedResponse?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled)
            return null;

        ArgumentException.ThrowIfNullOrEmpty(cacheKey);

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
            Remove(cacheKey);
            return null;
        }

        if (cacheItem is null || RemoveIfExpired(cacheKey, cacheItem))
            return null;

        await SetOrUpdateAsync(cacheKey, cacheItem, cancellationToken);
        return cacheItem;
    }

    public async Task SetOrUpdateAsync(string cacheKey, HttpRequestMessage request, HttpContent responseContent, CancellationToken token)
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
        await SetOrUpdateAsync(cacheKey, cacheItem, token);
    }

    private async Task SetOrUpdateAsync(string cacheKey, CachedResponse response, CancellationToken token)
    {
        if (!_cacheEnabled)
            return;

        response.Timestamp = _timeProvider.GetUtcNow();

        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");

        using var fileStream = new FileStream(cacheFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(fileStream, response, CachedResponseSerializer.Default.CachedResponse, token);
    }

    public bool RemoveIfExpired(string cacheKey, CachedResponse response)
    {
        if (response.Timestamp + _cacheSlidingAge >= _timeProvider.GetUtcNow())
            return false;

        // If the cached response is older than the sliding age, remove it
        Remove(cacheKey);
        return true;
    }

    public void Remove(string cacheKey)
    {
        var cacheFilePath = Path.Combine(_cachePath, $"{cacheKey}.json");
        if (File.Exists(cacheFilePath))
            File.Delete(cacheFilePath);
    }

    public string GetCacheKey(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request?.RequestUri);
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{request.Method.Method}:{request.RequestUri}")));
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
