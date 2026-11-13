using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using KvkkDsar.Benchmarks;

// Usage (Release build):
//   dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- accuracy    discovery vs ground truth, ablations (in memory)
//   dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- discovery   latency vs size, before/after index advice (Testcontainers PostgreSQL 17)
//   dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- evidence    evidence pack build / verify
//   dotnet run -c Release --project benchmarks/KvkkDsar.Benchmarks -- http        full erasure requests against `docker compose up`
// Results go to benchmark-results/<scenario>.json with a description of the environment.
var mode = args.FirstOrDefault() ?? "accuracy";
var outDir = Env("OUT_DIR", "benchmark-results");
Directory.CreateDirectory(outDir);
var environment = new
{
    os = RuntimeInformation.OSDescription,
    runtime = RuntimeInformation.FrameworkDescription,
    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString(),
    logicalCores = Environment.ProcessorCount,
};
Console.WriteLine($"environment: {JsonSerializer.Serialize(environment)}");

var started = Stopwatch.StartNew();
object result = mode switch
{
    "accuracy" => await Scenarios.AccuracyAsync(int.Parse(Env("PEOPLE", "5000"), CultureInfo.InvariantCulture)),
    "discovery" => await Scenarios.DiscoveryAsync([.. Env("SIZES", "10000,100000").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))],
        int.Parse(Env("SUBJECTS", "200"), CultureInfo.InvariantCulture)),
    "evidence" => await Scenarios.EvidenceAsync(int.Parse(Env("ITERATIONS", "300"), CultureInfo.InvariantCulture)),
    "http" => await Scenarios.HttpAsync(new Uri(Env("API_URL", "http://127.0.0.1:58762/")), int.Parse(Env("REQUESTS", "40"), CultureInfo.InvariantCulture)),
    _ => throw new ArgumentException($"Unknown scenario '{mode}'. Use accuracy, discovery, evidence or http."),
};

var json = JsonSerializer.Serialize(new { scenario = mode, measuredAt = DateTimeOffset.UtcNow, wallSeconds = Math.Round(started.Elapsed.TotalSeconds, 1), environment, result },
    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
await File.WriteAllTextAsync(Path.Combine(outDir, $"{mode}.json"), json);
Console.WriteLine(json);

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
