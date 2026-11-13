using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KvkkDsar.Catalog;
using KvkkDsar.Identifiers;
using KvkkDsar.Inventory;

namespace KvkkDsar.Discovery;

/// <summary>A row that is the subject's data: found by an owner key, or a descendant of such a row through a foreign key.</summary>
public sealed record OwnedRow(string Table, string Key, string Via, string? ParentTable, string? ParentKey);

/// <summary>An identifier of the subject inside someone else's row.</summary>
public sealed record MentionHit(string Table, string Key, string Column, IdentifierKind Kind);

/// <summary>
/// The result of one discovery run: row addresses and how each was found, never values. Values are read again when
/// they are needed (the access export, the executor), so the workflow store never becomes a copy of the data.
/// </summary>
public sealed record DiscoveryResult(
    string Source,
    IReadOnlyList<OwnedRow> Owned,
    IReadOnlyList<MentionHit> Mentions,
    IReadOnlyList<string> SearchedColumns,
    IReadOnlyList<UnreachableTable> Unreachable,
    IReadOnlyList<string> Warnings,
    int Queries,
    double ElapsedMs)
{
    /// <summary>Order-independent digest of what was found; equal fingerprints mean the same rows and mentions.</summary>
    public string Fingerprint()
    {
        var lines = Owned.Select(o => $"O|{o.Table}|{o.Key}")
            .Concat(Mentions.Select(m => $"M|{m.Table}|{m.Key}|{m.Column}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    public IReadOnlyDictionary<string, int> OwnedCountByTable() =>
        Owned.GroupBy(o => o.Table).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>A row hit returned by the source: its primary key and the raw value of the searched column.</summary>
public sealed record SourceHit(string Key, string? Value);

/// <summary>The three questions discovery asks a source database. PostgreSQL and in-memory implementations must agree.</summary>
public interface ISourceReader
{
    /// <summary>Rows whose key column matches the identifier (exact: canonical equality; free text: a superset prefilter).</summary>
    Task<IReadOnlyList<SourceHit>> FindAsync(KeyColumn column, string canonical, DiscoveryOptions options, CancellationToken cancellationToken);

    /// <summary>Primary keys of child rows whose foreign key points at one of the parent keys.</summary>
    Task<IReadOnlyList<(string ChildKey, string ParentKey)>> ChildrenAsync(ForeignKey fk, string childPrimaryKey, IReadOnlyCollection<string> parentKeys, CancellationToken cancellationToken);

    /// <summary>Column values (as invariant text) of the given rows.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> FetchAsync(TableInfo table, IReadOnlyCollection<string> keys, IReadOnlyCollection<string> columns, CancellationToken cancellationToken);
}

/// <summary>Switches used to compare against a naive search (benchmark ablation); production uses the defaults.</summary>
public sealed record DiscoveryOptions
{
    /// <summary>Compare canonical forms (phone digits, lower-case e-mail) instead of raw stored strings.</summary>
    public bool Normalise { get; init; } = true;

    /// <summary>Include rows that reference the subject's rows through declared foreign keys.</summary>
    public bool FollowForeignKeys { get; init; } = true;

    /// <summary>Treat mention columns as mentions (not row ownership) and confirm free-text hits with digit boundaries.</summary>
    public bool RespectMentions { get; init; } = true;

    public static DiscoveryOptions Full { get; } = new();

    public static DiscoveryOptions Naive { get; } = new() { Normalise = false, FollowForeignKeys = false, RespectMentions = false };
}

/// <summary>
/// Finds everything a source holds about one subject: search every inventory key column the subject gave an
/// identifier for, then walk declared foreign keys from the owned rows (appointments, diagnoses and invoices carry a
/// patient id, not a TCKN). Breadth-first with a visited set, so cycles and diamonds terminate.
/// </summary>
public sealed class DiscoveryEngine(SourceCatalog catalog, IReadOnlyList<KeyColumn> keys, IReadOnlyList<UnreachableTable> unreachable, IReadOnlyList<string> warnings)
{
    public SourceCatalog Catalog { get; } = catalog;

    public IReadOnlyList<KeyColumn> Keys { get; } = keys;

    public static DiscoveryEngine Create(DataInventory inventory, DsarPolicy policy, SourceCatalog catalog)
    {
        var (keys, unreachable, warnings) = SearchPlan.Resolve(inventory, policy, catalog);
        return new DiscoveryEngine(catalog, keys, unreachable, warnings);
    }

    public async Task<DiscoveryResult> DiscoverAsync(string source, ISourceReader reader, SubjectIdentifiers subject, DiscoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(subject);
        options ??= DiscoveryOptions.Full;
        var started = Stopwatch.GetTimestamp();
        var queries = 0;
        var owned = new Dictionary<(string Table, string Key), OwnedRow>();
        var mentions = new Dictionary<(string, string, string), MentionHit>();
        var searched = new List<string>();
        var identifiers = subject.Searchable().ToList();

        foreach (var key in Keys)
        {
            foreach (var (kind, value) in identifiers.Where(i => i.Kind == key.Kind))
            {
                searched.Add($"{key.Table}.{key.Column} ({key.Kind}, {key.Mode}, {key.Role})");
                var hits = await reader.FindAsync(key, value, options, cancellationToken);
                queries++;
                foreach (var hit in hits)
                {
                    // Free-text hits from the SQL prefilter are confirmed with digit/word boundaries here.
                    if (key.Mode == MatchMode.FreeText && options.RespectMentions && !FreeTextMatcher.Contains(hit.Value, kind, value))
                    {
                        continue;
                    }

                    if (key.Role == KeyRole.Mention && options.RespectMentions)
                    {
                        mentions.TryAdd((key.Table, hit.Key, key.Column), new MentionHit(key.Table, hit.Key, key.Column, kind));
                    }
                    else
                    {
                        owned.TryAdd((key.Table, hit.Key), new OwnedRow(key.Table, hit.Key, key.Column, null, null));
                    }
                }
            }
        }

        if (options.FollowForeignKeys)
        {
            var frontier = owned.Values.GroupBy(o => o.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(o => o.Key).ToList(), StringComparer.OrdinalIgnoreCase);
            while (frontier.Count > 0)
            {
                var next = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (table, parentKeys) in frontier)
                {
                    foreach (var fk in Catalog.ChildrenOf(table))
                    {
                        var child = Catalog.Table(fk.ChildTable);
                        var parent = Catalog.Table(fk.ParentTable);
                        if (child?.PrimaryKey is null || parent?.PrimaryKey is null)
                        {
                            continue;
                        }

                        var referenced = parentKeys;
                        if (!string.Equals(fk.ParentColumn, parent.PrimaryKey, StringComparison.OrdinalIgnoreCase))
                        {
                            // The foreign key references a unique column, not the primary key: translate.
                            var values = await reader.FetchAsync(parent, parentKeys, [fk.ParentColumn], cancellationToken);
                            queries++;
                            referenced = [.. values.Values.Select(v => v[fk.ParentColumn]).OfType<string>()];
                        }

                        var children = await reader.ChildrenAsync(fk, child.PrimaryKey, referenced, cancellationToken);
                        queries++;
                        foreach (var (childKey, parentKey) in children)
                        {
                            if (owned.TryAdd((child.Name, childKey), new OwnedRow(child.Name, childKey, $"fk:{fk.Name}", table, parentKey)))
                            {
                                if (!next.TryGetValue(child.Name, out var list))
                                {
                                    next[child.Name] = list = [];
                                }

                                list.Add(childKey);
                            }
                        }
                    }
                }

                frontier = next;
            }
        }

        var ownedList = owned.Values.OrderBy(o => o.Table, StringComparer.Ordinal).ThenBy(o => o.Key, KeyComparer.Instance).ToList();
        var mentionList = mentions.Values.OrderBy(m => m.Table, StringComparer.Ordinal).ThenBy(m => m.Key, KeyComparer.Instance).ThenBy(m => m.Column, StringComparer.Ordinal).ToList();
        return new DiscoveryResult(source, ownedList, mentionList, searched, unreachable, warnings, queries,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

/// <summary>Orders keys numerically when both are numbers, so "10" sorts after "9" in reports.</summary>
public sealed class KeyComparer : IComparer<string>
{
    public static KeyComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (long.TryParse(x, out var a) && long.TryParse(y, out var b))
        {
            return a.CompareTo(b);
        }

        return string.CompareOrdinal(x, y);
    }
}
