using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio.DataModel.Args;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minio.Extensions.Caching.Distributed;

public class MinioCacheOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = "aspnetcore-distributed-cache";
    public bool UseSSL { get; set; } = false;
    public string? Region { get; set; } = null;
    public bool UseFallbackCache { get; set; } = true;
}

public class MinioDistributedCache : IDistributedCache, IDisposable
{
    private readonly IMinioClient _minioClient;
    private readonly MinioCacheOptions _options;
    private readonly IMemoryCache? _fallbackCache;
    private readonly ILogger<MinioDistributedCache> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed = false;
    private bool _bucketChecked = false;

    public MinioDistributedCache(
        IOptions<MinioCacheOptions> options, 
        ILogger<MinioDistributedCache> logger,
        IMemoryCache? fallbackCache = null)
    {
        _options = options.Value;
        _logger = logger;
        _fallbackCache = _options.UseFallbackCache ? fallbackCache : null;
        
        _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null,
            WriteIndented = false
        };
        
        var clientBuilder = new MinioClient()
            .WithEndpoint(_options.Endpoint)
            .WithCredentials(_options.AccessKey, _options.SecretKey)
            .WithSSL(_options.UseSSL);

        if (!string.IsNullOrEmpty(_options.Region))
        {
            clientBuilder = clientBuilder.WithRegion(_options.Region);
        }

        _minioClient = clientBuilder.Build();
        
        _logger.LogInformation(
            "MinIO cache initialized with endpoint: {Endpoint}, bucket: {BucketName}, SSL: {UseSSL}, region: {Region}, fallback cache enabled: {UseFallback}",
            _options.Endpoint,
            _options.BucketName,
            _options.UseSSL,
            string.IsNullOrEmpty(_options.Region) ? "not specified" : _options.Region,
            _options.UseFallbackCache);
    }

    private async Task EnsureBucketExistsAsync()
    {
        if (_bucketChecked) return;
        
        try
        {
            var bucketExistsArgs = new BucketExistsArgs()
                .WithBucket(_options.BucketName);

            bool exists = await _minioClient.BucketExistsAsync(bucketExistsArgs);
            
            if (!exists)
            {
                var makeBucketArgs = new MakeBucketArgs()
                    .WithBucket(_options.BucketName);
            
                await _minioClient.MakeBucketAsync(makeBucketArgs);
                _logger.LogInformation("Created MinIO bucket: {BucketName}", _options.BucketName);
            }
            
            _bucketChecked = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to ensure bucket exists: {BucketName}. Error: {ErrorMessage}", 
                _options.BucketName, ex.Message);
        }
    }

    #region IDistributedCache Implementation

    public byte[]? Get(string key)
        => GetInternalAsync(key, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        => await GetInternalAsync(key, token);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => SetInternalAsync(key, value, options, CancellationToken.None).GetAwaiter().GetResult();

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        => await SetInternalAsync(key, value, options, token);

    public void Refresh(string key)
        => RefreshInternalAsync(key, CancellationToken.None).GetAwaiter().GetResult();

    public async Task RefreshAsync(string key, CancellationToken token = default)
        => await RefreshInternalAsync(key, token);

    public void Remove(string key)
        => RemoveInternalAsync(key, CancellationToken.None).GetAwaiter().GetResult();

    public async Task RemoveAsync(string key, CancellationToken token = default)
        => await RemoveInternalAsync(key, token);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _minioClient?.Dispose();
            _disposed = true;
        }
    }

    #endregion

    #region Private Methods

    private async Task<byte[]?> GetInternalAsync(string key, CancellationToken token)
    {
        try
        {
            await EnsureBucketExistsAsync();
            
            var cacheItem = await GetCacheItemAsync(key, token);
            
            if (cacheItem == null || IsExpired(cacheItem))
            {
                if (cacheItem != null)
                {
                    // Remove expired item
                    await RemoveInternalAsync(key, token);
                }
                return null;
            }

            return cacheItem.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MinIO cache get failed for key {Key}, using fallback. Error: {ErrorMessage}", 
                key, ex.Message);
            return GetFromFallback(key);
        }
    }

    private async Task SetInternalAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token)
    {
        try
        {
            await EnsureBucketExistsAsync();
            
            var cacheItem = new CacheItem
            {
                Value = value,
                CreatedAt = DateTimeOffset.UtcNow,
                AbsoluteExpiration = options.AbsoluteExpiration,
                SlidingExpiration = options.SlidingExpiration
            };

            var json = JsonSerializer.Serialize(cacheItem, _jsonOptions);
            var data = Encoding.UTF8.GetBytes(json);

            using var stream = new MemoryStream(data);
            
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(GetObjectName(key))
                .WithStreamData(stream)
                .WithObjectSize(data.Length)
                .WithContentType("application/json");

            await _minioClient.PutObjectAsync(putObjectArgs, token);
            
            // Also set in fallback if available
            SetInFallback(key, value, options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MinIO cache set failed for key {Key}, using fallback only. Error: {ErrorMessage}", 
                key, ex.Message);
            SetInFallback(key, value, options);
        }
    }

    private async Task RefreshInternalAsync(string key, CancellationToken token)
    {
        try
        {
            await EnsureBucketExistsAsync();
            
            var cacheItem = await GetCacheItemAsync(key, token);
            
            if (cacheItem != null && !IsExpired(cacheItem))
            {
                // Update timestamp for sliding expiration
                cacheItem.CreatedAt = DateTimeOffset.UtcNow;
                
                var json = JsonSerializer.Serialize(cacheItem, _jsonOptions);
                var data = Encoding.UTF8.GetBytes(json);

                using var stream = new MemoryStream(data);
                
                var putObjectArgs = new PutObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(GetObjectName(key))
                    .WithStreamData(stream)
                    .WithObjectSize(data.Length)
                    .WithContentType("application/json");

                await _minioClient.PutObjectAsync(putObjectArgs, token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MinIO cache refresh failed for key {Key}. Error: {ErrorMessage}", key, ex.Message);
        }
    }

    private async Task RemoveInternalAsync(string key, CancellationToken token)
    {
        try
        {
            await EnsureBucketExistsAsync();
            
            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(GetObjectName(key));

            await _minioClient.RemoveObjectAsync(removeObjectArgs, token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("MinIO cache remove failed for key {Key} (may not exist). Error: {ErrorMessage}", 
                key, ex.Message);
        }
        
        RemoveFromFallback(key);
    }

    private async Task<CacheItem?> GetCacheItemAsync(string key, CancellationToken token = default)
    {
        try
        {
            using var stream = new MemoryStream();
            
            var getObjectArgs = new GetObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(GetObjectName(key))
                .WithCallbackStream(s => s.CopyTo(stream));

            await _minioClient.GetObjectAsync(getObjectArgs, token);
            
            var json = Encoding.UTF8.GetString(stream.ToArray());
            return JsonSerializer.Deserialize<CacheItem>(json, _jsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsExpired(CacheItem cacheItem)
    {
        var now = DateTimeOffset.UtcNow;

        // Check absolute expiration
        if (cacheItem.AbsoluteExpiration.HasValue && now >= cacheItem.AbsoluteExpiration.Value)
        {
            return true;
        }

        // Check sliding expiration
        if (cacheItem.SlidingExpiration.HasValue)
        {
            var expiry = cacheItem.CreatedAt.Add(cacheItem.SlidingExpiration.Value);
            if (now >= expiry)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetObjectName(string key)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .Replace('+', '-')
            .Replace('/', '_')
            .Replace('=', '.');

    private byte[]? GetFromFallback(string key)
        => _fallbackCache?.Get<byte[]>(key);

    private void SetInFallback(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (_fallbackCache == null) return;

        var memoryOptions = new MemoryCacheEntryOptions();
        
        if (options.AbsoluteExpiration.HasValue)
            memoryOptions.SetAbsoluteExpiration(options.AbsoluteExpiration.Value);
        
        if (options.SlidingExpiration.HasValue)
            memoryOptions.SetSlidingExpiration(options.SlidingExpiration.Value);
            
        _fallbackCache.Set(key, value, memoryOptions);
    }

    private void RemoveFromFallback(string key)
        => _fallbackCache?.Remove(key);

    #endregion
}

internal class CacheItem
{
    public byte[] Value { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AbsoluteExpiration { get; set; }
    public TimeSpan? SlidingExpiration { get; set; }
}

// Extension methods for DI container registration
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMinioDistributedCache(
        this IServiceCollection services, 
        Action<MinioCacheOptions> setupAction)
    {
        services.Configure(setupAction);
        
        // Ensure there is an IMemoryCache available for fallback
        services.AddMemoryCache();
        
        services.AddSingleton<IDistributedCache, MinioDistributedCache>();
        return services;
    }
}