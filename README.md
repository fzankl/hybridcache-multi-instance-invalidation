# HybridCache Multi-Instance Invalidation: what a remove actually reaches

You call `RemoveAsync` on one instance, and the other instance keeps serving the old value. This repository shows how long it keeps serving it, for Microsoft's `HybridCache` and for FusionCache with and without a backplane, all behind the same `HybridCache` API.

In short: after a remove by key, the other instance is stale for the L1 lifetime (10s here). After a remove by tag, it can be stale for the L2 lifetime (60s). With FusionCache's backplane, the remove reaches the other instance, which returns the new value on its first or second read. Per-call options that set only `Expiration` apply it to L1 as well, and then a remove by key is stale for 60s too. Without a backplane, FusionCache can shorten the window after a remove by tag: with tag markers kept in L1 for 5s, the other instance was stale for 5s.

Everything runs locally: one Redis container and several instances of the same API, orchestrated by Aspire. No Azure subscription needed, though the same AppHost also [deploys to Azure](#run-it-in-azure).

## What you need

- .NET SDK 10.0
- A container runtime (Docker Desktop or Podman)

## Run it

Start the AppHost:

```bash
dotnet run --project aspire/CacheInvalidation.AppHost
```

It starts Redis and two instances per variant, each with its own L1:

| Variant                 | What sits behind `HybridCache`                     | Instance A | Instance B |
|-------------------------|----------------------------------------------------|------------|------------|
| `hybridcache`           | `Microsoft.Extensions.Caching.Hybrid`              | 5101       | 5102       |
| `fusioncache`           | FusionCache via `AsHybridCache()`, no backplane    | 5201       | 5202       |
| `fusioncache-backplane` | FusionCache via `AsHybridCache()`, Redis backplane | 5301       | 5302       |
| `fusioncache-tags`      | as `fusioncache`, tag markers in L1 for 5 s        | 5401       | 5402       |

All eight share one Redis as L2. Each variant uses its own key prefix, so they don't read each other's entries. The Microsoft variant is called `hybridcache` because Azure rejects resource names that contain `microsoft` (`ReservedResourceName`).

A ninth resource, `hybridcache-replicas` on port 5111, runs two replicas of the `hybridcache` variant behind one endpoint. That is the setup for the [second scenario](#the-symptom-two-replicas-behind-one-endpoint).

In a second terminal, run a scenario:

```bash
dotnet run scenario/run.cs -- hybridcache key
```

The last line of the output is the result:

```text
B returned stale data for 10.0 s (92 stale reads).
```

A removed the entry, and B kept serving v1 from its own L1 until `LocalCacheExpiration` ran out. The [results](#results) compare that across all variants and cases.

<details>
<summary>Full output of this run</summary>

```text
hybridcache, invalidate by key, options=default
A reads after the invalidation: no
A http://localhost:5101/
B http://localhost:5102/
L1 (LocalCacheExpiration) 00:00:10, L2 (Expiration) 00:01:00
Round trip: A 1 ms, B 0 ms (median of 5)

         A -> v1 (source)
            L2 get product:5812b572 miss
            L2 get __MSFT_HCT__product-tag:5812b572 miss
         B -> v1 (l2)
            L2 get __MSFT_HCT__product-tag:5812b572 miss
            L2 get product:5812b572 hit
         Redis after warm-up:
            hybridcache:product:5812b572 (Hash, ttl 60 s)
            hybridcache:source:5812b572 (String, no ttl)
-- source changed to v2, A removed the entry by key --
         Redis after invalidation:
            hybridcache:source:5812b572 (String, no ttl)
  0.0 s  B -> v1 (l1)
  5.0 s  B -> v1 (l1)
 10.0 s  B -> v2 (source)
            L2 get product:5812b572 miss
         Redis at the end:
            hybridcache:source:5812b572 (String, no ttl)

B returned stale data for 10.0 s (92 stale reads).
```

</details>

The scenario does three things:

1. The source holds v1. A and B read the product, so both have it in their L1.
2. The source changes to v2, and A removes the entry, by key or by tag.
3. B is read every 100 ms until it returns v2.

Every read says where its value came from. `l1`: nothing left the process. `l2`: a get on Redis returned the entry. `source`: the factory ran. The `L2` lines below a read list the Redis calls it made, recorded by a wrapper around `IDistributedCache`. The lines below `Redis` list the keys Redis holds for the product, so you can see what a remove deleted and what it wrote.

Arguments: `<variant> [key|tag] [default|expiration-only|complete] [read-a]`.

With `read-a`, A reads once right after the remove. Without it, A stays quiet. A's read writes v2 to L2, and B finds it there once its own L1 expires, whether or not the remove ever reached B.

## Results

Measured with `Microsoft.Extensions.Caching.Hybrid` 10.10.0 and `ZiggyCreatures.FusionCache` 2.9.0, `LocalCacheExpiration` 10 s, `Expiration` 60 s, in two environments:

- **Local:**  
Windows 11, the `redis:8.6` container Aspire 13.4.6 starts, everything on one machine. Two runs per case. The second run gave the same values within 0.1 s.
- **Azure:**  
Azure Container Apps in West US, one replica per instance, against Azure Managed Redis (`Balanced_B0`, Redis 7.4, clustering policy `OSSCluster`, Entra ID only). The scenario ran on a machine in Germany, with a round trip of 155 to 199 ms to each instance. One run per case and deployment. `hybridcache`, `fusioncache` and `fusioncache-backplane` ran against two deployments, on 29 September and 2 October 2026, with values within 0.3 s of each other; the table shows the first. `fusioncache-tags` ran against the second.

The table shows how long B kept returning v1 after A's remove, local / Azure.

| Case                                                   | `hybridcache`   | `fusioncache`   | `fusioncache-backplane` | `fusioncache-tags`  |
|--------------------------------------------------------|-----------------|-----------------|-------------------------|---------------------|
| Remove by key                                          | 10.0 s / 9.0 s  | 10.0 s / 9.2 s  | 0.1 s / first read      | 10.0 s / 8.4 s      |
| Remove by key, A reads afterwards                      | 10.0 s / 9.1 s  | 10.0 s / 9.1 s  | first read / first read | 10.0 s / 8.9 s      |
| Remove by tag                                          | 60.0 s / 59.2 s | 60.0 s / 59.0 s | first read / first read | 5.0 s / 4.4 s       |
| Remove by tag, A reads afterwards                      | 10.0 s / 9.0 s  | 10.0 s / 9.0 s  | first read / first read | 5.0 s / 4.1 s       |
| Remove by key, per-call options with `Expiration` only | 60.0 s / 59.2 s | 60.0 s / 59.0 s | first read / first read | 60.0 s / 59.0 s     |
| Remove by tag, per-call options with `Expiration` only | 60.0 s / 59.2 s | 60.0 s / 59.2 s | first read / first read | 5.0 s / 4.2 s       |
| Remove by key, per-call options with both values       | 10.0 s / 9.3 s  | 10.0 s / 9.3 s  | first read / first read | 10.0 s / 9.4 s      |
| Remove by tag, per-call options with both values       | 60.0 s / 59.1 s | 60.0 s / 59.0 s | first read / first read | 5.0 s / 4.2 s       |

The raw output of the Azure runs is in [measurements/azure-2026-09-29](measurements/azure-2026-09-29/README.md) and [measurements/azure-2026-10-02](measurements/azure-2026-10-02/README.md), because you cannot reproduce that column without a deployment.

"First read" means B returned v2 on the first read after the remove. The Azure values are below 10 s and 60 s because the clock starts at the remove, while B's L1 runs from B's last load. In Azure, four calls of 160 ms each lie in between.

Every value is an upper bound. The scenario removes the entry right after B loaded it, when B's L1 copy and its copy of the tag marker have their whole lifetime ahead of them. A remove at a random point in time meets a copy that is already partly through its lifetime, so the window is shorter.

A itself returned v2 on its first read after the remove, in every variant, by key and by tag, in both environments.

What the traces show:

**Without a backplane, a remove by key deletes the L2 entry and clears only A's L1**  
After the remove, Redis holds only the source key. A's own L1 is cleared before `RemoveAsync` returns. B keeps serving its L1 copy until `LocalCacheExpiration` runs out, then misses on L2 and reloads. That holds for FusionCache without a backplane just as much as for Microsoft's implementation.

**A remove by tag leaves the L2 entry in place**  
It writes a marker instead: `__MSFT_HCT__<tag>` for Microsoft, `__fc:t:<tag>` for FusionCache, both with a TTL of days. The entry keeps its 60 s. B reads the marker once, on its very first read, when it does not exist yet, and not again within the 60 s. So when B's L1 expires after 10 s, B gets the old entry from L2, serves it, and keeps it in L1 for another 10 s. That repeats until the L2 entry expires. A, which knows about the tag, gets the same L2 hit and runs the factory anyway.

The trace of that case, `hybridcache tag`, local run:

```text
         A -> v1 (source)
            L2 get __MSFT_HCT__product-tag:314c609d miss
            L2 get product:314c609d miss
         B -> v1 (l2)
            L2 get __MSFT_HCT__product-tag:314c609d miss
            L2 get product:314c609d hit
-- source changed to v2, A removed the entry by tag --
         Redis after invalidation:
            hybridcache:__MSFT_HCT__product-tag:314c609d (Hash, ttl 86400000 s)
            hybridcache:product:314c609d (Hash, ttl 60 s)
            hybridcache:source:314c609d (String, no ttl)
  0.0 s  B -> v1 (l1)
  5.0 s  B -> v1 (l1)
 10.0 s  B -> v1 (l2)
            L2 get product:314c609d hit
 10.1 s  B -> v1 (l1)
 15.2 s  B -> v1 (l1)
 20.1 s  B -> v1 (l2)
            L2 get product:314c609d hit
  ...
 50.2 s  B -> v1 (l2)
            L2 get product:314c609d hit
 55.3 s  B -> v1 (l1)
 60.1 s  B -> v2 (source)
            L2 get product:314c609d miss

B returned stale data for 60.1 s (551 stale reads).
```

After the remove, the marker is in Redis, next to the entry it was meant to invalidate, and B never asks for it. The `fusioncache` trace has the same shape, with its own marker.

For Microsoft's implementation, the source shows why. Each instance keeps the invalidation time of a tag in a dictionary, filled from L2 the first time it meets the tag. After that, only a `RemoveByTagAsync` on the same instance updates the entry ([DefaultHybridCache.TagInvalidation.cs](https://github.com/dotnet/extensions/blob/v10.10.0/src/Libraries/Microsoft.Extensions.Caching.Hybrid/Internal/DefaultHybridCache.TagInvalidation.cs), tag `v10.10.0`). In 10.10.0, an instance that knew the tag before the remove does not learn about it until it restarts. [dotnet/extensions#7411](https://github.com/dotnet/extensions/issues/7411) is open and proposes an expiry for these dictionary entries. It describes the stale data as indefinite. In these runs, it ended when the L2 entry expired.

For FusionCache, the behavior is documented. Without a backplane, the cache "can become incoherent after a change (and that includes a `RemoveByTag(tag)` call) until the data in the cache expires" ([Tagging.md](https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/Tagging.md)). The documentation recommends a low `MemoryCacheDuration` in `TagsDefaultEntryOptions`, so that each instance reads the marker from L2 more often. The `fusioncache` variant keeps the defaults for the tag options, and with them B did not read the marker again within the 60 s.

**Without a backplane, a short L1 lifetime for tag markers cuts the window after a tag remove to that lifetime**  
The `fusioncache-tags` variant sets `TagsDefaultEntryOptions.MemoryCacheDuration` to 5 s and changes nothing else. After a remove by tag, B was stale for 5.0 s in all four tag cases locally, and for 4.1 to 4.4 s in Azure. The local run:

```text
-- source changed to v2, A removed the entry by tag --
         Redis after invalidation:
            fusioncache-tags:source:50aaff2a (String, no ttl)
            fusioncache-tags:v2:__fc:t:product-tag:50aaff2a (Hash, ttl 863999 s)
            fusioncache-tags:v2:product:50aaff2a (Hash, ttl 59 s)
  0.0 s  B -> v1 (l1)
  4.9 s  B -> v2 (source)
            L2 get v2:__fc:t:! miss
            L2 get v2:__fc:t:product-tag:50aaff2a hit
            L2 get v2:product:50aaff2a hit
            L2 set v2:product:50aaff2a
```

B's copy of the marker expired after 5 s. The next read fetched the marker from L2, found the entry older than the marker, and ran the factory. B did not wait for its own L1 entry to expire: the window is the marker's 5 s, not the entry's 10 s. With per-call options that set only `Expiration`, the entry stays in B's L1 for 60 s, and the tag remove still took effect after 5 s. A remove by key gains nothing from the setting: 10 s, and 60 s with `Expiration` only, as in the `fusioncache` variant.

The price is more reads on L2 while nothing changes. `scenario/reads.cs` reads B every 100 ms for 55 s without a remove and counts the L2 calls:

```bash
dotnet run scenario/reads.cs -- fusioncache-tags
```

| Variant                 | L2 calls in 55 s | Entry | Tag markers                                |
|-------------------------|------------------|-------|--------------------------------------------|
| `hybridcache`           | 7                | 6     | 1                                          |
| `fusioncache`           | 7 to 9           | 6     | 1 to 3, all on the first read              |
| `fusioncache-backplane` | 7 to 9           | 6     | 1 to 3, all on the first read              |
| `fusioncache-tags`      | 39               | 6     | 33: 3 markers, each read 11 times          |

The counts are from two local runs and one run in Azure. Every variant reads the entry from L2 each time its L1 copy expires, six times in 55 s. FusionCache keeps two more markers for the cache as a whole, `__fc:t:!` and `__fc:t:*`. Without the short lifetime, B read them in some runs on its first read and in no run afterwards. `fusioncache-tags` reads all three markers every 5 s. The scenario uses one tag. How the count grows with more tags was not measured.

**If A reads after the tag remove, the stale window drops from 60 s to 10 s**  
A writes v2 to L2, and B picks it up after 10 s. In production, that read happens on whichever instance handled the write. The instances that did not are the ones this scenario measures.

**Per-call options that set only `Expiration` apply it to L1 as well**  
In those runs, B did not touch L2 at all for 60 s. The globally configured `LocalCacheExpiration` did not apply to the entry. Set both values on every call (`options=complete`), or none.

**With the backplane, B's first read after a tag remove hit L2 and ran the factory anyway**  
The invalidation had reached B before the read. That holds on Azure Managed Redis with `OSSCluster` too. Locally, after a remove by key, one run had a single stale read from B's L1, 100 ms before the next read missed on L2 and reloaded. The Azure runs cannot resolve that: B's first read arrives about 0.2 s after the remove at the earliest.

**Microsoft does not wait for the L2 write**  
In the FusionCache variants, every read that ran the factory also lists its `set` on L2, 96 of 96 in the runs of 29 September and 48 of 48 in the Azure run of 2 October. In the Microsoft variant, 1 of 64 does, the very first request of a process, although the entry is in Redis afterwards every time. The wrapper records a call when it completes, so in the other 63 the write had not completed when the response was built. In the sample output above, Redis does not hold the entry yet when the scenario lists the keys right after B's reload. The source says so as well: the value goes to the callers first, and "everything from this point onwards happens in the background", including the write to L2 ([DefaultHybridCache.StampedeStateT.cs](https://github.com/dotnet/extensions/blob/v10.10.0/src/Libraries/Microsoft.Extensions.Caching.Hybrid/Internal/DefaultHybridCache.StampedeStateT.cs), tag `v10.10.0`).

## Run the whole table

```bash
dotnet run scenario/matrix.cs -- --name first-run --description "my machine, Redis container"
```

That runs all 32 cases and the replica scenario, the four variants side by side, in about six minutes. Each line on the console is the result of one case. The raw output goes to `results/<date>-<name>-<variant>.txt`, one file per variant. Existing files are never overwritten, so every run needs its own `--name`. `--out` writes to another directory, and `--domain <environment>.<region>.azurecontainerapps.io` runs against a deployment in Azure.

## Traces in the dashboard

The Aspire dashboard shows what each instance did. The FusionCache variants emit their own spans: `get or set from cache`, `get from cache level`, `execute factory`, `set to cache level`, `remove from cache by tag`. Around them you see the Redis commands, including the `PUBLISH` and `SUBSCRIBE` of the backplane. The `hybridcache` variant emits no cache spans, only the HTTP request and the Redis commands.

```bash
aspire otel spans fusioncache-backplane-b --apphost aspire/CacheInvalidation.AppHost/CacheInvalidation.AppHost.csproj
```

## The symptom: two replicas behind one endpoint

The first scenario addresses A and B separately to show the mechanism. In production, you see something else: one URL, and a value that changes back and forth.

```bash
dotnet run scenario/replicas.cs -- key
```

The scenario warms both replicas, changes the source, removes the entry through the endpoint, and reads every 250 ms. Each line names the replica that answered. Measured on 29 September 2026 in Azure, one Container App with two replicas behind its ingress, same setup as above:

```text
hybridcache, two replicas behind one endpoint, invalidate by key
Endpoint https://hybridcache-replicas.<environment>.westus.azurecontainerapps.io/
L1 (LocalCacheExpiration) 00:00:10, L2 (Expiration) 00:01:00

         hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v1 (source)
         hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v1 (l2)
-- source changed to v2 --
-- hybridcache-replicas--a2mxtne-5759b844c4-lph49 removed the entry by key --
  0.0 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v1 (l1)
  0.8 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v1 (l1)
  1.5 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (source)
  2.3 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l1)
  3.0 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l1)
  3.7 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v1 (l1)
  4.5 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l1)
  5.2 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l1)
  5.9 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l1)
  6.7 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v1 (l1)
  7.4 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v1 (l1)
  8.1 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v1 (l1)
  8.9 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v2 (l2)
  9.7 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v2 (l1)
 10.4 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l1)
 11.1 s  hybridcache-replicas--a2mxtne-5759b844c4-82mnz -> v2 (l1)
 11.9 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l2)
 12.6 s  hybridcache-replicas--a2mxtne-5759b844c4-lph49 -> v2 (l1)

hybridcache-replicas--a2mxtne-5759b844c4-82mnz: 6 x v1, 3 x v2
hybridcache-replicas--a2mxtne-5759b844c4-lph49: 0 x v1, 9 x v2
The value changed 5 times between consecutive reads.
```

The replica that handled the remove never returned v1 again. The other one returned v1 until its L1 expired. A user who reloads the page sees the old value, then the new one, then the old one again.

Locally, the endpoint is the Aspire proxy in front of two processes, and the replicas are named by process id. The same run there, with reads 250 ms apart and no network in between: 20 stale reads from one replica, none from the other, 19 changes of the value in 10 s.

After a remove by tag, the second replica was stale for about 10 s as well, not 60 s. The replica that removed the entry answered some of the reads, reloaded, and wrote v2 to L2. That is the `read-a` case from the table.

## Run it in Azure

Optional, and it costs money while it runs. The AppHost deploys to Azure Container Apps and Azure Managed Redis:

- `AddAzureManagedRedis("redis").RunAsContainer()` is the Redis container locally and an Azure Managed Redis instance in Azure, configured as listed under [Results](#results).
- Every instance becomes its own Container App with its own hostname and a managed identity that may access the cache.
- A and B are pinned to exactly one replica each. Scale-to-zero would drop the L1 and make an instance look consistent. Scale-out would add a second L1 behind the same hostname.

```bash
az login
aspire deploy --apphost aspire/CacheInvalidation.AppHost/CacheInvalidation.AppHost.csproj
```

The CLI asks for subscription, region and resource group. Azure Managed Redis is not available in every region at every size. If the deployment fails with `InsufficientCapacity`, pick another region.

Then pass the domain to the matrix, or the hostnames to a single scenario:

```bash
dotnet run scenario/matrix.cs -- --name azure --domain <environment>.<region>.azurecontainerapps.io

dotnet run scenario/run.cs -- fusioncache-backplane key \
  --a https://fusioncache-backplane-a.<environment>.<region>.azurecontainerapps.io \
  --b https://fusioncache-backplane-b.<environment>.<region>.azurecontainerapps.io

dotnet run scenario/replicas.cs -- key \
  --url https://hybridcache-replicas.<environment>.<region>.azurecontainerapps.io
```

The endpoints are public and have no authentication. They hold test data only, but anyone who knows a hostname can call them. Delete the resource group when you are done:

```bash
az group delete --name <resource group>
```

## The per-call options

The three `options` values of `GET /products/{id}` map to:

```csharp
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
```

## How the FusionCache variants are configured

FusionCache's `Duration` and `MemoryCacheDuration` get the same values as `Expiration` and `LocalCacheExpiration` in the `hybridcache` variant. The backplane uses the connection Aspire registers:

```csharp
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

fusion.AsHybridCache();
```

The `fusioncache-tags` variant adds one line, with `Cache:TagsLocalCacheExpiration` (5 s) as the value:

```csharp
fusion.WithOptions(options => options.TagsDefaultEntryOptions.MemoryCacheDuration = tagsDuration);
```

The full setup is in [src/CacheInvalidation.Api/Program.cs](src/CacheInvalidation.Api/Program.cs).

## Changing the timings

`Cache:Expiration` and `Cache:LocalCacheExpiration` in [src/CacheInvalidation.Api/appsettings.json](src/CacheInvalidation.Api/appsettings.json), and `Cache:TagsLocalCacheExpiration` for the `fusioncache-tags` variant, which defaults to 5 s in [Program.cs](src/CacheInvalidation.Api/Program.cs). The scenario reads the values from instance A and waits for up to `Expiration` plus 15 s.

## License

Code under the [MIT License](LICENSE), text under [CC BY 4.0](LICENSE-docs).
