using System.Globalization;
using KvkkDsar.Catalog;
using KvkkDsar.Identifiers;

namespace KvkkDsar.Discovery;

/// <summary>One canonical text form for values, shared by every source so fingerprints and exports agree.</summary>
public static class ValueText
{
    public static string? Format(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        byte[] b => $"<{b.Length} bytes>",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>The date part of a stored anchor value (date or timestamp text), for retention arithmetic.</summary>
    public static DateOnly? AsDate(string? text) =>
        text is { Length: >= 10 } && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// The reference implementation of <see cref="ISourceReader"/> over in-memory rows. Unit tests use it, and the
/// integration tests check that PostgreSQL returns exactly the same discovery for the same data.
/// </summary>
public sealed class InMemorySource(SourceCatalog catalog) : ISourceReader
{
    private readonly Dictionary<string, List<Dictionary<string, object?>>> _rows = new(StringComparer.OrdinalIgnoreCase);

    public SourceCatalog Catalog { get; } = catalog;

    public void Add(string table, Dictionary<string, object?> row)
    {
        if (!_rows.TryGetValue(table, out var list))
        {
            _rows[table] = list = [];
        }

        list.Add(new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<Dictionary<string, object?>> Rows(string table) =>
        _rows.TryGetValue(table, out var list) ? list : [];

    public Task<IReadOnlyList<SourceHit>> FindAsync(KeyColumn column, string canonical, DiscoveryOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(options);
        var hits = Rows(column.Table)
            .Where(r => Matches(column, ValueText.Format(r.GetValueOrDefault(column.Column)), canonical, options))
            .Select(r => new SourceHit(ValueText.Format(r[column.PrimaryKey])!, ValueText.Format(r.GetValueOrDefault(column.Column))))
            .ToList();
        return Task.FromResult<IReadOnlyList<SourceHit>>(hits);
    }

    public Task<IReadOnlyList<(string ChildKey, string ParentKey)>> ChildrenAsync(ForeignKey fk, string childPrimaryKey, IReadOnlyCollection<string> parentKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fk);
        var wanted = parentKeys.ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<(string, string)> children = [.. Rows(fk.ChildTable)
            .Select(r => (Child: ValueText.Format(r[childPrimaryKey])!, Parent: ValueText.Format(r.GetValueOrDefault(fk.ChildColumn))))
            .Where(x => x.Parent is not null && wanted.Contains(x.Parent))
            .Select(x => (x.Child, x.Parent!))];
        return Task.FromResult(children);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> FetchAsync(TableInfo table, IReadOnlyCollection<string> keys, IReadOnlyCollection<string> columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> result = Rows(table.Name)
            .Where(r => wanted.Contains(ValueText.Format(r[table.PrimaryKey!])!))
            .ToDictionary(
                r => ValueText.Format(r[table.PrimaryKey!])!,
                r => (IReadOnlyDictionary<string, string?>)columns.ToDictionary(c => c, c => ValueText.Format(r.GetValueOrDefault(c)), StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);
        return Task.FromResult(result);
    }

    /// <summary>Exactly the semantics of the SQL in <c>PostgresSourceReader</c>; keep the two in step.</summary>
    public static bool Matches(KeyColumn column, string? stored, string canonical, DiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(options);
        if (stored is null)
        {
            return false;
        }

        if (!options.Normalise)
        {
            return column.Mode == MatchMode.FreeText
                ? stored.Contains(canonical, StringComparison.Ordinal)
                : string.Equals(stored, canonical, StringComparison.Ordinal);
        }

        return (column.Mode, column.Kind) switch
        {
            (MatchMode.Exact, IdentifierKind.Phone) => string.Equals(PhoneNumbers.StoredForm(stored), canonical, StringComparison.Ordinal),
            (MatchMode.Exact, IdentifierKind.Email) => string.Equals(stored.Trim(' ').ToLowerInvariant(), canonical, StringComparison.Ordinal),
            (MatchMode.Exact, IdentifierKind.NationalId) => string.Equals(stored, canonical, StringComparison.Ordinal),
            (MatchMode.FreeText, IdentifierKind.Phone) => PhoneNumbers.Digits(stored).Contains(canonical, StringComparison.Ordinal),
            (MatchMode.FreeText, IdentifierKind.Email) => stored.ToLowerInvariant().Contains(canonical, StringComparison.Ordinal),
            (MatchMode.FreeText, _) => stored.Contains(canonical, StringComparison.Ordinal),
            _ => false,
        };
    }
}
