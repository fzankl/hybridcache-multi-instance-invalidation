#:property PublishAot=false

// Runs one invalidation scenario against the two instances of a variant.
//
//   dotnet run scenario/run.cs -- <variant> [key|tag] [default|expiration-only|complete] [read-a]
//                                 [--a <url of instance A> --b <url of instance B>]
//
// variant: hybridcache | fusioncache | fusioncache-backplane | fusioncache-tags
// --a and --b: only needed when the instances do not run on the local ports, e.g. in Azure.
//
// 1. The source holds v1. A and B read the product, so both have it in their L1.
// 2. The source changes to v2, and A invalidates the entry (by key or by tag).
// 3. B is read every 100 ms until it returns v2.
//
// Every read reports where its value came from: l1 (nothing left the process),
// l2 (a get on Redis returned the entry), or source (the factory ran).
// The keys Redis holds for the product are listed before and after the invalidation.
//
// With read-a, A reads once right after the invalidation. That shows whether A itself is
// consistent, but it also writes v2 to L2, which B then finds once its L1 expires. Without
// read-a, B can only become consistent through the invalidation itself.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;

// The output looks the same on every machine, whatever its regional settings.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var ports = new Dictionary<string, int>
{
    ["hybridcache"] = 5101,
    ["fusioncache"] = 5201,
    ["fusioncache-backplane"] = 5301,
    ["fusioncache-tags"] = 5401
};

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var urlA = Option("--a");
var urlB = Option("--b");
var positional = args.Where((arg, i) => !arg.StartsWith("--") && (i == 0 || !args[i - 1].StartsWith("--"))).ToArray();

if (positional.Length == 0 || !ports.TryGetValue(positional[0], out var portA) || (urlA is null) != (urlB is null))
{
    Console.WriteLine("Usage: dotnet run scenario/run.cs -- <hybridcache|fusioncache|fusioncache-backplane|fusioncache-tags> [key|tag] [default|expiration-only|complete] [read-a] [--a <url> --b <url>]");
    return 1;
}

var variant = positional[0];
var invalidation = positional.Contains("tag") ? "tag" : "key";
var options = positional.FirstOrDefault(arg => arg is "default" or "expiration-only" or "complete") ?? "default";
var readA = positional.Contains("read-a");

using var a = new HttpClient { BaseAddress = new Uri(urlA ?? $"http://localhost:{portA}") };
using var b = new HttpClient { BaseAddress = new Uri(urlB ?? $"http://localhost:{portA + 1}") };

var settings = await a.GetFromJsonAsync<Settings>("/");
var id = Guid.NewGuid().ToString("n")[..8];
var product = $"/products/{id}?options={options}";

Console.WriteLine($"{variant}, invalidate by {invalidation}, options={options}");
Console.WriteLine($"A reads after the invalidation: {(readA ? "yes" : "no")}");
Console.WriteLine($"A {a.BaseAddress}");
Console.WriteLine($"B {b.BaseAddress}");
Console.WriteLine($"L1 (LocalCacheExpiration) {settings!.LocalCacheExpiration}, L2 (Expiration) {settings.Expiration}");

if (settings.TagsLocalCacheExpiration is { } tagsLocalCacheExpiration)
{
    Console.WriteLine($"Tag markers in L1 (TagsDefaultEntryOptions.MemoryCacheDuration) {tagsLocalCacheExpiration}");
}

// Round trip from here to each instance. Reads cannot resolve anything faster than this.
Console.WriteLine($"Round trip: A {await RoundTrip(a):0} ms, B {await RoundTrip(b):0} ms (median of 5)");

var settingsB = await b.GetFromJsonAsync<Settings>("/");

if (settings.Variant != variant || settingsB!.Variant != variant)
{
    Console.WriteLine($"A runs variant '{settings.Variant}', B runs '{settingsB!.Variant}', expected '{variant}'.");
    return 1;
}

Console.WriteLine();

await a.PutAsync($"/source/{id}/v1", null);
Print("A", await a.GetFromJsonAsync<Read>(product));
Print("B", await b.GetFromJsonAsync<Read>(product));
await PrintRedis("after warm-up");

await a.PutAsync($"/source/{id}/v2", null);
var invalidate = invalidation == "tag" ? $"/products/{id}/tag" : $"/products/{id}";
(await a.DeleteAsync(invalidate)).EnsureSuccessStatusCode();

Console.WriteLine($"-- source changed to v2, A removed the entry by {invalidation} --");
await PrintRedis("after invalidation");

if (readA)
{
    Print("A", await a.GetFromJsonAsync<Read>(product));
}

var clock = Stopwatch.StartNew();
var timeout = settings.Expiration + TimeSpan.FromSeconds(15);
var staleReads = 0;
var lastOrigin = "";

// Poll every 100 ms. Print a line whenever the origin changes, and at least every 5 s.
var nextPrint = TimeSpan.Zero;

while (clock.Elapsed < timeout)
{
    var elapsed = clock.Elapsed;
    var read = await b.GetFromJsonAsync<Read>(product);

    if (read!.Value == "v2")
    {
        Print("B", read, elapsed);
        await PrintRedis("at the end");

        Console.WriteLine();
        Console.WriteLine(staleReads == 0
            ? "B saw the change on its first read."
            : $"B returned stale data for {elapsed.TotalSeconds:0.0} s ({staleReads} stale reads).");
        
        return 0;
    }

    staleReads++;

    if (elapsed >= nextPrint || read.Origin != lastOrigin)
    {
        Print("B", read, elapsed);
        nextPrint = elapsed + TimeSpan.FromSeconds(5);
        lastOrigin = read.Origin;
    }

    await Task.Delay(TimeSpan.FromMilliseconds(100));
}

await PrintRedis("at the end");

Console.WriteLine();
Console.WriteLine($"B still returned stale data after {timeout.TotalSeconds:0} s.");

return 0;

static void Print(string instance, Read? read, TimeSpan? elapsed = null)
{
    var time = elapsed is { } t ? $"{t.TotalSeconds,5:0.0} s  " : "         ";
    Console.WriteLine($"{time}{instance} -> {read!.Value} ({read.Origin})");
    
    foreach (var c in read.L2Calls)
    {
        Console.WriteLine($"            L2 {c.Operation} {c.Key}{(c.Operation == "get" ? (c.Hit ? " hit" : " miss") : "")}");
    }
}

static async Task<double> RoundTrip(HttpClient client)
{
    var samples = new List<double>();

    for (var i = 0; i < 5; i++)
    {
        var watch = Stopwatch.StartNew();
        (await client.GetAsync("/")).EnsureSuccessStatusCode();
        samples.Add(watch.Elapsed.TotalMilliseconds);
    }

    return samples.Order().ElementAt(2);
}

async Task PrintRedis(string when)
{
    var keys = await a.GetFromJsonAsync<List<RedisKey>>($"/redis/{id}");
    Console.WriteLine($"         Redis {when}:{(keys!.Count == 0 ? " no keys" : "")}");

    foreach (var k in keys)
    {
        Console.WriteLine($"            {k.Key} ({k.Type}, {(k.TtlSeconds is { } ttl ? $"ttl {ttl:0} s" : "no ttl")})");
    }
}

record Settings(string Variant, string Instance, TimeSpan Expiration, TimeSpan LocalCacheExpiration, TimeSpan? TagsLocalCacheExpiration = null);
record Read(string Instance, string Value, string Origin, List<L2Call> L2Calls);
record L2Call(string Operation, string Key, bool Hit);
record RedisKey(string Key, string Type, double? TtlSeconds);
