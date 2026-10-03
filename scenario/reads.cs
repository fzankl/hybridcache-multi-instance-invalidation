#:property PublishAot=false

// Counts the L2 calls of one instance while nothing is removed.
//
//   dotnet run scenario/reads.cs -- <variant> [--a <url of instance A> --b <url of instance B>]
//
// variant: hybridcache | fusioncache | fusioncache-backplane | fusioncache-tags
// --a and --b: only needed when the instances do not run on the local ports, e.g. in Azure.
//
// 1. A and B read the product, so both have it in their L1.
// 2. B is read every 100 ms, until 5 s before the L2 entry expires. Nothing is removed.
// 3. The L2 calls of those reads are counted per key.
//
// That is what the variant costs on L2 while the data does not change.

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

if (args.Length == 0 || !ports.TryGetValue(args[0], out var portA) || (urlA is null) != (urlB is null))
{
    Console.WriteLine("Usage: dotnet run scenario/reads.cs -- <hybridcache|fusioncache|fusioncache-backplane|fusioncache-tags> [--a <url> --b <url>]");
    return 1;
}

var variant = args[0];
using var a = new HttpClient { BaseAddress = new Uri(urlA ?? $"http://localhost:{portA}") };
using var b = new HttpClient { BaseAddress = new Uri(urlB ?? $"http://localhost:{portA + 1}") };

var settings = await a.GetFromJsonAsync<Settings>("/");
var id = Guid.NewGuid().ToString("n")[..8];
var product = $"/products/{id}";
var duration = settings!.Expiration - TimeSpan.FromSeconds(5);

Console.WriteLine($"{variant}, no invalidation, B read for {duration.TotalSeconds:0} s");
Console.WriteLine($"B {b.BaseAddress}");
Console.WriteLine($"L1 (LocalCacheExpiration) {settings.LocalCacheExpiration}, L2 (Expiration) {settings.Expiration}");

if (settings.TagsLocalCacheExpiration is { } tagsLocalCacheExpiration)
{
    Console.WriteLine($"Tag markers in L1 (TagsDefaultEntryOptions.MemoryCacheDuration) {tagsLocalCacheExpiration}");
}

var settingsB = await b.GetFromJsonAsync<Settings>("/");

if (settings.Variant != variant || settingsB!.Variant != variant)
{
    Console.WriteLine($"A runs variant '{settings.Variant}', B runs '{settingsB!.Variant}', expected '{variant}'.");
    return 1;
}

Console.WriteLine();

await a.PutAsync($"/source/{id}/v1", null);
await a.GetFromJsonAsync<Read>(product);

// The clock starts with A's load, which wrote the L2 entry. B's first read is part of the count.
var clock = Stopwatch.StartNew();
var reads = 0;
var readsWithL2 = 0;
var calls = new Dictionary<string, int>();

while (clock.Elapsed < duration)
{
    var elapsed = clock.Elapsed;
    var read = await b.GetFromJsonAsync<Read>(product);
    reads++;

    if (read!.L2Calls.Count > 0)
    {
        readsWithL2++;
        Console.WriteLine($"{elapsed.TotalSeconds,5:0.0} s  B -> {read.Value} ({read.Origin})");
        
        foreach (var c in read.L2Calls)
        {
            Console.WriteLine($"            L2 {c.Operation} {c.Key}{(c.Operation == "get" ? (c.Hit ? " hit" : " miss") : "")}");
            var call = $"{c.Operation} {c.Key}";
            calls[call] = calls.GetValueOrDefault(call) + 1;
        }
    }

    await Task.Delay(TimeSpan.FromMilliseconds(100));
}

Console.WriteLine();

foreach (var (call, count) in calls.OrderBy(c => c.Key))
{
    Console.WriteLine($"{count,4} x L2 {call}");
}

Console.WriteLine();
Console.WriteLine($"{reads} reads in {duration.TotalSeconds:0} s, {readsWithL2} of them touched L2, {calls.Values.Sum()} L2 calls.");

return 0;

record Settings(string Variant, string Instance, TimeSpan Expiration, TimeSpan LocalCacheExpiration, TimeSpan? TagsLocalCacheExpiration = null);
record Read(string Instance, string Value, string Origin, List<L2Call> L2Calls);
record L2Call(string Operation, string Key, bool Hit);
