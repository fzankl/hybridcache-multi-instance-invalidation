using CacheInvalidation.Api;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Set by the AppHost: which cache sits behind the HybridCache API, and which instance this is.
var variant = builder.Configuration["Cache:Variant"] ?? "hybridcache";
var instance = builder.Configuration["Cache:Instance"] ?? "a";

if (instance == "replica")
{
    // Replicas share one configuration. Name them by what tells them apart at runtime.
    instance = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") ?? $"pid-{Environment.ProcessId}";
}

var expiration = builder.Configuration.GetValue("Cache:Expiration", TimeSpan.FromSeconds(60));
var localCacheExpiration = builder.Configuration.GetValue("Cache:LocalCacheExpiration", TimeSpan.FromSeconds(10));

// Only the fusioncache-tags variant: how long an instance keeps a tag marker in its L1
// before it reads the marker from L2 again.
TimeSpan? tagsLocalCacheExpiration = variant == "fusioncache-tags"
    ? builder.Configuration.GetValue("Cache:TagsLocalCacheExpiration", TimeSpan.FromSeconds(5))
    : null;

// Entra ID against Azure Managed Redis. Against the local container the call changes nothing:
// it only applies to Azure hostnames.
builder.AddRedisClientBuilder("redis")
    .WithAzureAuthentication();

// L2 for all variants. The prefix keeps the variants apart in the shared Redis.
// Wrapped in a tracer, so every read can report whether it touched L2.
builder.Services.AddOptions<RedisCacheOptions>()
    .Configure<IConnectionMultiplexer>((options, redis) =>
    {
        options.InstanceName = $"{variant}:";
        options.ConnectionMultiplexerFactory = () => Task.FromResult(redis);
    });

builder.Services.AddSingleton<IDistributedCache>(services =>
    new L2Trace.Cache(new RedisCache(services.GetRequiredService<IOptions<RedisCacheOptions>>())));

switch (variant)
{
    case "hybridcache":
        builder.Services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = expiration,
                LocalCacheExpiration = localCacheExpiration
            };
        });
        break;

    case "fusioncache":
    case "fusioncache-tags":
    case "fusioncache-backplane":
        var fusion = builder.Services.AddFusionCache()
            .WithDefaultEntryOptions(new FusionCacheEntryOptions
            {
                Duration = expiration,
                MemoryCacheDuration = localCacheExpiration
            })
            .WithSerializer(new FusionCacheSystemTextJsonSerializer())
            .WithRegisteredDistributedCache();

        if (variant == "fusioncache-backplane")
        {
            fusion.WithBackplane(services => new RedisBackplane(new RedisBackplaneOptions
            {
                ConnectionMultiplexerFactory = () =>
                    Task.FromResult(services.GetRequiredService<IConnectionMultiplexer>())
            }));
        }

        if (tagsLocalCacheExpiration is { } tagsDuration)
        {
            // No backplane: the only way a tag remove reaches another instance is its next read
            // of the marker from L2.
            fusion.WithOptions(options => options.TagsDefaultEntryOptions.MemoryCacheDuration = tagsDuration);
        }

        // Same HybridCache API as the hybridcache variant: only the implementation changes.
        fusion.AsHybridCache();

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddFusionCacheInstrumentation())
            .WithMetrics(metrics => metrics.AddFusionCacheInstrumentation());
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown Cache:Variant '{variant}'. Use hybridcache, fusioncache, fusioncache-tags, or fusioncache-backplane.");
}

var app = builder.Build();

static string SourceKey(string id) => $"source:{id}";
static string EntryKey(string id) => $"product:{id}";
static string TagFor(string id) => $"product-tag:{id}";

app.MapGet("/", () => Results.Ok(new { variant, instance, expiration, localCacheExpiration, tagsLocalCacheExpiration }));

// Stands in for the database: a plain Redis key that both instances read, outside the cache.
app.MapPut("/source/{id}/{value}", async (string id, string value, IConnectionMultiplexer redis) =>
{
    await redis.GetDatabase().StringSetAsync($"{variant}:{SourceKey(id)}", value);
    return Results.NoContent();
});

// options=default          the defaults registered above
// options=expiration-only  per-call options with Expiration only: that value becomes the L1 lifetime too
// options=complete         per-call options with Expiration and LocalCacheExpiration
app.MapGet("/products/{id}", async (string id, string? options, HybridCache cache, IConnectionMultiplexer redis) =>
{
    HybridCacheEntryOptions? entryOptions = options switch
    {
        null or "default" => null,
        "expiration-only" => new HybridCacheEntryOptions { Expiration = expiration },
        "complete" => new HybridCacheEntryOptions
        {
            Expiration = expiration,
            LocalCacheExpiration = localCacheExpiration
        },
        _ => throw new BadHttpRequestException("options must be default, expiration-only, or complete.")
    };

    var loadedFromSource = false;
    var l2Calls = new List<L2Call>();
    
    L2Trace.Current.Value = l2Calls;

    var value = await cache.GetOrCreateAsync(
        EntryKey(id),
        async cancellationToken =>
        {
            loadedFromSource = true;
            var stored = await redis.GetDatabase().StringGetAsync($"{variant}:{SourceKey(id)}");
            return stored.HasValue ? stored.ToString() : "missing";
        },
        entryOptions,
        [TagFor(id)]);

    L2Trace.Current.Value = null;

    // source: the factory ran. l2: a get on L2 returned a value. l1: nothing left the process.
    var origin = loadedFromSource ? "source"
        : l2Calls.Any(call => call.Operation == "get" && call.Hit) ? "l2"
        : "l1";

    return Results.Ok(new { instance, value, origin, l2Calls });
});

// What the shared Redis holds for this id: cache entries, tag markers, and the source key.
app.MapGet("/redis/{id}", async (string id, IConnectionMultiplexer redis) =>
{
    var database = redis.GetDatabase();
    var keys = new List<object>();

    foreach (var server in redis.GetServers())
    {
        await foreach (var key in server.KeysAsync(pattern: $"*{id}*"))
        {
            var ttl = await database.KeyTimeToLiveAsync(key);
            var type = await database.KeyTypeAsync(key);
            keys.Add(new
            {
                key = key.ToString(),
                type = type.ToString(),
                ttlSeconds = ttl is { } t ? Math.Round(t.TotalSeconds) : (double?)null
            });
        }
    }

    return Results.Ok(keys.OrderBy(k => ((dynamic)k).key).ToList());
});

app.MapDelete("/products/{id}", async (string id, HybridCache cache) =>
{
    await cache.RemoveAsync(EntryKey(id));
    return Results.Ok(new { instance });
});

app.MapDelete("/products/{id}/tag", async (string id, HybridCache cache) =>
{
    await cache.RemoveByTagAsync(TagFor(id));
    return Results.Ok(new { instance });
});

app.Run();
