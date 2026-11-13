using System.Text.Json;
using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using KvkkDsar.Identifiers;
using Npgsql;

namespace KvkkDsar.Postgres;

public sealed record IndexAdvice(string Table, string Column, string Purpose, double EstimatedRows, string Ddl);

/// <summary>
/// Asks PostgreSQL how it would run each discovery query (EXPLAIN, no execution) and proposes an index for every one
/// that would scan a whole large table. Two findings are typical: foreign-key columns are not indexed (PostgreSQL
/// never creates those automatically), and normalised phone/e-mail comparisons cannot use a plain column index.
/// </summary>
public static class IndexAdvisor
{
    public const double MinRows = 10_000;

    public static async Task<IReadOnlyList<IndexAdvice>> AdviseAsync(NpgsqlDataSource dataSource, SourceCatalog catalog, IReadOnlyList<KeyColumn> keys, double minRows = MinRows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(keys);
        var advice = new List<IndexAdvice>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var key in keys)
        {
            var (expression, predicate, trigram) = MatchSql.For(key, DiscoveryOptions.Full);
            var probe = key.Kind switch
            {
                IdentifierKind.NationalId => "10000000146",
                IdentifierKind.Phone => "5320000000",
                _ => "probe@example.invalid",
            };
            var sql = $"SELECT 1 FROM {MatchSql.Table(catalog.Schema, key.Table)} WHERE {predicate.Replace("$1", Literal(probe), StringComparison.Ordinal)}";
            var (seqScan, rows) = await ExplainAsync(connection, sql, MatchSql.Table(catalog.Schema, key.Table), key.Table, cancellationToken);
            if (seqScan && rows >= minRows)
            {
                var name = $"dsar_{key.Table}_{key.Column}_{(trigram ? "trgm" : "key")}_idx";
                var ddl = trigram
                    ? $"CREATE INDEX CONCURRENTLY {name} ON {MatchSql.Table(catalog.Schema, key.Table)} USING gin (({expression}) gin_trgm_ops);"
                    : $"CREATE INDEX CONCURRENTLY {name} ON {MatchSql.Table(catalog.Schema, key.Table)} (({expression}));";
                advice.Add(new IndexAdvice(key.Table, key.Column, $"{key.Kind} {key.Mode} lookup", rows, ddl));
            }
        }

        foreach (var fk in catalog.ForeignKeys)
        {
            var child = catalog.Table(fk.ChildTable)?.Column(fk.ChildColumn);
            if (child is null)
            {
                continue;
            }

            var sql = $"SELECT 1 FROM {MatchSql.Table(catalog.Schema, fk.ChildTable)} WHERE {MatchSql.Ident(fk.ChildColumn)} = ANY(CAST('{{1,2,3}}' AS {child.SqlType}[]))";
            var (seqScan, rows) = await ExplainAsync(connection, sql, MatchSql.Table(catalog.Schema, fk.ChildTable), fk.ChildTable, cancellationToken);
            if (seqScan && rows >= minRows)
            {
                advice.Add(new IndexAdvice(fk.ChildTable, fk.ChildColumn, $"foreign key {fk.Name} (discovery walk and parent DELETE checks)", rows,
                    $"CREATE INDEX CONCURRENTLY dsar_{fk.ChildTable}_{fk.ChildColumn}_fk_idx ON {MatchSql.Table(catalog.Schema, fk.ChildTable)} ({MatchSql.Ident(fk.ChildColumn)});"));
            }
        }

        return advice;
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<(bool SeqScan, double Rows)> ExplainAsync(NpgsqlConnection connection, string sql, string schemaTable, string table, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + sql, connection);
        var json = (string)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        using var doc = JsonDocument.Parse(json);
        var seq = false;
        double rows = 0;
        Walk(doc.RootElement[0].GetProperty("Plan"));
        await using (var size = new NpgsqlCommand("SELECT greatest(c.reltuples, 0)::float8 FROM pg_class c WHERE c.oid = to_regclass($1)", connection))
        {
            size.Parameters.Add(new NpgsqlParameter { Value = schemaTable });
            rows = await size.ExecuteScalarAsync(cancellationToken) is double d ? d : 0;
        }

        return (seq, rows);

        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("Relation Name", out var rel) && rel.GetString() == table)
            {
                var type = node.GetProperty("Node Type").GetString();
                seq |= type == "Seq Scan";
            }

            if (node.TryGetProperty("Plans", out var plans))
            {
                foreach (var child in plans.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }
    }
}
