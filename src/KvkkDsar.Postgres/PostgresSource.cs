using System.Globalization;
using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using KvkkDsar.Identifiers;
using Npgsql;

namespace KvkkDsar.Postgres;

/// <summary>Reads tables, columns, single-column primary keys and foreign keys of one schema from the PostgreSQL catalog.</summary>
public static class PostgresCatalogReader
{
    public static async Task<SourceCatalog> ReadAsync(NpgsqlDataSource dataSource, string schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var columns = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.table_name, c.column_name, c.data_type, c.character_maximum_length, c.is_nullable = 'YES'
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema = $1 AND t.table_type = 'BASE TABLE'
            ORDER BY c.table_name, c.ordinal_position
            """, connection))
        {
            cmd.Parameters.Add(new() { Value = schema });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var list))
                {
                    columns[table] = list = [];
                }

                var sqlType = reader.GetString(2);
                list.Add(new ColumnInfo(reader.GetString(1), Map(sqlType), sqlType, reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetBoolean(4)));
            }
        }

        var primaryKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var foreignKeys = new List<ForeignKey>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT con.contype, con.conname, cl.relname, a.attname, pcl.relname, pa.attname
            FROM pg_constraint con
            JOIN pg_class cl ON cl.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = cl.relnamespace
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = con.conkey[1]
            LEFT JOIN pg_class pcl ON pcl.oid = con.confrelid
            LEFT JOIN pg_namespace pn ON pn.oid = pcl.relnamespace
            LEFT JOIN pg_attribute pa ON pa.attrelid = con.confrelid AND pa.attnum = con.confkey[1]
            WHERE n.nspname = $1 AND con.contype IN ('p', 'f') AND cardinality(con.conkey) = 1
              AND (con.contype = 'p' OR pn.nspname = $1)
            ORDER BY con.conname
            """, connection))
        {
            cmd.Parameters.Add(new() { Value = schema });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetChar(0) == 'p')
                {
                    primaryKeys[reader.GetString(2)] = reader.GetString(3);
                }
                else
                {
                    foreignKeys.Add(new ForeignKey(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                }
            }
        }

        var tables = columns.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new TableInfo(kv.Key, primaryKeys.GetValueOrDefault(kv.Key), kv.Value))
            .ToList();
        return new SourceCatalog(schema, tables, foreignKeys);
    }

    public static LogicalType Map(string sqlType) => sqlType switch
    {
        "text" => LogicalType.LongText,
        "character varying" or "character" => LogicalType.Text,
        "bigint" or "integer" or "smallint" => LogicalType.WholeNumber,
        "numeric" or "real" or "double precision" => LogicalType.ExactNumeric,
        "date" => LogicalType.Date,
        _ when sqlType.StartsWith("timestamp", StringComparison.Ordinal) => LogicalType.Timestamp,
        "boolean" => LogicalType.Boolean,
        "bytea" => LogicalType.Binary,
        _ => LogicalType.Other,
    };
}

/// <summary>
/// SQL for matching identifiers. One place, because the index advisor must propose indexes on exactly these
/// expressions: PostgreSQL only uses an expression index when the query repeats the indexed expression.
/// </summary>
public static class MatchSql
{
    public static string Ident(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string Table(string schema, string table) => $"{Ident(schema)}.{Ident(table)}";

    /// <summary>The indexable expression and the predicate for a key column.</summary>
    public static (string Expression, string Predicate, bool Trigram) For(KeyColumn key, DiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        var col = Ident(key.Column);
        if (!options.Normalise)
        {
            return key.Mode == MatchMode.FreeText
                ? (col, $"strpos({col}::text, $1) > 0", true)
                : ($"{col}::text", $"{col}::text = $1", false);
        }

        return (key.Mode, key.Kind) switch
        {
            (MatchMode.Exact, IdentifierKind.Phone) => Eq($"right(regexp_replace({col}, '\\D', '', 'g'), 10)"),
            (MatchMode.Exact, IdentifierKind.Email) => Eq($"lower(btrim({col}))"),
            // TCKNs are stored canonical; comparing the bare column keeps the application's own index usable.
            (MatchMode.Exact, IdentifierKind.NationalId) when key.Info.Type == LogicalType.WholeNumber => (col, $"{col} = $1::bigint", false),
            (MatchMode.Exact, IdentifierKind.NationalId) => Eq(col),
            (MatchMode.FreeText, IdentifierKind.Phone) => Like($"regexp_replace({col}, '\\D', '', 'g')"),
            (MatchMode.FreeText, IdentifierKind.Email) => Like($"lower({col})"),
            _ => Like(col),
        };

        static (string, string, bool) Eq(string e) => (e, $"{e} = $1", false);
        static (string, string, bool) Like(string e) => (e, $"{e} LIKE '%' || $1 || '%' ESCAPE '\\'", true);
    }

    /// <summary>LIKE-escapes a value used inside a pattern ('_' is common in e-mail addresses).</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>
/// <see cref="ISourceReader"/> on PostgreSQL. Every value is a bound parameter; identifiers come from the catalog and are
/// quoted. It can run on its own pooled connections (discovery as the read-only finder) or on one connection inside an
/// open transaction (the executor's drift check, which must see exactly what it is about to change).
/// </summary>
public sealed class PostgresSourceReader : ISourceReader
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly NpgsqlConnection? _connection;
    private readonly NpgsqlTransaction? _transaction;
    private readonly SourceCatalog _catalog;

    public PostgresSourceReader(NpgsqlDataSource dataSource, SourceCatalog catalog)
    {
        _dataSource = dataSource;
        _catalog = catalog;
    }

    public PostgresSourceReader(NpgsqlConnection connection, NpgsqlTransaction transaction, SourceCatalog catalog)
    {
        _connection = connection;
        _transaction = transaction;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<SourceHit>> FindAsync(KeyColumn column, string canonical, DiscoveryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(options);
        var (_, predicate, _) = MatchSql.For(column, options);
        object parameter = canonical;
        if (options.Normalise && column.Kind == IdentifierKind.NationalId && column.Info.Type == LogicalType.WholeNumber)
        {
            if (!long.TryParse(canonical, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return [];
            }

            parameter = number.ToString(CultureInfo.InvariantCulture);
        }
        else if (options.Normalise && column.Mode == MatchMode.FreeText)
        {
            parameter = MatchSql.EscapeLike(canonical);
        }

        var sql = $"SELECT {MatchSql.Ident(column.PrimaryKey)}::text, {MatchSql.Ident(column.Column)}::text FROM {MatchSql.Table(_catalog.Schema, column.Table)} WHERE {predicate}";
        var hits = new List<SourceHit>();
        await RunAsync(sql, [parameter], async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new SourceHit(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }, cancellationToken);
        return hits;
    }

    public async Task<IReadOnlyList<(string ChildKey, string ParentKey)>> ChildrenAsync(ForeignKey fk, string childPrimaryKey, IReadOnlyCollection<string> parentKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fk);
        ArgumentNullException.ThrowIfNull(parentKeys);
        if (parentKeys.Count == 0)
        {
            return [];
        }

        var childColumn = _catalog.Table(fk.ChildTable)?.Column(fk.ChildColumn) ?? throw new InvalidOperationException($"Unknown column {fk.ChildTable}.{fk.ChildColumn}.");
        var sql = $"SELECT {MatchSql.Ident(childPrimaryKey)}::text, {MatchSql.Ident(fk.ChildColumn)}::text FROM {MatchSql.Table(_catalog.Schema, fk.ChildTable)} " +
                  $"WHERE {MatchSql.Ident(fk.ChildColumn)} = ANY(CAST($1 AS {childColumn.SqlType}[]))";
        var children = new List<(string, string)>();
        await RunAsync(sql, [parentKeys.ToArray()], async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                children.Add((reader.GetString(0), reader.GetString(1)));
            }
        }, cancellationToken);
        return children;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> FetchAsync(TableInfo table, IReadOnlyCollection<string> keys, IReadOnlyCollection<string> columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(columns);
        var result = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        var pk = table.Column(table.PrimaryKey!)!;
        var wanted = columns.Select(c => table.Column(c)).OfType<ColumnInfo>().ToList();
        var select = string.Join(", ", wanted.Select(c => MatchSql.Ident(c.Name)).Prepend($"{MatchSql.Ident(pk.Name)}::text"));
        var sql = $"SELECT {select} FROM {MatchSql.Table(_catalog.Schema, table.Name)} WHERE {MatchSql.Ident(pk.Name)} = ANY(CAST($1 AS {pk.SqlType}[]))";
        await RunAsync(sql, [keys.ToArray()], async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < wanted.Count; i++)
                {
                    row[wanted[i].Name] = reader.IsDBNull(i + 1) ? null : ValueText.Format(wanted[i].Type switch
                    {
                        LogicalType.Date => reader.GetFieldValue<DateOnly>(i + 1),
                        _ => reader.GetValue(i + 1),
                    });
                }

                result[reader.GetString(0)] = row;
            }
        }, cancellationToken);
        return result;
    }

    private async Task RunAsync(string sql, object[] parameters, Func<NpgsqlDataReader, Task> read, CancellationToken cancellationToken)
    {
        NpgsqlConnection? owned = null;
        try
        {
            var connection = _connection ?? (owned = await _dataSource!.OpenConnectionAsync(cancellationToken));
            await using var cmd = new NpgsqlCommand(sql, connection, _transaction);
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = p });
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await read(reader);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }
}
