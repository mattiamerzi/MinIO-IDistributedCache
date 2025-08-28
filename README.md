# MinIO Distributed Cache for ASP.NET Core

A high-performance distributed cache implementation for ASP.NET Core using MinIO object storage.

## Overview

This library provides an `IDistributedCache` implementation that uses MinIO, an S3-compatible object storage, as its backend. It's designed to be a scalable and reliable caching solution for distributed applications.

## Features

- **S3-Compatible Storage**: Works with MinIO and any S3-compatible storage services
- **Fallback Cache**: Optional in-memory fallback cache for improved resilience
- **Expiration Support**: Handles both absolute and sliding expiration policies
- **Thread-Safe**: Safe for concurrent access from multiple threads or processes
- **Simple Configuration**: Easy to set up with dependency injection

## Installation

```bash
dotnet add package MinioDistributedCache
```

## Quick Start

### 1. Register the service in your `Program.cs` or `Startup.cs`:

```csharp
builder.Services.AddMinioDistributedCache(options =>
{
    options.Endpoint = "minio:9000";
    options.AccessKey = "minioadmin";
    options.SecretKey = "minioadmin";
    options.BucketName = "aspnetcore-cache";
    options.UseSSL = false;
    options.UseFallbackCache = true; // Use in-memory fallback if MinIO is unavailable
});
```

### 2. Use the distributed cache in your application:

```csharp
public class WeatherForecastController : ControllerBase
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<WeatherForecastController> _logger;

    public WeatherForecastController(
        IDistributedCache cache,
        ILogger<WeatherForecastController> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // Try to get from cache
        var cachedData = await _cache.GetStringAsync("WeatherForecast");
        
        if (cachedData != null)
        {
            _logger.LogInformation("Returning cached weather data");
            return Ok(JsonSerializer.Deserialize<WeatherForecast[]>(cachedData));
        }
        
        // Cache miss - generate new data
        var forecast = GenerateForecasts();
        
        // Cache the result
        await _cache.SetStringAsync(
            "WeatherForecast", 
            JsonSerializer.Serialize(forecast),
            new DistributedCacheEntryOptions 
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            });
        
        return Ok(forecast);
    }
}
```

## Configuration Options

| Option | Description | Default |
|--------|-------------|---------|
| `Endpoint` | MinIO server endpoint | `""` |
| `AccessKey` | MinIO access key | `""` |
| `SecretKey` | MinIO secret key | `""` |
| `BucketName` | Bucket name for cache storage | `"aspnetcore-distributed-cache"` |
| `UseSSL` | Whether to use SSL for MinIO connection | `false` |
| `Region` | MinIO region (optional) | `null` |
| `UseFallbackCache` | Whether to use in-memory fallback cache | `true` |

## Performance Considerations

- The MinIO distributed cache is optimized for medium to large cache values
- The fallback cache is used only when MinIO operations fail, providing resilience to temporary storage issues
- Consider using a CDN for public assets instead of the distributed cache

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the LICENSE file for details.