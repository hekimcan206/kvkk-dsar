using KvkkDsar.Api.Persistence;
using KvkkDsar.Discovery;
using KvkkDsar.IntegrationTests.Infrastructure;
using KvkkDsar.Planning;
using KvkkDsar.Postgres;
using KvkkDsar.Synthetic;
using Npgsql;

namespace KvkkDsar.IntegrationTests;

public sealed class SourceTests(DsarEnvironment env)
{
    [Fact]
    public async Task Catalog_read_from_postgres_equals_the_declared_one()
    {
        var expected = ClinicDatabase.Catalog();
        env.Catalog.ForeignKeys.Should().BeEquivalentTo(expected.ForeignKeys);
        env.Catalog.Tables.Select(t => (t.Name, t.PrimaryKey)).Should().BeEquivalentTo(expected.Tables.Select(t => (t.Name, t.PrimaryKey)));
        foreach (var table in expected.Tables)
        {
            env.Catalog.Table(table.Name)!.Columns.Should().BeEquivalentTo(table.Columns, $"columns of {table.Name}");
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Postgres_discovery_equals_the_in_memory_reference_for_every_person_in_both_modes()
    {
        var engine = env.Engine();
        var memory = ClinicDatabase.InMemory(env.Dataset);
        // A pristine clone: the API tests erase people from the shared clinic database.
        await using var finder = PersistenceExtensions.CreateDataSource(env.FinderConnection(await env.CloneClinicAsync()));
        var reader = new PostgresSourceReader(finder, env.Catalog);
        var differences = new List<string>();
        foreach (var person in env.Dataset.People)
        {
            foreach (var mode in new[] { DiscoveryOptions.Full, DiscoveryOptions.Naive })
            {
                var pg = await engine.DiscoverAsync("clinic", reader, person.Identifiers(), mode, TestContext.Current.CancellationToken);
                var mem = await engine.DiscoverAsync("clinic", memory, person.Identifiers(), mode, TestContext.Current.CancellationToken);
                if (pg.Fingerprint() != mem.Fingerprint())
                {
                    differences.Add($"person {person.Index} ({(mode.Normalise ? "full" : "naive")}): pg {pg.Owned.Count}/{pg.Mentions.Count} vs memory {mem.Owned.Count}/{mem.Mentions.Count}");
                }
            }

            var truth = env.Dataset.Truth[person.Index];
            var full = await engine.DiscoverAsync("clinic", reader, person.Identifiers(), cancellationToken: TestContext.Current.CancellationToken);
            full.Owned.Select(o => (o.Table, o.Key)).ToHashSet().SetEquals(truth.Owned).Should().BeTrue($"person {person.Index} owned rows match the ground truth");
        }

        differences.Should().BeEmpty();
    }

    [Fact]
    public async Task Finder_can_only_read_and_eraser_cannot_insert()
    {
        var finderDelete = async () => await DsarEnvironment.ExecAsync(env.FinderConnection(), "DELETE FROM clinic.customers WHERE id = 1");
        (await finderDelete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var eraserInsert = async () => await DsarEnvironment.ExecAsync(env.EraserConnection(), "INSERT INTO clinic.subeler (id, sube_adi) VALUES (999, 'x')");
        (await eraserInsert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var eraserDdl = async () => await DsarEnvironment.ExecAsync(env.EraserConnection(), "DROP TABLE clinic.audit_log");
        await eraserDdl.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Audit_chain_is_append_only_even_for_the_schema_owner()
    {
        var appDelete = async () => await DsarEnvironment.ExecAsync(env.AppConnection, "DELETE FROM dsar.audit_events");
        (await appDelete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var ownerUpdate = async () => await DsarEnvironment.ExecAsync(env.MigratorConnection, "UPDATE dsar.audit_events SET actor = 'x'");
        // No rows may exist yet; TRUNCATE fires the statement trigger regardless.
        var ownerTruncate = async () => await DsarEnvironment.ExecAsync(env.MigratorConnection, "TRUNCATE dsar.audit_events CASCADE");
        (await ownerTruncate.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        await ownerUpdate.Should().NotThrowAsync("an UPDATE that matches rows is refused by the row trigger; covered by the API tests");

        var appDeleteRequests = async () => await DsarEnvironment.ExecAsync(env.AppConnection, "DELETE FROM dsar.requests");
        (await appDeleteRequests.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Index_advisor_flags_unindexed_foreign_keys_and_normalised_lookups()
    {
        var engine = env.Engine();
        var advice = await IndexAdvisor.AdviseAsync(env.Finder, env.Catalog, engine.Keys, minRows: 0, TestContext.Current.CancellationToken);

        advice.Should().Contain(a => a.Table == "randevular" && a.Column == "hasta_id" && a.Ddl.Contains("(\"hasta_id\")", StringComparison.Ordinal));
        advice.Should().Contain(a => a.Table == "customers" && a.Column == "phone_number" && a.Ddl.Contains("right(regexp_replace(", StringComparison.Ordinal));
        advice.Should().Contain(a => a.Table == "randevular" && a.Column == "notlar" && a.Ddl.Contains("gin_trgm_ops", StringComparison.Ordinal));
        advice.Should().NotContain(a => a.Table == "hastalar" && a.Column == "tc_kimlik_no", "the application's own TCKN index is used as is");
    }
}

/// <summary>The executor against a private clone of the clinic: erase, drift, rollback and racing executors.</summary>
public sealed class ExecutorTests(DsarEnvironment env)
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    private sealed record Ctx(string Db, NpgsqlDataSource Finder, NpgsqlDataSource Eraser, NpgsqlDataSource Owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Finder.DisposeAsync();
            await Eraser.DisposeAsync();
            await Owner.DisposeAsync();
        }
    }

    private async Task<Ctx> CloneAsync()
    {
        var db = await env.CloneClinicAsync();
        return new Ctx(db, PersistenceExtensions.CreateDataSource(env.FinderConnection(db)), PersistenceExtensions.CreateDataSource(env.EraserConnection(db)),
            PersistenceExtensions.CreateDataSource(env.Owner(db)));
    }

    private async Task<(DiscoveryResult, ErasurePlan)> PlanAsync(Ctx ctx, Person person)
    {
        var engine = env.Engine();
        var reader = new PostgresSourceReader(ctx.Finder, env.Catalog);
        var discovery = await engine.DiscoverAsync("clinic", reader, person.Identifiers());
        var anchors = new Dictionary<(string, string), IReadOnlyDictionary<string, string?>>();
        foreach (var (table, columns) in ErasurePlanner.AnchorColumns(env.Policy, discovery))
        {
            foreach (var (key, v) in await reader.FetchAsync(env.Catalog.Table(table)!, [.. discovery.Owned.Where(o => o.Table == table).Select(o => o.Key)], columns, CancellationToken.None))
            {
                anchors[(table, key)] = v;
            }
        }

        return (discovery, ErasurePlanner.Plan(discovery, env.Catalog, env.Inventory, env.Policy, anchors, Today));
    }

    /// <summary>MD5 of every row of every table, except the rows the plan touches: what must not change.</summary>
    private async Task<string> ChecksumExceptAsync(NpgsqlDataSource ds, IEnumerable<PlannedAction> touched)
    {
        var skip = touched.GroupBy(a => a.Table).ToDictionary(g => g.Key, g => g.Select(a => long.Parse(a.Key, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        var parts = new List<string>();
        foreach (var table in env.Catalog.Tables.Select(t => t.Name).Order(StringComparer.Ordinal))
        {
            await using var cmd = ds.CreateCommand($"SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY t.id)), '') FROM clinic.{table} t WHERE NOT (t.id = ANY($1))");
            cmd.Parameters.Add(new() { Value = skip.GetValueOrDefault(table, []) });
            parts.Add($"{table}:{await cmd.ExecuteScalarAsync()}");
        }

        return string.Join(';', parts);
    }

    [Fact]
    public async Task Executing_plans_for_twelve_people_leaves_exactly_the_kept_rows_and_touches_nobody_else()
    {
        await using var ctx = await CloneAsync();
        var engine = env.Engine();
        var people = env.Dataset.People.Where(p => env.Dataset.Truth[p.Index].Owned.Count > 0).Where((_, i) => i % 23 == 0).Take(12).ToList();
        foreach (var person in people)
        {
            var (_, plan) = await PlanAsync(ctx, person);
            var before = await ChecksumExceptAsync(ctx.Owner, plan.Actions);
            var result = await new PlanExecutor(ctx.Eraser, env.Catalog, engine).ExecuteAsync(plan, person.Identifiers(), TestContext.Current.CancellationToken);

            result.Applied.Should().BeTrue();
            result.RowsDeleted.Should().Be(plan.Actions.Count(a => a.Kind == PlannedActionKind.Erase));
            (await ChecksumExceptAsync(ctx.Owner, plan.Actions)).Should().Be(before, $"rows outside person {person.Index}'s plan are untouched");

            var after = await engine.DiscoverAsync("clinic", new PostgresSourceReader(ctx.Finder, env.Catalog), person.Identifiers(), cancellationToken: TestContext.Current.CancellationToken);
            var kept = plan.Actions.Where(a => a.Kind is PlannedActionKind.Retain or PlannedActionKind.Anonymise).Select(a => (a.Table, a.Key)).ToHashSet();
            after.Owned.Where(o => !kept.Contains((o.Table, o.Key))).Should().BeEmpty("only kept rows can still be found");
            after.Mentions.Where(m => !kept.Contains((m.Table, m.Key))).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_new_row_after_planning_is_drift_and_nothing_changes()
    {
        await using var ctx = await CloneAsync();
        var person = env.Dataset.People.First(p => env.Dataset.Truth[p.Index].Owned.Any(o => o.Table == "hastalar"));
        var (discovery, plan) = await PlanAsync(ctx, person);
        var hastaId = discovery.Owned.First(o => o.Table == "hastalar").Key;
        await DsarEnvironment.ExecAsync(env.Owner(ctx.Db),
            $"INSERT INTO clinic.randevular (id, hasta_id, randevu_zamani) VALUES (900001, {hastaId}, now())");
        var before = await ChecksumExceptAsync(ctx.Owner, []);

        var result = await new PlanExecutor(ctx.Eraser, env.Catalog, env.Engine()).ExecuteAsync(plan, person.Identifiers(), TestContext.Current.CancellationToken);

        result.Applied.Should().BeFalse();
        result.Drift.Should().BeTrue();
        (await ChecksumExceptAsync(ctx.Owner, [])).Should().Be(before);
    }

    private sealed class FailBeforeCommit : IExecutionFaults
    {
        public void BeforeCommit() => throw new InvalidOperationException("simulated failure before commit");
    }

    [Fact]
    public async Task A_failure_before_commit_rolls_back_every_statement()
    {
        await using var ctx = await CloneAsync();
        var person = env.Dataset.People.First(p => env.Dataset.Truth[p.Index].Owned.Count > 3 && env.Dataset.Truth[p.Index].Owned.Any(o => o.Table == "customers"));
        var (_, plan) = await PlanAsync(ctx, person);
        var before = await ChecksumExceptAsync(ctx.Owner, []);

        var run = () => new PlanExecutor(ctx.Eraser, env.Catalog, env.Engine(), new FailBeforeCommit()).ExecuteAsync(plan, person.Identifiers(), TestContext.Current.CancellationToken);

        await run.Should().ThrowAsync<InvalidOperationException>();
        (await ChecksumExceptAsync(ctx.Owner, [])).Should().Be(before, "a half-erased person is worse than an unerased one");
    }

    [Fact]
    public async Task Two_executors_racing_on_one_plan_apply_it_exactly_once()
    {
        await using var ctx = await CloneAsync();
        var person = env.Dataset.People.First(p => env.Dataset.Truth[p.Index].Owned.Any(o => o.Table == "customers"));
        var (_, plan) = await PlanAsync(ctx, person);

        var runs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            new PlanExecutor(ctx.Eraser, env.Catalog, env.Engine()).ExecuteAsync(plan, person.Identifiers(), TestContext.Current.CancellationToken))));

        runs.Count(r => r.Applied).Should().Be(1);
        runs.Count(r => r.Drift).Should().Be(1, "the loser waits on the row locks, then sees the rows gone");
    }

    [Fact]
    public async Task Mention_redaction_cuts_only_the_identifier_out_of_the_note()
    {
        await using var ctx = await CloneAsync();
        var person = env.Dataset.People.First(p => env.Dataset.Truth[p.Index].Mentions.Any(m => m.Table == "randevular"));
        var key = env.Dataset.Truth[person.Index].Mentions.First(m => m.Table == "randevular").Key;
        var (_, plan) = await PlanAsync(ctx, person);
        async Task<string> Note()
        {
            await using var cmd = ctx.Owner.CreateCommand($"SELECT notlar FROM clinic.randevular WHERE id = {key}");
            return (string)(await cmd.ExecuteScalarAsync())!;
        }

        var original = await Note();
        await new PlanExecutor(ctx.Eraser, env.Catalog, env.Engine()).ExecuteAsync(plan, person.Identifiers(), TestContext.Current.CancellationToken);
        var redacted = await Note();

        redacted.Should().Contain("[silindi]").And.StartWith("Randevu ertelendi, hasta yakını ile").And.EndWith("üzerinden görüşüldü.");
        KvkkDsar.Identifiers.FreeTextMatcher.Contains(redacted, KvkkDsar.Identifiers.IdentifierKind.Phone, person.Phone).Should().BeFalse();
        original.Should().NotBe(redacted);
    }
}
