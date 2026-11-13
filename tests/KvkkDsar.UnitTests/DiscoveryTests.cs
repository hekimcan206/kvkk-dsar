using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using KvkkDsar.Identifiers;
using KvkkDsar.Synthetic;

namespace KvkkDsar.UnitTests;

public sealed class DiscoveryTests
{
    [Fact]
    public void Search_plan_uses_the_inventory_keys_minus_policy_exclusions()
    {
        var engine = TestData.Engine();
        var keys = engine.Keys.Select(k => $"{k.Table}.{k.Column}:{k.Kind}:{k.Mode}:{k.Role}").Order(StringComparer.Ordinal).ToList();

        keys.Should().Equal(
            "customers.email_address:Email:Exact:Owner",
            "customers.phone_number:Phone:Exact:Owner",
            "hastalar.cep_telefonu:Phone:Exact:Owner",
            "hastalar.eposta:Email:Exact:Owner",
            "hastalar.tc_kimlik_no:NationalId:Exact:Owner",
            "personel.acil_durum_tel:Phone:Exact:Mention",
            "personel.email:Email:Exact:Owner",
            "personel.gsm:Phone:Exact:Owner",
            "personel.tckn:NationalId:Exact:Owner",
            "randevular.notlar:Phone:FreeText:Mention");
    }

    [Fact]
    public async Task Unreachable_tables_are_reported_not_silently_skipped()
    {
        var result = await TestData.Engine().DiscoverAsync("clinic", TestData.Source, TestData.Dataset.People[0].Identifiers(), cancellationToken: TestContext.Current.CancellationToken);

        result.Unreachable.Select(u => u.Table).Should().BeEquivalentTo(["audit_log"], "subeler only had findings the policy rejected");
        result.Warnings.Should().Contain(w => w.StartsWith("subeler.telefon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Full_discovery_matches_the_ground_truth_for_every_person()
    {
        var engine = TestData.Engine();
        var mismatches = new List<string>();
        foreach (var person in TestData.Dataset.People)
        {
            var result = await engine.DiscoverAsync("clinic", TestData.Source, person.Identifiers(), cancellationToken: TestContext.Current.CancellationToken);
            var truth = TestData.Dataset.Truth[person.Index];
            var owned = result.Owned.Select(o => (o.Table, o.Key)).ToHashSet();
            var mentions = result.Mentions.Where(m => !owned.Contains((m.Table, m.Key))).Select(m => (m.Table, m.Key, m.Column)).ToHashSet();

            if (!owned.SetEquals(truth.Owned))
            {
                mismatches.Add($"person {person.Index}: owned missing {string.Join(',', truth.Owned.Except(owned))} extra {string.Join(',', owned.Except(truth.Owned))}");
            }

            if (!mentions.SetEquals(truth.Mentions))
            {
                mismatches.Add($"person {person.Index}: mentions missing {string.Join(',', truth.Mentions.Except(mentions))} extra {string.Join(',', mentions.Except(truth.Mentions))}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Naive_exact_search_misses_spellings_children_and_confuses_mentions_with_ownership()
    {
        var engine = TestData.Engine();
        int truthOwned = 0, naiveTruePositive = 0, naiveFalsePositive = 0;
        foreach (var person in TestData.Dataset.People.Take(300))
        {
            var truth = TestData.Dataset.Truth[person.Index];
            var naive = await engine.DiscoverAsync("clinic", TestData.Source, person.Identifiers(), DiscoveryOptions.Naive, TestContext.Current.CancellationToken);
            var owned = naive.Owned.Select(o => (o.Table, o.Key)).ToHashSet();
            truthOwned += truth.Owned.Count;
            naiveTruePositive += owned.Count(truth.Owned.Contains);
            naiveFalsePositive += owned.Count(o => !truth.Owned.Contains(o));
        }

        ((double)naiveTruePositive / truthOwned).Should().BeLessThan(0.5, "without normalisation and foreign keys most rows are missed");
        naiveFalsePositive.Should().BePositive("an emergency-contact phone makes the naive search claim the employee's whole record");
    }

    [Fact]
    public async Task Foreign_keys_bring_in_child_rows_with_their_path()
    {
        var person = TestData.Dataset.People.First(p => TestData.Dataset.Truth[p.Index].Owned.Any(o => o.Table == "tanilar"));
        var result = await TestData.Engine().DiscoverAsync("clinic", TestData.Source, person.Identifiers(), cancellationToken: TestContext.Current.CancellationToken);

        var diagnosis = result.Owned.First(o => o.Table == "tanilar");
        diagnosis.Via.Should().Be("fk:tanilar_hasta_fk");
        diagnosis.ParentTable.Should().Be("hastalar");
        result.Owned.Should().Contain(o => o.Table == "hastalar" && o.Key == diagnosis.ParentKey && o.Via == "tc_kimlik_no");
    }

    [Fact]
    public async Task Fingerprint_is_order_independent_and_changes_with_the_rows()
    {
        var person = TestData.Dataset.People.First(p => TestData.Dataset.Truth[p.Index].Owned.Count > 3);
        var result = await TestData.Engine().DiscoverAsync("clinic", TestData.Source, person.Identifiers(), cancellationToken: TestContext.Current.CancellationToken);

        var shuffled = result with { Owned = [.. result.Owned.Reverse()] };
        shuffled.Fingerprint().Should().Be(result.Fingerprint());
        (result with { Owned = result.Owned.Skip(1).ToList() }).Fingerprint().Should().NotBe(result.Fingerprint());
    }

    [Fact]
    public async Task Discovery_works_with_any_single_identifier()
    {
        var person = TestData.Dataset.People.First(p => TestData.Dataset.Truth[p.Index].Owned.Any(o => o.Table == "customers"));
        var byPhoneOnly = new SubjectIdentifiers { Phone = person.Phone, FirstName = person.FirstName, LastName = person.LastName };
        var result = await TestData.Engine().DiscoverAsync("clinic", TestData.Source, byPhoneOnly, cancellationToken: TestContext.Current.CancellationToken);

        result.Owned.Should().Contain(o => o.Table == "customers");
        result.SearchedColumns.Should().OnlyContain(c => c.Contains("Phone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cycles_in_foreign_keys_terminate()
    {
        var catalog = new SourceCatalog("s",
            [
                new TableInfo("a", "id", [new ColumnInfo("id", LogicalType.WholeNumber, "bigint", null, false), new ColumnInfo("b_id", LogicalType.WholeNumber, "bigint", null, true), new ColumnInfo("tckn", LogicalType.Text, "character varying", 11, true)]),
                new TableInfo("b", "id", [new ColumnInfo("id", LogicalType.WholeNumber, "bigint", null, false), new ColumnInfo("a_id", LogicalType.WholeNumber, "bigint", null, true)]),
            ],
            [new ForeignKey("a_b", "a", "b_id", "b", "id"), new ForeignKey("b_a", "b", "a_id", "a", "id")]);
        var source = new InMemorySource(catalog);
        source.Add("a", new() { ["id"] = 1L, ["b_id"] = 1L, ["tckn"] = "10000000146" });
        source.Add("b", new() { ["id"] = 1L, ["a_id"] = 1L });
        var key = new KeyColumn("a", "tckn", IdentifierKind.NationalId, KeyRole.Owner, MatchMode.Exact, catalog.Tables[0].Columns[2], "id");
        var engine = new DiscoveryEngine(catalog, [key], [], []);

        var result = await engine.DiscoverAsync("s", source, new SubjectIdentifiers { NationalId = "10000000146" }, cancellationToken: TestContext.Current.CancellationToken);

        result.Owned.Select(o => $"{o.Table}#{o.Key}").Should().BeEquivalentTo(["a#1", "b#1"]);
        catalog.TablesInCycles().Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Topological_order_puts_parents_first()
    {
        var order = TestData.Catalog.TopologicalOrder().ToList();
        order.IndexOf("hastalar").Should().BeLessThan(order.IndexOf("tanilar"));
        order.IndexOf("personel").Should().BeLessThan(order.IndexOf("personel_izinleri"));
        TestData.Catalog.TablesInCycles().Should().BeEmpty();
    }

    [Fact]
    public void Synthetic_dataset_is_deterministic_for_a_seed()
    {
        var a = ClinicDataset.Generate(50, seed: 99);
        var b = ClinicDataset.Generate(50, seed: 99);
        a.People.Select(p => p.Tckn).Should().Equal(b.People.Select(p => p.Tckn));
        a.Tables["randevular"].Rows.Count.Should().Be(b.Tables["randevular"].Rows.Count);
        a.People.Should().OnlyContain(p => Tckn.IsValid(p.Tckn));
    }
}
