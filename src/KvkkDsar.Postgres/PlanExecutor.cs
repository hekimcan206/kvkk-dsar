using System.Diagnostics;
using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using KvkkDsar.Identifiers;
using KvkkDsar.Inventory;
using KvkkDsar.Planning;
using Npgsql;

namespace KvkkDsar.Postgres;

/// <summary>One statement the executor ran: the SQL template (no values) and how many rows it touched.</summary>
public sealed record ExecutionStep(string Table, string Operation, int Rows, string Sql);

public sealed record ExecutionResult(
    bool Applied,
    bool Drift,
    string? DriftDetail,
    IReadOnlyList<ExecutionStep> Steps,
    int RowsDeleted,
    int RowsUpdated,
    double ElapsedMs);

/// <summary>Lets tests fail the executor at a precise point (after the changes, before commit).</summary>
public interface IExecutionFaults
{
    void BeforeCommit();
}

/// <summary>
/// Applies an approved plan to the source database in ONE transaction, as the eraser role (UPDATE/DELETE only):
/// <list type="number">
/// <item>Lock every planned row <c>FOR UPDATE</c> in a fixed order. That also blocks new child rows: inserting a child
/// takes <c>FOR KEY SHARE</c> on its parent, which conflicts.</item>
/// <item>Re-run discovery on the same transaction and compare its fingerprint with the one the plan was made from.
/// A new appointment or a changed e-mail since planning is drift: roll back and ask for a new plan, because the
/// approver approved something else.</item>
/// <item>Apply mention redactions and anonymisation, then delete children before parents, checking each statement's
/// row count against the plan.</item>
/// </list>
/// Any surprise rolls everything back: a half-erased person is worse than an unerased one, because nobody can tell
/// what is left.
/// </summary>
public sealed class PlanExecutor(NpgsqlDataSource eraser, SourceCatalog catalog, DiscoveryEngine engine, IExecutionFaults? faults = null)
{
    public async Task<ExecutionResult> ExecuteAsync(ErasurePlan plan, SubjectIdentifiers subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(subject);
        var started = Stopwatch.GetTimestamp();
        var steps = new List<ExecutionStep>();
        var order = catalog.TopologicalOrder().ToList();

        await using var connection = await eraser.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await Exec(connection, tx, "SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '120s'", cancellationToken);

        foreach (var table in plan.Actions.Select(a => a.Table).Distinct(StringComparer.Ordinal).OrderBy(t => order.IndexOf(t)))
        {
            var info = Table(table);
            var keys = plan.Actions.Where(a => a.Table == table).Select(a => a.Key).Order(KeyComparer.Instance).ToArray();
            var sql = $"SELECT {MatchSql.Ident(info.PrimaryKey!)} FROM {MatchSql.Table(catalog.Schema, table)} WHERE {PkIn(info)} ORDER BY 1 FOR UPDATE";
            var locked = await Exec(connection, tx, sql, cancellationToken, keys, readRows: true);
            steps.Add(new ExecutionStep(table, "lock", locked, sql));
        }

        var now = await engine.DiscoverAsync(plan.Source, new PostgresSourceReader(connection, tx, catalog), subject, DiscoveryOptions.Full, cancellationToken);
        if (!string.Equals(now.Fingerprint(), plan.DiscoveryFingerprint, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(cancellationToken);
            var detail = $"source changed since planning: now {now.Owned.Count} owned rows and {now.Mentions.Count} mentions";
            return new ExecutionResult(false, true, detail, steps, 0, 0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var updated = 0;
        foreach (var group in plan.Actions.Where(a => a.Changes.Count > 0)
                     .GroupBy(a => (a.Table, Signature: string.Join(',', a.Changes.Select(c => $"{c.Column}:{c.Treatment}"))))
                     .OrderBy(g => order.IndexOf(g.Key.Table)))
        {
            var info = Table(group.Key.Table);
            var keys = group.Select(a => a.Key).Order(KeyComparer.Instance).ToArray();
            var changes = group.First().Changes;
            var plain = changes.Where(c => c.Treatment != ColumnTreatment.RedactIdentifiers).ToList();
            if (plain.Count > 0)
            {
                var set = string.Join(", ", plain.Select(c => $"{MatchSql.Ident(c.Column)} = {Assignment(info, c)}"));
                var sql = $"UPDATE {MatchSql.Table(catalog.Schema, info.Name)} SET {set} WHERE {PkIn(info)}";
                var rows = await Exec(connection, tx, sql, cancellationToken, keys);
                Expect(rows, keys.Length, sql);
                steps.Add(new ExecutionStep(info.Name, group.First().Kind.ToString(), rows, sql));
                updated += rows;
            }

            foreach (var change in changes.Where(c => c.Treatment == ColumnTreatment.RedactIdentifiers))
            {
                updated += await RedactTextAsync(connection, tx, info, change.Column, keys, subject, steps, cancellationToken);
            }
        }

        var deleted = 0;
        foreach (var table in plan.DeleteOrder)
        {
            var info = Table(table);
            var keys = plan.Actions.Where(a => a.Table == table && a.Kind == PlannedActionKind.Erase).Select(a => a.Key).Order(KeyComparer.Instance).ToArray();
            var sql = $"DELETE FROM {MatchSql.Table(catalog.Schema, table)} WHERE {PkIn(info)}";
            var rows = await Exec(connection, tx, sql, cancellationToken, keys);
            Expect(rows, keys.Length, sql);
            steps.Add(new ExecutionStep(table, "delete", rows, sql));
            deleted += rows;
        }

        faults?.BeforeCommit();
        await tx.CommitAsync(cancellationToken);
        return new ExecutionResult(true, false, null, steps, deleted, updated, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task<int> RedactTextAsync(NpgsqlConnection connection, NpgsqlTransaction tx, TableInfo info, string column, string[] keys,
        SubjectIdentifiers subject, List<ExecutionStep> steps, CancellationToken cancellationToken)
    {
        var select = $"SELECT {MatchSql.Ident(info.PrimaryKey!)}::text, {MatchSql.Ident(column)} FROM {MatchSql.Table(catalog.Schema, info.Name)} WHERE {PkIn(info)}";
        var texts = new List<(string Key, string Text)>();
        await using (var cmd = new NpgsqlCommand(select, connection, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = keys });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(1))
                {
                    texts.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        var pk = info.Column(info.PrimaryKey!)!;
        var update = $"UPDATE {MatchSql.Table(catalog.Schema, info.Name)} SET {MatchSql.Ident(column)} = $2 WHERE {MatchSql.Ident(pk.Name)} = CAST($1 AS {pk.SqlType})";
        var rows = 0;
        await using (var batch = new NpgsqlBatch(connection, tx))
        {
            foreach (var (key, text) in texts)
            {
                var redacted = FreeTextMatcher.Redact(text, subject.Searchable());
                if (string.Equals(redacted, text, StringComparison.Ordinal))
                {
                    continue;
                }

                var command = new NpgsqlBatchCommand(update);
                command.Parameters.Add(new NpgsqlParameter { Value = key });
                command.Parameters.Add(new NpgsqlParameter { Value = redacted });
                batch.BatchCommands.Add(command);
            }

            if (batch.BatchCommands.Count > 0)
            {
                await batch.ExecuteNonQueryAsync(cancellationToken);
                foreach (var c in batch.BatchCommands)
                {
                    rows += (int)c.RecordsAffected;
                }
            }
        }

        steps.Add(new ExecutionStep(info.Name, "redact-identifiers", rows, update));
        return rows;
    }

    /// <summary>The SQL expression a column gets for a treatment; constants are typed from the catalog.</summary>
    public static string Assignment(TableInfo table, ColumnChange change)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(change);
        var column = table.Column(change.Column) ?? throw new InvalidOperationException($"Unknown column {table.Name}.{change.Column}.");
        var col = MatchSql.Ident(column.Name);
        var treatment = change.Treatment == ColumnTreatment.Null && !column.Nullable ? ColumnTreatment.Redact : change.Treatment;
        return (treatment, column.Type) switch
        {
            (ColumnTreatment.Null, _) => "NULL",
            (ColumnTreatment.TruncateToYear, LogicalType.Date) => $"date_trunc('year', {col})::date",
            (ColumnTreatment.TruncateToYear, LogicalType.Timestamp) => $"date_trunc('year', {col})",
            (_, LogicalType.Text or LogicalType.LongText) => column.MaxLength is { } n && n < ErasurePlanner.RedactedText.Length
                ? $"'{ErasurePlanner.RedactedText[..n]}'"
                : $"'{ErasurePlanner.RedactedText}'",
            (_, LogicalType.WholeNumber or LogicalType.ExactNumeric) => "0",
            (_, LogicalType.Date) => "DATE '1900-01-01'",
            (_, LogicalType.Timestamp) => "TIMESTAMP '1900-01-01 00:00:00'",
            (_, LogicalType.Boolean) => "false",
            (_, LogicalType.Binary) => "'\\x'::bytea",
            _ => column.Nullable ? "NULL" : throw new InvalidOperationException($"No redaction value for {table.Name}.{column.Name} ({column.SqlType})."),
        };
    }

    private TableInfo Table(string name) =>
        catalog.Table(name) is { PrimaryKey: not null } t ? t : throw new InvalidOperationException($"Table {name} has no single-column primary key.");

    private static string PkIn(TableInfo info)
    {
        var pk = info.Column(info.PrimaryKey!)!;
        return $"{MatchSql.Ident(pk.Name)} = ANY(CAST($1 AS {pk.SqlType}[]))";
    }

    private static void Expect(int actual, int expected, string sql)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected {expected} rows, statement touched {actual}: {sql}. Rolled back.");
        }
    }

    private static async Task<int> Exec(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken cancellationToken, string[]? keys = null, bool readRows = false)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        if (keys is not null)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = keys });
        }

        if (!readRows)
        {
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var count = 0;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
        }

        return count;
    }
}
