using KvkkDsar.Identifiers;
using KvkkDsar.Inventory;

namespace KvkkDsar.Catalog;

public enum LogicalType
{
    Text,

    /// <summary>Unbounded text (PostgreSQL <c>text</c>): searched as free text, not by equality.</summary>
    LongText,
    WholeNumber,
    ExactNumeric,
    Date,
    Timestamp,
    Boolean,
    Binary,
    Other,
}

public sealed record ColumnInfo(string Name, LogicalType Type, string SqlType, int? MaxLength, bool Nullable);

public sealed record TableInfo(string Name, string? PrimaryKey, IReadOnlyList<ColumnInfo> Columns)
{
    public ColumnInfo? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A declared single-column foreign key: <c>Child.ChildColumn → Parent.ParentColumn</c>.</summary>
public sealed record ForeignKey(string Name, string ChildTable, string ChildColumn, string ParentTable, string ParentColumn);

/// <summary>What the source database declares: tables, primary keys and foreign keys. Read from the catalog, never typed in.</summary>
public sealed record SourceCatalog(string Schema, IReadOnlyList<TableInfo> Tables, IReadOnlyList<ForeignKey> ForeignKeys)
{
    public TableInfo? Table(string name) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ForeignKey> ChildrenOf(string parent) =>
        ForeignKeys.Where(f => string.Equals(f.ParentTable, parent, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(f.ChildTable, parent, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parents before children (Kahn's algorithm); tables in a foreign-key cycle are appended at the end in name order,
    /// and the planner never deletes across a cycle (it anonymises instead).
    /// </summary>
    public IReadOnlyList<string> TopologicalOrder()
    {
        var names = Tables.Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        var edges = ForeignKeys.Where(f => !string.Equals(f.ChildTable, f.ParentTable, StringComparison.OrdinalIgnoreCase)).ToList();
        var indegree = names.ToDictionary(n => n, n => edges.Count(e => e.ChildTable == n), StringComparer.Ordinal);
        var ready = new SortedSet<string>(names.Where(n => indegree[n] == 0), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            order.Add(next);
            foreach (var e in edges.Where(e => e.ParentTable == next))
            {
                if (indegree.ContainsKey(e.ChildTable) && --indegree[e.ChildTable] == 0)
                {
                    ready.Add(e.ChildTable);
                }
            }
        }

        order.AddRange(names.Where(n => !order.Contains(n)));
        return order;
    }

    public IReadOnlySet<string> TablesInCycles()
    {
        var order = TopologicalOrder();
        var acyclic = new HashSet<string>(StringComparer.Ordinal);
        var edges = ForeignKeys.Where(f => f.ChildTable != f.ParentTable).ToList();
        // Recompute: a table is acyclic if all its parents were emitted before it by Kahn's algorithm proper.
        var indegree = Tables.ToDictionary(t => t.Name, t => edges.Count(e => e.ChildTable == t.Name), StringComparer.Ordinal);
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            acyclic.Add(n);
            foreach (var e in edges.Where(e => e.ParentTable == n))
            {
                if (indegree.ContainsKey(e.ChildTable) && --indegree[e.ChildTable] == 0)
                {
                    queue.Enqueue(e.ChildTable);
                }
            }
        }

        return order.Where(n => !acyclic.Contains(n)).ToHashSet(StringComparer.Ordinal);
    }
}

public enum KeyRole
{
    /// <summary>The identifier belongs to the row's data subject: the row (and its children) is the subject's data.</summary>
    Owner,

    /// <summary>The identifier names someone else in another person's row: only that part is the subject's data.</summary>
    Mention,
}

public enum MatchMode
{
    Exact,
    FreeText,
}

/// <summary>A column discovery searches, with how to compare and what a hit means.</summary>
public sealed record KeyColumn(string Table, string Column, IdentifierKind Kind, KeyRole Role, MatchMode Mode, ColumnInfo Info, string PrimaryKey);

/// <summary>A table that holds personal data per the inventory but that no search key or foreign key reaches.</summary>
public sealed record UnreachableTable(string Table, string Reason);

/// <summary>Resolves the inventory and policy against the live catalog into the concrete search plan.</summary>
public static class SearchPlan
{
    public static (IReadOnlyList<KeyColumn> Keys, IReadOnlyList<UnreachableTable> Unreachable, IReadOnlyList<string> Warnings) Resolve(
        DataInventory inventory, DsarPolicy policy, SourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(catalog);
        var warnings = new List<string>();
        var keys = new List<KeyColumn>();

        foreach (var c in inventory.Columns)
        {
            if (DataInventory.KeyKind(c.Type) is not { } kind)
            {
                continue;
            }

            if (policy.IsExcluded(c.Table, c.Column))
            {
                warnings.Add($"{c.Table}.{c.Column}: inventory says {c.Type}; excluded by policy (not a data subject's identifier).");
                continue;
            }

            var table = catalog.Table(c.Table);
            var column = table?.Column(c.Column);
            if (table is null || column is null)
            {
                warnings.Add($"{c.Table}.{c.Column}: in the inventory but not in the database; the inventory is stale.");
                continue;
            }

            if (table.PrimaryKey is null)
            {
                warnings.Add($"{c.Table}: no single-column primary key; rows cannot be addressed, so it is not searched.");
                continue;
            }

            var mode = column.Type == LogicalType.LongText ? MatchMode.FreeText : MatchMode.Exact;
            var role = mode == MatchMode.FreeText || policy.MentionFor(table.Name, column.Name) is not null ? KeyRole.Mention : KeyRole.Owner;
            keys.Add(new KeyColumn(table.Name, column.Name, kind, role, mode, column, table.PrimaryKey));
        }

        // Static reachability: tables with an owner key, plus everything that references them through foreign keys.
        var reachable = new HashSet<string>(keys.Where(k => k.Role == KeyRole.Owner).Select(k => k.Table), StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<string>(reachable);
        while (frontier.Count > 0)
        {
            foreach (var fk in catalog.ChildrenOf(frontier.Dequeue()))
            {
                if (reachable.Add(fk.ChildTable))
                {
                    frontier.Enqueue(fk.ChildTable);
                }
            }
        }

        var mentionOnly = keys.Where(k => k.Role == KeyRole.Mention).Select(k => k.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreachable = inventory.Tables()
            .Where(t => !reachable.Contains(t) && !mentionOnly.Contains(t))
            // A table whose every finding the policy rejected (e.g. branch phone numbers) holds no personal data.
            .Where(t => !inventory.ColumnsOf(t).All(c => policy.IsExcluded(t, c.Column)))
            .Select(t => new UnreachableTable(t, catalog.Table(t) is null
                ? "listed in the inventory but missing from the database"
                : keys.Count == 0 || inventory.ColumnsOf(t).All(c => DataInventory.KeyKind(c.Type) is null || policy.IsExcluded(t, c.Column))
                    ? "no identifier column (TCKN, e-mail, phone) and no foreign key to a table that has one"
                    : "not reachable"))
            .ToList();

        return (keys, unreachable, warnings);
    }
}
