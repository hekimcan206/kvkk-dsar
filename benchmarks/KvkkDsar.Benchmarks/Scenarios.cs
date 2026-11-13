using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KvkkDsar.Api.Persistence;
using KvkkDsar.Discovery;
using KvkkDsar.Evidence;
using KvkkDsar.Inventory;
using KvkkDsar.Planning;
using KvkkDsar.Postgres;
using KvkkDsar.Synthetic;
using Npgsql;
using Testcontainers.PostgreSql;

namespace KvkkDsar.Benchmarks;

internal static class Scenarios
{
    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "samples");
    private static readonly DataInventory Inventory = DataInventory.Load(Path.Combine(Samples, "clinic.classification.json"));
    private static readonly DsarPolicy Policy = DsarPolicy.Load(Path.Combine(Samples, "dsar-policy.json"));
    private static readonly DateOnly Today = new(2026, 11, 13);
    private static readonly string[] ErasureOnly = ["Erasure"];

    private sealed record Counts(int Tp, int Fp, int Fn)
    {
        public double Precision => Tp + Fp == 0 ? 1 : Math.Round((double)Tp / (Tp + Fp), 4);

        public double Recall => Tp + Fn == 0 ? 1 : Math.Round((double)Tp / (Tp + Fn), 4);
    }

    // ------------------------------------------------------------------------------------------------ accuracy

    public static async Task<object> AccuracyAsync(int people)
    {
        var sw = Stopwatch.StartNew();
        var dataset = ClinicDataset.Generate(people, 2026);
        var source = ClinicDatabase.InMemory(dataset);
        var engine = DiscoveryEngine.Create(Inventory, Policy, ClinicDatabase.Catalog());
        Console.WriteLine($"generated {people} people, {dataset.Tables.Values.Sum(t => t.Rows.Count)} rows in {sw.Elapsed.TotalSeconds:0.0} s");

        var modes = new (string Name, DiscoveryOptions Options)[]
        {
            ("full", DiscoveryOptions.Full),
            ("no-normalisation", DiscoveryOptions.Full with { Normalise = false }),
            ("no-foreign-keys", DiscoveryOptions.Full with { FollowForeignKeys = false }),
            ("mentions-as-ownership", DiscoveryOptions.Full with { RespectMentions = false }),
            ("naive (exact raw match, no FK, no mentions)", DiscoveryOptions.Naive),
        };

        var results = new List<object>();
        foreach (var (name, options) in modes)
        {
            int oTp = 0, oFp = 0, oFn = 0, mTp = 0, mFp = 0, mFn = 0, peopleWithWrongRows = 0, peopleFullyFound = 0, withData = 0;
            foreach (var person in dataset.People)
            {
                var truth = dataset.Truth[person.Index];
                var r = await engine.DiscoverAsync("clinic", source, person.Identifiers(), options);
                var owned = r.Owned.Select(o => (o.Table, o.Key)).ToHashSet();
                var mentions = r.Mentions.Where(m => !truth.Owned.Contains((m.Table, m.Key))).Select(m => (m.Table, m.Key, m.Column)).ToHashSet();
                oTp += owned.Count(truth.Owned.Contains);
                var wrong = owned.Count(o => !truth.Owned.Contains(o));
                oFp += wrong;
                oFn += truth.Owned.Count(o => !owned.Contains(o));
                mTp += mentions.Count(truth.Mentions.Contains);
                mFp += mentions.Count(m => !truth.Mentions.Contains(m));
                mFn += truth.Mentions.Count(m => !mentions.Contains(m));
                peopleWithWrongRows += wrong > 0 ? 1 : 0;
                if (truth.Owned.Count > 0)
                {
                    withData++;
                    peopleFullyFound += truth.Owned.IsSubsetOf(owned) ? 1 : 0;
                }
            }

            var o = new Counts(oTp, oFp, oFn);
            var m = new Counts(mTp, mFp, mFn);
            results.Add(new
            {
                mode = name,
                ownedRows = new { truePositives = oTp, falsePositives = oFp, falseNegatives = oFn, o.Precision, o.Recall },
                mentions = new { truePositives = mTp, falsePositives = mFp, falseNegatives = mFn, m.Precision, m.Recall },
                peopleWithAllRowsFound = $"{peopleFullyFound}/{withData}",
                peopleWithSomeoneElsesRowsClaimed = peopleWithWrongRows,
            });
            Console.WriteLine($"{name}: owned P {o.Precision} R {o.Recall}; mentions P {m.Precision} R {m.Recall}; wrong-row people {peopleWithWrongRows}");
        }

        return new { people, rows = dataset.Tables.Values.Sum(t => t.Rows.Count), truthOwned = dataset.Truth.Values.Sum(t => t.Owned.Count), truthMentions = dataset.Truth.Values.Sum(t => t.Mentions.Count), modes = results };
    }

    // ------------------------------------------------------------------------------------------------ discovery on PostgreSQL

    public static async Task<object> DiscoveryAsync(IReadOnlyList<int> sizes, int subjects)
    {
        var passwords = new Dictionary<string, string>
        {
            ["MIGRATOR_PASSWORD"] = "m",
            ["APP_PASSWORD"] = "a",
            ["CLINIC_OWNER_PASSWORD"] = "o",
            ["FINDER_PASSWORD"] = "f",
            ["ERASER_PASSWORD"] = "e",
        };
        var builder = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("dsar")
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")), "/docker-entrypoint-initdb.d/01-roles.sh")
            // Same budget for every run, whatever else the host is doing: 2 CPUs, 2 GiB.
            .WithCreateParameterModifier(p => { p.HostConfig!.NanoCPUs = 2_000_000_000; p.HostConfig.Memory = 2L * 1024 * 1024 * 1024; })
            .WithCommand("-c", "shared_buffers=512MB");
        foreach (var (k, v) in passwords)
        {
            builder = builder.WithEnvironment(k, v);
        }

        await using var container = builder.Build();
        await container.StartAsync();
        string Conn(string user, string pw) => new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Username = user, Password = pw, Database = "clinic", Timeout = 60 }.ConnectionString;

        var runs = new List<object>();
        foreach (var size in sizes)
        {
            var sw = Stopwatch.StartNew();
            var dataset = ClinicDataset.Generate(size, 2026);
            var generateSeconds = sw.Elapsed.TotalSeconds;
            sw.Restart();
            await using (var owner = PersistenceExtensions.CreateDataSource(Conn("clinic_owner", "o")))
            {
                await ClinicDatabase.LoadAsync(owner, dataset);
            }

            var loadSeconds = sw.Elapsed.TotalSeconds;
            var rows = dataset.Tables.Values.Sum(t => t.Rows.Count);
            Console.WriteLine($"[{size}] generated in {generateSeconds:0.0} s, loaded {rows} rows in {loadSeconds:0.0} s");

            await using var finder = PersistenceExtensions.CreateDataSource(Conn("dsar_finder", "f"));
            await using var eraser = PersistenceExtensions.CreateDataSource(Conn("dsar_eraser", "e"));
            var catalog = await PostgresCatalogReader.ReadAsync(finder, ClinicDataset.Schema);
            var engine = DiscoveryEngine.Create(Inventory, Policy, catalog);
            var reader = new PostgresSourceReader(finder, catalog);

            var rng = new Random(size);
            var pool = dataset.People.Where(p => dataset.Truth[p.Index].Owned.Count > 0).OrderBy(_ => rng.Next()).ToList();
            var probe = pool.Take(subjects).ToList();
            var executeBefore = pool.Skip(subjects).Take(30).ToList();
            var executeAfter = pool.Skip(subjects + 30).Take(30).ToList();

            var before = await MeasureDiscoveryAsync(engine, reader, probe);
            var execBefore = await MeasureExecutionAsync(engine, reader, eraser, catalog, executeBefore);
            Console.WriteLine($"[{size}] discovery before advice: {before.Latency}; execution {execBefore.Total}");

            var advice = await IndexAdvisor.AdviseAsync(finder, catalog, engine.Keys);
            sw.Restart();
            await using (var owner = PersistenceExtensions.CreateDataSource(Conn("clinic_owner", "o")))
            {
                foreach (var a in advice)
                {
                    await using var cmd = owner.CreateCommand(a.Ddl);
                    cmd.CommandTimeout = 600;
                    await cmd.ExecuteNonQueryAsync();
                }

                await using var analyze = owner.CreateCommand("ANALYZE");
                await analyze.ExecuteNonQueryAsync();
            }

            var indexSeconds = sw.Elapsed.TotalSeconds;
            var after = await MeasureDiscoveryAsync(engine, reader, probe);
            var execAfter = await MeasureExecutionAsync(engine, reader, eraser, catalog, executeAfter);
            Console.WriteLine($"[{size}] discovery after advice: {after.Latency}; execution {execAfter.Total}");

            runs.Add(new
            {
                people = size,
                rows,
                loadSeconds = Math.Round(loadSeconds, 1),
                advice = advice.Select(a => new { a.Table, a.Column, a.Purpose, estimatedRows = a.EstimatedRows }),
                indexBuildSeconds = Math.Round(indexSeconds, 1),
                discoveryBeforeAdvice = before,
                discoveryAfterAdvice = after,
                executionBeforeAdvice = execBefore,
                executionAfterAdvice = execAfter,
            });
        }

        return new { postgres = "17-alpine, 2 CPUs, 2 GiB, shared_buffers 512MB", subjectsPerSize = subjects, runs };
    }

    private sealed record DiscoveryRun(Summary Latency, double MedianQueries, double MedianOwnedRows);

    private static async Task<DiscoveryRun> MeasureDiscoveryAsync(DiscoveryEngine engine, PostgresSourceReader reader, IReadOnlyList<Person> people)
    {
        foreach (var p in people.Take(10))
        {
            await engine.DiscoverAsync("clinic", reader, p.Identifiers()); // warm-up: plans and buffers
        }

        var latencies = new List<double>();
        var queries = new List<double>();
        var owned = new List<double>();
        foreach (var p in people)
        {
            var sw = Stopwatch.StartNew();
            var r = await engine.DiscoverAsync("clinic", reader, p.Identifiers());
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            queries.Add(r.Queries);
            owned.Add(r.Owned.Count);
        }

        return new DiscoveryRun(Summary.Of(latencies), Summary.Of(queries).MedianMs, Summary.Of(owned).MedianMs);
    }

    private sealed record ExecutionRun(Summary Total, Summary Plan, Summary Execute, int RowsDeleted, int RowsUpdated, int Drifts);

    /// <summary>Discover + plan + execute (one transaction, drift check included) per subject, sequentially.</summary>
    private static async Task<ExecutionRun> MeasureExecutionAsync(DiscoveryEngine engine, PostgresSourceReader reader, NpgsqlDataSource eraser, Catalog.SourceCatalog catalog, IReadOnlyList<Person> people)
    {
        var total = new List<double>();
        var plan = new List<double>();
        var exec = new List<double>();
        int deleted = 0, updated = 0, drifts = 0;
        foreach (var p in people)
        {
            var sw = Stopwatch.StartNew();
            var discovery = await engine.DiscoverAsync("clinic", reader, p.Identifiers());
            var anchors = new Dictionary<(string, string), IReadOnlyDictionary<string, string?>>();
            foreach (var (table, columns) in ErasurePlanner.AnchorColumns(Policy, discovery))
            {
                foreach (var (key, v) in await reader.FetchAsync(catalog.Table(table)!, [.. discovery.Owned.Where(o => o.Table == table).Select(o => o.Key)], columns, CancellationToken.None))
                {
                    anchors[(table, key)] = v;
                }
            }

            var erasurePlan = ErasurePlanner.Plan(discovery, catalog, Inventory, Policy, anchors, Today);
            plan.Add(sw.Elapsed.TotalMilliseconds);
            var e = Stopwatch.StartNew();
            var result = await new PlanExecutor(eraser, catalog, engine).ExecuteAsync(erasurePlan, p.Identifiers());
            exec.Add(e.Elapsed.TotalMilliseconds);
            total.Add(sw.Elapsed.TotalMilliseconds);
            deleted += result.RowsDeleted;
            updated += result.RowsUpdated;
            drifts += result.Drift ? 1 : 0;
        }

        return new ExecutionRun(Summary.Of(total), Summary.Of(plan), Summary.Of(exec), deleted, updated, drifts);
    }

    // ------------------------------------------------------------------------------------------------ evidence

    public static async Task<object> EvidenceAsync(int iterations)
    {
        var dataset = ClinicDataset.Generate(600, 7);
        var source = ClinicDatabase.InMemory(dataset);
        var catalog = ClinicDatabase.Catalog();
        var engine = DiscoveryEngine.Create(Inventory, Policy, catalog);
        var person = dataset.People.OrderByDescending(p => dataset.Truth[p.Index].Owned.Count).First();
        var discovery = await engine.DiscoverAsync("clinic", source, person.Identifiers());
        var anchors = new Dictionary<(string, string), IReadOnlyDictionary<string, string?>>();
        foreach (var (table, columns) in ErasurePlanner.AnchorColumns(Policy, discovery))
        {
            foreach (var (rowKey, v) in await source.FetchAsync(catalog.Table(table)!, [.. discovery.Owned.Where(o => o.Table == table).Select(o => o.Key)], columns, CancellationToken.None))
            {
                anchors[(table, rowKey)] = v;
            }
        }

        var plan = ErasurePlanner.Plan(discovery, catalog, Inventory, Policy, anchors, Today);
        var files = new Dictionary<string, byte[]>
        {
            ["discovery-clinic.json"] = JsonSerializer.SerializeToUtf8Bytes(discovery),
            ["plan-clinic.json"] = JsonSerializer.SerializeToUtf8Bytes(plan, ErasurePlan.Json),
            ["response-tr.md"] = Encoding.UTF8.GetBytes(new string('x', 4000)),
            ["response-en.md"] = Encoding.UTF8.GetBytes(new string('x', 4000)),
        };
        var audit = new List<AuditEvent>();
        for (var i = 0; i < 20; i++)
        {
            audit.Add(AuditChain.Next(audit.LastOrDefault(), DateTimeOffset.UtcNow, "dpo", $"step.{i}", $"{{\"i\":{i},\"note\":\"{new string('y', 200)}\"}}"));
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var build = new List<double>();
        var verify = new List<double>();
        byte[] pack = [];
        for (var i = -20; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            pack = EvidencePack.Build(Guid.NewGuid(), "DSAR-2026-000001", DateTimeOffset.UtcNow, audit, files, key);
            var b = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var report = EvidencePack.Verify(new MemoryStream(pack), EvidencePack.KeyId(key));
            var v = sw.Elapsed.TotalMilliseconds;
            if (!report.Valid)
            {
                throw new InvalidOperationException("pack did not verify");
            }

            if (i >= 0)
            {
                build.Add(b);
                verify.Add(v);
            }
        }

        return new { ownedRows = discovery.Owned.Count, planActions = plan.Actions.Count, auditEvents = audit.Count, packBytes = pack.Length, build = Summary.Of(build), verify = Summary.Of(verify) };
    }

    // ------------------------------------------------------------------------------------------------ HTTP against docker compose

    public static async Task<object> HttpAsync(Uri api, int requests)
    {
        // The compose stack seeds 2,000 people with seed 2026; the same generator gives us their identifiers.
        var dataset = ClinicDataset.Generate(int.Parse(Environment.GetEnvironmentVariable("SEED_PEOPLE") ?? "2000", System.Globalization.CultureInfo.InvariantCulture), 2026);
        var offset = int.Parse(Environment.GetEnvironmentVariable("OFFSET") ?? "100", System.Globalization.CultureInfo.InvariantCulture);
        // People with a TCKN-bearing record (patient or staff file): only they have a contact on file for the OTP.
        var people = dataset.People.Where(p => dataset.Truth[p.Index].Owned.Any(o => o.Table is "hastalar" or "personel")).Skip(offset).Take(requests).ToList();
        HttpClient Client(string key)
        {
            var c = new HttpClient { BaseAddress = api, Timeout = TimeSpan.FromMinutes(2) };
            c.DefaultRequestHeaders.Add("X-Api-Key", key);
            return c;
        }

        using var intake = Client("demo-intake-key");
        using var verifier = Client("demo-verifier-key");
        using var dpo = Client("demo-dpo-key");
        using var legal = Client("demo-legal-key");
        using var ops = Client("demo-ops-key");
        using var auditor = Client("demo-audit-key");
        var stages = new Dictionary<string, List<double>>();
        var totals = new List<double>();
        var packSizes = new List<double>();
        var manualReview = 0;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        async Task<JsonElement> Step(string stage, Func<Task<HttpResponseMessage>> call)
        {
            var sw = Stopwatch.StartNew();
            using var response = await call();
            var body = await response.Content.ReadAsStringAsync();
            if (!stages.TryGetValue(stage, out var list))
            {
                stages[stage] = list = [];
            }

            list.Add(sw.Elapsed.TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{stage}: {(int)response.StatusCode} {body}");
            }

            return body.Length > 0 && body[0] is '{' or '[' ? JsonSerializer.Deserialize<JsonElement>(body, options) : default;
        }

        foreach (var p in people)
        {
            var total = Stopwatch.StartNew();
            var created = await Step("create", () => intake.PostAsJsonAsync("api/v1/requests", new
            {
                types = ErasureOnly,
                channel = "WebPortal",
                subject = new { nationalId = p.Tckn, email = p.Email, phone = p.Phone, firstName = p.FirstName, lastName = p.LastName, birthDate = p.BirthDate },
            }));
            var id = created.GetProperty("id").GetString();
            try
            {
                await Step("otp-challenge", () => verifier.PostAsync($"api/v1/requests/{id}/verification/challenge", null));
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("ManualReview", StringComparison.Ordinal))
            {
                // No phone or e-mail on file: correctly sent to manual review, not part of the timed flow.
                manualReview++;
                continue;
            }

            var inbox = await Step("demo-inbox", () => verifier.GetAsync($"demo/inbox/{id}"));
            await Step("otp-confirm", () => verifier.PostAsJsonAsync($"api/v1/requests/{id}/verification/confirm", new { code = inbox.GetProperty("code").GetString() }));
            await Step("discovery", () => dpo.PostAsync($"api/v1/requests/{id}/discovery", null));
            var plan = await Step("plan", () => dpo.PostAsync($"api/v1/requests/{id}/plan", null));
            await Step("approve", () => legal.PostAsJsonAsync($"api/v1/requests/{id}/approve", new { planHash = plan.GetProperty("planHash").GetString() }));
            await Step("execute", () => ops.PostAsync($"api/v1/requests/{id}/execute", null));
            await Step("respond", () => dpo.PostAsync($"api/v1/requests/{id}/respond", null));
            total.Stop();
            totals.Add(total.Elapsed.TotalMilliseconds);
            var sw = Stopwatch.StartNew();
            var pack = await auditor.GetByteArrayAsync($"api/v1/requests/{id}/evidence");
            (stages.TryGetValue("evidence", out var ev) ? ev : stages["evidence"] = []).Add(sw.Elapsed.TotalMilliseconds);
            packSizes.Add(pack.Length);
        }

        return new
        {
            requests = totals.Count,
            sentToManualReview = manualReview,
            endToEndExcludingEvidence = Summary.Of(totals),
            stages = stages.ToDictionary(kv => kv.Key, kv => Summary.Of(kv.Value)),
            evidencePackBytes = Summary.Of(packSizes),
        };
    }
}
