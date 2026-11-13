using KvkkDsar.Discovery;
using KvkkDsar.Inventory;
using KvkkDsar.Planning;
using KvkkDsar.Synthetic;

namespace KvkkDsar.UnitTests;

public sealed class PlannerTests
{
    internal static readonly DateOnly Today = new(2026, 11, 13);

    internal static async Task<(DiscoveryResult Discovery, ErasurePlan Plan)> PlanFor(Person person, InMemorySource? source = null, DsarPolicy? policy = null)
    {
        source ??= TestData.Source;
        policy ??= TestData.Policy;
        var engine = DiscoveryEngine.Create(TestData.Inventory, policy, TestData.Catalog);
        var discovery = await engine.DiscoverAsync("clinic", source, person.Identifiers());
        var anchors = new Dictionary<(string, string), IReadOnlyDictionary<string, string?>>();
        foreach (var (table, columns) in ErasurePlanner.AnchorColumns(policy, discovery))
        {
            var keys = discovery.Owned.Where(o => o.Table == table).Select(o => o.Key).ToList();
            foreach (var (key, values) in await source.FetchAsync(TestData.Catalog.Table(table)!, keys, columns, CancellationToken.None))
            {
                anchors[(table, key)] = values;
            }
        }

        return (discovery, ErasurePlanner.Plan(discovery, TestData.Catalog, TestData.Inventory, policy, anchors, Today));
    }

    private static Person Find(Func<Person, GroundTruth, bool> predicate) =>
        TestData.Dataset.People.First(p => predicate(p, TestData.Dataset.Truth[p.Index]));

    [Fact]
    public async Task Portal_account_without_retention_is_erased()
    {
        var person = Find((_, t) => t.Owned.Count == 1 && t.Owned.Single().Table == "customers");
        var (_, plan) = await PlanFor(person);

        plan.Actions.Should().ContainSingle(a => a.Table == "customers").Which.Kind.Should().Be(PlannedActionKind.Erase);
        plan.DeleteOrder.Should().Contain("customers");
        plan.NotifyRecipients.Should().Contain("Payment service provider (synthetic)");
    }

    [Fact]
    public async Task Recent_patient_record_is_retained_with_unneeded_columns_cleared()
    {
        var person = Find((p, t) => t.Owned.Contains(("hastalar", HastaId(p))) && Created(p).Year >= 2015);
        var (_, plan) = await PlanFor(person);

        var patient = plan.Actions.Single(a => a.Table == "hastalar");
        patient.Kind.Should().Be(PlannedActionKind.Retain);
        patient.RetainUntil.Should().Be(DateOnly.FromDateTime(Created(person)).AddYears(20));
        patient.Changes.Should().ContainSingle().Which.Should().Be(new ColumnChange("eposta", ColumnTreatment.Null));
        patient.LegalBasis.Should().Contain("Art. 5(2)(ç)");
    }

    [Fact]
    public async Task Old_patient_with_kept_invoices_is_anonymised_not_deleted()
    {
        var plans = new List<ErasurePlan>();
        foreach (var person in TestData.Dataset.People.Where(p => TestData.Dataset.Truth[p.Index].Owned.Any(o => o.Table == "hastalar") && Created(p).Year < 2006))
        {
            plans.Add((await PlanFor(person)).Plan);
        }

        var downgraded = plans.SelectMany(p => p.Actions).Where(a => a.Table == "hastalar" && a.Kind == PlannedActionKind.Anonymise).ToList();
        downgraded.Should().NotBeEmpty();
        downgraded.Should().OnlyContain(a => a.Reason.Contains("kept rows reference it", StringComparison.Ordinal));
        var changes = downgraded[0].Changes.ToDictionary(c => c.Column, c => c.Treatment);
        changes["tc_kimlik_no"].Should().Be(ColumnTreatment.Redact, "NOT NULL columns get a placeholder");
        changes["dogum_tarihi"].Should().Be(ColumnTreatment.TruncateToYear, "the policy keeps the birth year for statistics");
        changes.Should().NotContainKey("cinsiyet", "the policy keeps gender");
        plans.SelectMany(p => p.Actions).Should().Contain(a => a.Table == "hastalar" && a.Kind == PlannedActionKind.Erase, "some old files have nothing kept under them");
    }

    [Fact]
    public async Task Emergency_contact_is_a_mention_not_ownership()
    {
        var person = Find((_, t) => t.Mentions.Any(m => m.Table == "personel"));
        var mention = TestData.Dataset.Truth[person.Index].Mentions.First(m => m.Table == "personel");
        var (_, plan) = await PlanFor(person);

        var action = plan.Actions.Single(a => a.Table == "personel" && a.Key == mention.Key);
        action.Kind.Should().Be(PlannedActionKind.RedactMention);
        action.Changes.Select(c => c.Column).Should().BeEquivalentTo(["acil_durum_tel", "acil_durum_kisi"]);
    }

    [Fact]
    public async Task Phone_in_someone_elses_note_is_cut_out_of_the_note()
    {
        var person = Find((_, t) => t.Mentions.Any(m => m.Table == "randevular"));
        var mention = TestData.Dataset.Truth[person.Index].Mentions.First(m => m.Table == "randevular");
        var (_, plan) = await PlanFor(person);

        plan.Actions.Single(a => a.Table == "randevular" && a.Key == mention.Key).Changes
            .Should().ContainSingle().Which.Treatment.Should().Be(ColumnTreatment.RedactIdentifiers);
    }

    [Fact]
    public async Task Employee_still_employed_is_retained_because_the_period_has_not_started()
    {
        var person = Find((p, t) => t.Owned.Any(o => o.Table == "personel") && LeftOn(p) is null);
        var (_, plan) = await PlanFor(person);

        var staff = plan.Actions.Single(a => a.Table == "personel" && a.Kind != PlannedActionKind.RedactMention);
        staff.Kind.Should().Be(PlannedActionKind.Retain);
        staff.RetainUntil.Should().BeNull();
        staff.Reason.Should().Contain("has not started");
        staff.Changes.Select(c => c.Column).Should().BeEquivalentTo(["parmak_izi_sablonu", "arac_plakasi"]);
    }

    [Fact]
    public async Task Every_plan_is_referentially_safe_and_covers_every_row_once()
    {
        foreach (var person in TestData.Dataset.People.Take(400))
        {
            var (discovery, plan) = await PlanFor(person);
            var byRow = plan.Actions.ToDictionary(a => (a.Table, a.Key));
            byRow.Count.Should().Be(plan.Actions.Count, "one action per row");

            foreach (var row in discovery.Owned)
            {
                byRow.Should().ContainKey((row.Table, row.Key));
                if (row.ParentTable is not null && byRow[(row.Table, row.Key)].Kind != PlannedActionKind.Erase)
                {
                    byRow[(row.ParentTable, row.ParentKey!)].Kind.Should().NotBe(PlannedActionKind.Erase, "a parent is never deleted under a kept child");
                }
            }

            foreach (var m in discovery.Mentions.Where(m => !discovery.Owned.Any(o => o.Table == m.Table && o.Key == m.Key)))
            {
                byRow[(m.Table, m.Key)].Kind.Should().Be(PlannedActionKind.RedactMention);
            }

            plan.Hash().Should().Be((await PlanFor(person)).Plan.Hash(), "planning is deterministic");
        }
    }

    [Fact]
    public async Task Changing_the_policy_changes_the_plan_hash()
    {
        var person = Find((_, t) => t.Owned.Any(o => o.Table == "customers"));
        var (_, a) = await PlanFor(person);
        var (_, b) = await PlanFor(person, policy: TestData.Policy with { Version = "other" });
        a.Hash().Should().NotBe(b.Hash());
    }

    private static string HastaId(Person p) =>
        TestData.Dataset.Tables["hastalar"].Rows.FirstOrDefault(r => (string)r[1]! == p.Tckn)?[0]?.ToString() ?? "-";

    private static DateTime Created(Person p) =>
        (DateTime)TestData.Dataset.Tables["hastalar"].Rows.First(r => (string)r[1]! == p.Tckn)[12]!;

    private static DateOnly? LeftOn(Person p) =>
        (DateOnly?)TestData.Dataset.Tables["personel"].Rows.First(r => (long)r[1]! == long.Parse(p.Tckn, System.Globalization.CultureInfo.InvariantCulture))[11];
}
