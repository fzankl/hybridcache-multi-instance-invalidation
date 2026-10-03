#:property PublishAot=false

// Shows the symptom as a user sees it: two replicas behind one endpoint.
//
//   dotnet run scenario/replicas.cs -- [key|tag] [--url <url of the endpoint>]
//
// 1. The source holds v1. The endpoint is read until both replicas have the product in their L1.
// 2. The source changes to v2, and the entry is removed through the endpoint.
//    One replica handles that request, the other one never hears about it.
// 3. The endpoint is read every 250 ms. Each line names the replica that answered.
//
// Every request opens a new connection, so the endpoint can pick a replica each time.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;

// The output looks the same on every machine, whatever its regional settings.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var invalidation = args.Contains("tag") ? "tag" : "key";

using var endpoint = new HttpClient { BaseAddress = new Uri(Option("--url") ?? "http://localhost:5111") };
endpoint.DefaultRequestHeaders.ConnectionClose = true;

var settings = await endpoint.GetFromJsonAsync<Settings>("/");
var id = Guid.NewGuid().ToString("n")[..8];
var product = $"/products/{id}";

Console.WriteLine($"{settings!.Variant}, two replicas behind one endpoint, invalidate by {invalidation}");
Console.WriteLine($"Endpoint {endpoint.BaseAddress}");
Console.WriteLine($"L1 (LocalCacheExpiration) {settings.LocalCacheExpiration}, L2 (Expiration) {settings.Expiration}");
Console.WriteLine();

await endpoint.PutAsync($"/source/{id}/v1", null);

var warm = new HashSet<string>();
for (var i = 0; i < 50 && warm.Count < 2; i++)
{
    var read = await endpoint.GetFromJsonAsync<Read>(product);
    if (warm.Add(read!.Instance))
    {
        Print(read);
    }
}

if (warm.Count < 2)
{
    Console.WriteLine("Only one replica answered in 50 reads. Is the endpoint balancing between two replicas?");
    return 1;
}

await endpoint.PutAsync($"/source/{id}/v2", null);

var removal = await endpoint.DeleteAsync(invalidation == "tag" ? $"/products/{id}/tag" : product);
var removedBy = await removal.Content.ReadFromJsonAsync<Removal>();

Console.WriteLine("-- source changed to v2 --");
Console.WriteLine($"-- {removedBy!.Instance} removed the entry by {invalidation} --");

var clock = Stopwatch.StartNew();
var duration = (invalidation == "tag" ? settings.Expiration : settings.LocalCacheExpiration) + TimeSpan.FromSeconds(3);
var stale = new Dictionary<string, int>();
var fresh = new Dictionary<string, int>();
var flips = 0;
string? previous = null;

while (clock.Elapsed < duration)
{
    var elapsed = clock.Elapsed;
    var read = (await endpoint.GetFromJsonAsync<Read>(product))!;
    Print(read, elapsed);

    var counter = read.Value == "v2" ? fresh : stale;
    counter[read.Instance] = counter.GetValueOrDefault(read.Instance) + 1;

    if (previous is not null && previous != read.Value)
    {
        flips++;
    }

    previous = read.Value;
    await Task.Delay(TimeSpan.FromMilliseconds(250));
}

Console.WriteLine();

foreach (var instance in warm.Order())
{
    Console.WriteLine($"{instance}: {stale.GetValueOrDefault(instance)} x v1, {fresh.GetValueOrDefault(instance)} x v2");
}

Console.WriteLine($"The value changed {flips} times between consecutive reads.");
return 0;

static void Print(Read read, TimeSpan? elapsed = null)
{
    var time = elapsed is { } t ? $"{t.TotalSeconds,5:0.0} s  " : "         ";
    Console.WriteLine($"{time}{read.Instance} -> {read.Value} ({read.Origin})");
}

record Settings(string Variant, string Instance, TimeSpan Expiration, TimeSpan LocalCacheExpiration);
record Read(string Instance, string Value, string Origin);
record Removal(string Instance);
