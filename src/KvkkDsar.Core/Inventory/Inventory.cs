using System.Text.Json;
using System.Text.Json.Serialization;
using KvkkDsar.Identifiers;

namespace KvkkDsar.Inventory;

/// <summary>One personal-data column as classified by kvkk-data-inventory (<c>kvkk classify</c> output).</summary>
public sealed record InventoryColumn(
    string Table,
    string Column,
    string Type,
    string Category,
    string CategoryName,
    bool Special,
    double Confidence,
    string Linkage);

/// <summary>
/// The personal-data inventory: the <c>classification.json</c> that kvkk-data-inventory produces for a schema. It is
/// the map that says where a person's identifiers can be (search keys) and which columns hold personal data (what to
/// export, clear or anonymise). Reading that file rather than re-detecting keeps one source of truth: if the inventory
/// is wrong, both the VERBİS register and the DSAR answers are wrong in the same, reviewable way.
/// </summary>
public sealed record DataInventory(string Schema, IReadOnlyList<InventoryColumn> Columns)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DataInventory Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<ClassificationFile>(json, Json)
            ?? throw new FormatException("Empty inventory file.");
        if (doc.Columns is null || doc.Columns.Count == 0)
        {
            throw new FormatException("The inventory has no classified columns; is this a kvkk-data-inventory classification.json?");
        }

        return new DataInventory(doc.Schema ?? "public", doc.Columns);
    }

    public static DataInventory Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Which inventory types are search keys. Names, addresses and birth dates are not: they are not unique.</summary>
    public static IdentifierKind? KeyKind(string type) => type switch
    {
        "NationalId" => IdentifierKind.NationalId,
        "Email" => IdentifierKind.Email,
        "Phone" => IdentifierKind.Phone,
        _ => null,
    };

    public IEnumerable<InventoryColumn> ColumnsOf(string table) =>
        Columns.Where(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> Tables() => [.. Columns.Select(c => c.Table).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];

    private sealed record ClassificationFile(string? Schema, List<InventoryColumn>? Columns);
}

public enum RowAction
{
    Erase,
    Anonymise,
}

/// <summary>How one column is changed when a row is anonymised, kept under retention, or holds a mention.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ColumnTreatment>))]
public enum ColumnTreatment
{
    /// <summary>Set to NULL; a NOT NULL column gets <see cref="Redact"/> instead.</summary>
    Null,

    /// <summary>Replace with a fixed placeholder of the column's type ('[anonim]', 0, 1900-01-01).</summary>
    Redact,

    /// <summary>Keep only the year (birth date → 1 January of that year): the value stays useful for statistics.</summary>
    TruncateToYear,

    /// <summary>Free text: cut out every occurrence of the subject's identifiers, keep the rest.</summary>
    RedactIdentifiers,

    Keep,
}

public sealed record RetentionRule(int Years, string Anchor, string Basis);

public sealed record TableRule
{
    [JsonConverter(typeof(JsonStringEnumConverter<RowAction>))]
    public RowAction Action { get; init; } = RowAction.Erase;

    public RetentionRule? Retention { get; init; }

    /// <summary>Columns no longer needed for the retention purpose: cleared even while the row is retained.</summary>
    public IReadOnlyList<string> ClearWhileRetained { get; init; } = [];

    /// <summary>Per-column treatment overrides for anonymisation; other personal-data columns get Null.</summary>
    public IReadOnlyDictionary<string, ColumnTreatment> Anonymise { get; init; } = new Dictionary<string, ColumnTreatment>();

    public string Purpose { get; init; } = "";

    public string LegalBasis { get; init; } = "";

    public IReadOnlyList<string> Recipients { get; init; } = [];
}

/// <summary>A key column that names someone other than the row's owner (an emergency contact, a relative in a note).</summary>
public sealed record MentionRule
{
    /// <summary>Other columns of the same row that describe the mentioned person and are cleared with it.</summary>
    public IReadOnlyList<string> AlsoClear { get; init; } = [];
}

public sealed record ControllerInfo(string Name, string Address, string ContactEmail, string Kep);

/// <summary>
/// The controller's decisions that the inventory cannot know: what may be erased and what must be kept (retention
/// and legal basis), which key columns are really about someone else, and which inventory findings are false
/// positives. Written and owned by the DPO; versioned with the evidence.
/// </summary>
public sealed record DsarPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public required string Version { get; init; }

    public required ControllerInfo Controller { get; init; }

    /// <summary>"table.column" inventory findings that are not personal data of a subject (e.g. branch phone numbers).</summary>
    public IReadOnlyList<string> ExcludeKeys { get; init; } = [];

    public IReadOnlyDictionary<string, MentionRule> Mentions { get; init; } = new Dictionary<string, MentionRule>();

    public IReadOnlyDictionary<string, TableRule> Tables { get; init; } = new Dictionary<string, TableRule>();

    public TableRule Default { get; init; } = new();

    public static DsarPolicy Parse(string json) =>
        JsonSerializer.Deserialize<DsarPolicy>(json, Json) ?? throw new FormatException("Empty policy file.");

    public static DsarPolicy Load(string path) => Parse(File.ReadAllText(path));

    public TableRule RuleFor(string table) =>
        Tables.TryGetValue(table, out var rule) ? rule : Default;

    public bool IsExcluded(string table, string column) =>
        ExcludeKeys.Contains($"{table}.{column}", StringComparer.OrdinalIgnoreCase);

    public MentionRule? MentionFor(string table, string column) =>
        Mentions.TryGetValue($"{table}.{column}", out var rule) ? rule : null;
}
