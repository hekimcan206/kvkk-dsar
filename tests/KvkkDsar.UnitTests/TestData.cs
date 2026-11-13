using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using KvkkDsar.Inventory;
using KvkkDsar.Synthetic;

namespace KvkkDsar.UnitTests;

/// <summary>Shared fixtures: the committed inventory and policy, and one in-memory synthetic clinic.</summary>
internal static class TestData
{
    private static readonly Lazy<ClinicDataset> LazyDataset = new(() => ClinicDataset.Generate(600, seed: 7));
    private static readonly Lazy<InMemorySource> LazySource = new(() => ClinicDatabase.InMemory(Dataset));

    public static DataInventory Inventory { get; } = DataInventory.Load(Path.Combine(AppContext.BaseDirectory, "samples", "clinic.classification.json"));

    public static DsarPolicy Policy { get; } = DsarPolicy.Load(Path.Combine(AppContext.BaseDirectory, "samples", "dsar-policy.json"));

    public static SourceCatalog Catalog { get; } = ClinicDatabase.Catalog();

    public static ClinicDataset Dataset => LazyDataset.Value;

    /// <summary>Read-only by convention: tests that change rows build their own source.</summary>
    public static InMemorySource Source => LazySource.Value;

    public static DiscoveryEngine Engine() => DiscoveryEngine.Create(Inventory, Policy, Catalog);

    public static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    /// <summary>Compares with a golden file; set UPDATE_GOLDEN=1 to rewrite (then review the diff before committing).</summary>
    public static void AssertGolden(string name, string actual)
    {
        var normalized = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(FindRepoRoot(), "tests", "KvkkDsar.UnitTests", "Golden", name);
            File.WriteAllText(source, normalized);
            return;
        }

        var expected = File.ReadAllText(GoldenPath(name)).Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized.Should().Be(expected, $"golden file {name} must match; rerun with UPDATE_GOLDEN=1 only after reviewing the change");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KvkkDsar.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
