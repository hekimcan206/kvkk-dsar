using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using KvkkDsar.Inventory;

namespace KvkkDsar.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<PlannedActionKind>))]
public enum PlannedActionKind
{
    /// <summary>DELETE the row.</summary>
    Erase,

    /// <summary>Keep the row, clear or generalise every personal-data column.</summary>
    Anonymise,

    /// <summary>Keep the row under a legal retention obligation; processing is restricted, unneeded columns are cleared.</summary>
    Retain,

    /// <summary>Someone else's row: clear only the part that is about the subject.</summary>
    RedactMention,
}

public sealed record ColumnChange(string Column, ColumnTreatment Treatment);

public sealed record PlannedAction(
    string Table,
    string Key,
    PlannedActionKind Kind,
    IReadOnlyList<ColumnChange> Changes,
    string Reason,
    string? LegalBasis,
    DateOnly? RetainUntil,
    IReadOnlyList<string> Categories);

/// <summary>
/// What will be done, row by row, and why. Its SHA-256 is what the approver signs off: the executor refuses to run a
/// plan whose hash is not the approved one, and re-checks the source for drift before touching anything.
/// </summary>
public sealed record ErasurePlan(
    string Source,
    string PolicyVersion,
    string DiscoveryFingerprint,
    DateOnly PlannedOn,
    IReadOnlyList<PlannedAction> Actions,
    IReadOnlyList<string> DeleteOrder,
    IReadOnlyList<string> NotifyRecipients)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)));

    public IReadOnlyDictionary<PlannedActionKind, int> Counts() =>
        Actions.GroupBy(a => a.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>
/// Turns a discovery into an erasure plan under the controller's policy (KVKK Art. 7 and the Regulation on Erasure,
/// Destruction or Anonymisation). Three rules do the hard part:
/// <list type="number">
/// <item>Retention first. A row under a legal retention period is kept (with its release date), and only the columns
/// the retention purpose does not need are cleared. The response tells the subject which part was refused and why.</item>
/// <item>Referential integrity. A row cannot be deleted while a kept row references it; it is anonymised instead.
/// Decisions are made children first (reverse topological order), so a parent always sees its children's outcome.</item>
/// <item>Mentions are not ownership. An emergency-contact phone in an employee's record is the subject's data, the
/// record is not: only the mention is cleared.</item>
/// </list>
/// </summary>
public static class ErasurePlanner
{
    public const string RedactedText = "[anonim]";

    /// <summary>The columns discovery must fetch so the planner can evaluate retention.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AnchorColumns(DsarPolicy policy, DiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(discovery);
        return discovery.Owned.Select(o => o.Table).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (Table: t, Rule: policy.RuleFor(t)))
            .Where(x => x.Rule.Retention is not null)
            .ToDictionary(x => x.Table, x => (IReadOnlyList<string>)[x.Rule.Retention!.Anchor], StringComparer.OrdinalIgnoreCase);
    }

    public static ErasurePlan Plan(
        DiscoveryResult discovery,
        SourceCatalog catalog,
        DataInventory inventory,
        DsarPolicy policy,
        IReadOnlyDictionary<(string Table, string Key), IReadOnlyDictionary<string, string?>> anchorValues,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(anchorValues);

        var order = catalog.TopologicalOrder();
        var rank = order.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i, StringComparer.OrdinalIgnoreCase);
        var cyclic = catalog.TablesInCycles();
        var decisions = new Dictionary<(string Table, string Key), PlannedAction>();
        var childrenOf = discovery.Owned
            .Where(o => o.ParentTable is not null)
            .GroupBy(o => (o.ParentTable!, o.ParentKey!))
            .ToDictionary(g => g.Key, g => g.Select(o => (o.Table, o.Key)).ToList());

        // Children first: a parent's decision depends on whether all of its children go away.
        foreach (var row in discovery.Owned.OrderByDescending(o => rank.GetValueOrDefault(o.Table)).ThenBy(o => o.Key, KeyComparer.Instance))
        {
            if (decisions.ContainsKey((row.Table, row.Key)))
            {
                continue;
            }

            var table = catalog.Table(row.Table) ?? throw new InvalidOperationException($"Table {row.Table} vanished from the catalog.");
            var rule = policy.RuleFor(row.Table);
            var categories = inventory.ColumnsOf(row.Table).Select(c => c.CategoryName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var action = Decide(row, table, rule, inventory, anchorValues, today, categories);

            if (action.Kind == PlannedActionKind.Erase)
            {
                var blockers = childrenOf.GetValueOrDefault((row.Table, row.Key), [])
                    .Select(c => decisions.GetValueOrDefault(c))
                    .Where(d => d is not null && d.Kind != PlannedActionKind.Erase)
                    .ToList();
                if (blockers.Count > 0 || cyclic.Contains(row.Table))
                {
                    var why = blockers.Count > 0
                        ? $"kept rows reference it ({string.Join(", ", blockers.Take(3).Select(b => $"{b!.Table}#{b.Key}"))}{(blockers.Count > 3 ? ", …" : "")}); anonymised instead of deleted"
                        : "table is part of a foreign-key cycle; anonymised instead of deleted";
                    action = action with
                    {
                        Kind = PlannedActionKind.Anonymise,
                        Changes = AnonymiseChanges(table, rule, inventory),
                        Reason = why,
                    };
                }
            }

            decisions[(row.Table, row.Key)] = action;
        }

        foreach (var mention in discovery.Mentions)
        {
            if (decisions.TryGetValue((mention.Table, mention.Key), out var existing))
            {
                // The subject's own row: erased or anonymised anyway, or kept for a legal reason that covers the note too.
                if (existing.Kind is PlannedActionKind.Erase or PlannedActionKind.Anonymise)
                {
                    continue;
                }
            }

            var table = catalog.Table(mention.Table)!;
            var column = table.Column(mention.Column)!;
            var changes = new List<ColumnChange>
            {
                new(mention.Column, column.Type == LogicalType.LongText ? ColumnTreatment.RedactIdentifiers : NullOrRedact(column)),
            };
            foreach (var also in policy.MentionFor(mention.Table, mention.Column)?.AlsoClear ?? [])
            {
                if (table.Column(also) is { } extra)
                {
                    changes.Add(new ColumnChange(extra.Name, NullOrRedact(extra)));
                }
            }

            var mentionAction = new PlannedAction(mention.Table, mention.Key, PlannedActionKind.RedactMention, changes,
                $"the subject's {mention.Kind} appears in another person's record ({mention.Column}); only that part is the subject's data",
                null, null, [.. inventory.ColumnsOf(mention.Table).Where(c => string.Equals(c.Column, mention.Column, StringComparison.OrdinalIgnoreCase)).Select(c => c.CategoryName).Distinct()]);

            if (existing is not null)
            {
                // Retained own row with a mention column: merge the clearing into the retained action.
                decisions[(mention.Table, mention.Key)] = existing with
                {
                    Changes = [.. existing.Changes.Concat(changes).DistinctBy(c => c.Column, StringComparer.OrdinalIgnoreCase)],
                };
                continue;
            }

            decisions[(mention.Table, mention.Key)] = MergeMention(decisions.GetValueOrDefault((mention.Table, mention.Key)), mentionAction);
        }

        var actions = decisions.Values
            .OrderBy(a => rank.GetValueOrDefault(a.Table))
            .ThenBy(a => a.Table, StringComparer.Ordinal)
            .ThenBy(a => a.Key, KeyComparer.Instance)
            .ToList();
        var deleteOrder = actions.Where(a => a.Kind == PlannedActionKind.Erase).Select(a => a.Table).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => rank.GetValueOrDefault(t)).ToList();
        var recipients = actions.Where(a => a.Kind != PlannedActionKind.Retain)
            .SelectMany(a => policy.RuleFor(a.Table).Recipients)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        return new ErasurePlan(discovery.Source, policy.Version, discovery.Fingerprint(), today, actions, deleteOrder, recipients);
    }

    private static PlannedAction MergeMention(PlannedAction? earlier, PlannedAction mention) =>
        earlier is null ? mention : earlier with
        {
            Changes = [.. earlier.Changes.Concat(mention.Changes).DistinctBy(c => c.Column, StringComparer.OrdinalIgnoreCase)],
            Categories = [.. earlier.Categories.Concat(mention.Categories).Distinct(StringComparer.Ordinal)],
        };

    private static PlannedAction Decide(
        OwnedRow row, TableInfo table, TableRule rule, DataInventory inventory,
        IReadOnlyDictionary<(string Table, string Key), IReadOnlyDictionary<string, string?>> anchorValues,
        DateOnly today, IReadOnlyList<string> categories)
    {
        if (rule.Retention is { } retention)
        {
            var anchorText = anchorValues.GetValueOrDefault((row.Table, row.Key))?.GetValueOrDefault(retention.Anchor);
            var anchor = ValueText.AsDate(anchorText);
            if (anchor is null)
            {
                // E.g. an employee with no termination date: the retention period has not even started.
                return new PlannedAction(row.Table, row.Key, PlannedActionKind.Retain, ClearWhileRetained(table, rule),
                    $"retention period has not started ({retention.Anchor} is empty)", retention.Basis, null, categories);
            }

            var until = anchor.Value.AddYears(retention.Years);
            if (until > today)
            {
                return new PlannedAction(row.Table, row.Key, PlannedActionKind.Retain, ClearWhileRetained(table, rule),
                    $"retained for {retention.Years} years from {retention.Anchor} ({anchor.Value:yyyy-MM-dd})", retention.Basis, until, categories);
            }
        }

        return rule.Action == RowAction.Anonymise
            ? new PlannedAction(row.Table, row.Key, PlannedActionKind.Anonymise, AnonymiseChanges(table, rule, inventory),
                "policy anonymises this table (aggregate use continues without the person)", rule.LegalBasis, null, categories)
            : new PlannedAction(row.Table, row.Key, PlannedActionKind.Erase, [],
                rule.Retention is null ? "no retention obligation" : "retention period has ended", rule.LegalBasis, null, categories);
    }

    private static List<ColumnChange> ClearWhileRetained(TableInfo table, TableRule rule) =>
        [.. rule.ClearWhileRetained.Select(c => table.Column(c)).OfType<ColumnInfo>().Select(c => new ColumnChange(c.Name, NullOrRedact(c)))];

    /// <summary>Every personal-data column of the table (per the inventory) except keys that hold the row together.</summary>
    internal static List<ColumnChange> AnonymiseChanges(TableInfo table, TableRule rule, DataInventory inventory)
    {
        var changes = new List<ColumnChange>();
        foreach (var name in inventory.ColumnsOf(table.Name).Select(c => c.Column).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var column = table.Column(name);
            if (column is null || string.Equals(column.Name, table.PrimaryKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var treatment = rule.Anonymise.TryGetValue(column.Name, out var t) ? t : NullOrRedact(column);
            if (treatment == ColumnTreatment.Null && !column.Nullable)
            {
                treatment = ColumnTreatment.Redact;
            }

            if (treatment != ColumnTreatment.Keep)
            {
                changes.Add(new ColumnChange(column.Name, treatment));
            }
        }

        return [.. changes.OrderBy(c => c.Column, StringComparer.Ordinal)];
    }

    private static ColumnTreatment NullOrRedact(ColumnInfo column) => column.Nullable ? ColumnTreatment.Null : ColumnTreatment.Redact;
}
