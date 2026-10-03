#:property PublishAot=false

// Runs every case of the results table and writes the raw output to one file per variant.
//
//   dotnet run scenario/matrix.cs -- --name <name> [--description <text>] [--out <directory>]
//                                    [--domain <default domain of the Container Apps environment>]
//                                    [--variants hybridcache,fusioncache,fusioncache-backplane,fusioncache-tags]
//                                    [--skip-replicas]
//
// Per variant, eight cases: key and tag, each with default options, with a read on A after the
// invalidation, with per-call options that set Expiration only, and with per-call options that
// set both values. Then the replica scenario, by key and by tag.
//
// Files: <out>/<date>-<name>-<variant>.txt. Existing files are never overwritten.
// Without --domain, the instances are expected on the local ports the AppHost assigns.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var name = Option("--name");
if (name is null)
{
    Console.WriteLine("Usage: dotnet run scenario/matrix.cs -- --name <name> [--description <text>] [--out <directory>] [--domain <domain>] [--variants <a,b,c>] [--skip-replicas]");
    return 1;
}

var scenarios = ScenarioDirectory();
var description = Option("--description") ?? "not described";
var domain = Option("--domain");
var variants = (Option("--variants") ?? "hybridcache,fusioncache,fusioncache-backplane,fusioncache-tags").Split(',');
var output = Path.GetFullPath(Option("--out") ?? Path.Combine(scenarios, "..", "results"));
var prefix = $"{DateTime.Now:yyyy-MM-dd}-{name}-";

Directory.CreateDirectory(output);

if (Directory.EnumerateFiles(output, prefix + "*.txt").Any())
{
    Console.WriteLine($"Results named {prefix}* exist already in {output}. Pick another --name.");
    return 1;
}

string[] cases =
[
    "key default",
    "key default read-a",
    "tag default",
    "tag default read-a",
    "key expiration-only",
    "tag expiration-only",
    "key complete",
    "tag complete"
];

var failed = false;

// Build both scenarios once, so the parallel runs below do not build the same file at the same time.
await Run("build", [Path.Combine(scenarios, "run.cs")]);
await Run("build", [Path.Combine(scenarios, "replicas.cs")]);

// Variants do not share keys, so they run side by side. The cases of one variant run in order.
await Task.WhenAll(variants.Select(async variant =>
{
    var file = Path.Combine(output, $"{prefix}{variant}.txt");
    await File.WriteAllTextAsync(file, $"environment: {description}{Environment.NewLine}");

    foreach (var @case in cases)
    {
        List<string> arguments = [Path.Combine(scenarios, "run.cs"), "--", variant, .. @case.Split(' ')];
        
        if (domain is not null)
        {
            arguments.AddRange(["--a", $"https://{variant}-a.{domain}", "--b", $"https://{variant}-b.{domain}"]);
        }

        var (result, exitCode) = await Run("run", arguments);
        await File.AppendAllTextAsync(file, $"{Environment.NewLine}===== {variant} {@case} ====={Environment.NewLine}{result}");
        
        Console.WriteLine($"{variant,-22} {@case,-22} {Summary(result, exitCode)}");
        
        if (exitCode != 0)
        {
            failed = true;
        }
    }
}));

if (!args.Contains("--skip-replicas"))
{
    var file = Path.Combine(output, $"{prefix}hybridcache-replicas.txt");
    await File.WriteAllTextAsync(file, $"environment: {description}{Environment.NewLine}");

    foreach (var invalidation in new[] { "key", "tag" })
    {
        List<string> arguments = [Path.Combine(scenarios, "replicas.cs"), "--", invalidation];
        
        if (domain is not null)
        {
            arguments.AddRange(["--url", $"https://hybridcache-replicas.{domain}"]);
        }

        var (result, exitCode) = await Run("run", arguments);
        await File.AppendAllTextAsync(file, $"{Environment.NewLine}===== replicas {invalidation} ====={Environment.NewLine}{result}");
        
        Console.WriteLine($"{"hybridcache-replicas",-22} {invalidation,-22} {Summary(result, exitCode)}");
        
        if (exitCode != 0)
        {
            failed = true;
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Raw output in {output}");

if (failed)
{
    Console.WriteLine("At least one case did not finish. Its output is in the file, but the table is incomplete.");
    return 1;
}

return 0;

static async Task<(string Output, int ExitCode)> Run(string command, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(command);
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start)!;
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (await standardOutput + await standardError, process.ExitCode);
}

// The last line of a scenario that states a result.
static string Summary(string result, int exitCode) =>
    exitCode != 0
        ? $"failed with exit code {exitCode}, see the file"
        : result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => Regex.IsMatch(line, "stale data|first read|changed \\d+ times")) ?? "no result, see the file";

static string ScenarioDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
