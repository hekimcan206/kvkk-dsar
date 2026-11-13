using System.Net;
using System.Text.Json;
using KvkkDsar.Api.Endpoints;
using KvkkDsar.Api.Persistence;
using KvkkDsar.Evidence;
using KvkkDsar.IntegrationTests.Infrastructure;
using KvkkDsar.Synthetic;
using KvkkDsar.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace KvkkDsar.IntegrationTests;

/// <summary>
/// The service over HTTP against the shared clinic database. Each test works on different synthetic people, so tests
/// that erase data do not interfere.
/// </summary>
public sealed class ApiTests(DsarEnvironment env)
{
    private static readonly string[] ErasureOnly = ["Erasure"];
    private static readonly HashSet<int> Used = [];

    private readonly HttpClient _intake = env.Client("key-intake");
    private readonly HttpClient _verifier = env.Client("key-verifier");
    private readonly HttpClient _dpo = env.Client("key-dpo");
    private readonly HttpClient _legal = env.Client("key-legal");
    private readonly HttpClient _ops = env.Client("key-ops");
    private readonly HttpClient _audit = env.Client("key-audit");

    /// <summary>A person with the wanted shape that no other test has used yet.</summary>
    private Person Next(Func<GroundTruth, bool> shape)
    {
        lock (Used)
        {
            var person = env.Dataset.People.Where(p => !Used.Contains(p.Index) && shape(env.Dataset.Truth[p.Index])).Skip(3).First();
            Used.Add(person.Index);
            return person;
        }
    }

    private static object Body(Person p, RequestType[] types, Channel channel = Channel.WebPortal, string? email = null, DateTimeOffset? receivedAt = null,
        bool withBirthDate = true, string? nationalId = null) => new
        {
            types,
            channel,
            receivedAt,
            channelEvidence = channel is Channel.Kep ? "KEP msg 2026/123 signature verified" : null,
            subject = new
            {
                nationalId = nationalId ?? p.Tckn,
                email = email ?? p.Email,
                phone = ClinicDataset.PhoneSpelling(p.Phone, new Bogus.Randomizer(p.Index)),
                firstName = p.FirstName,
                lastName = p.LastName,
                birthDate = withBirthDate ? p.BirthDate : (DateOnly?)null,
            },
        };

    private async Task<RequestView> CreateAsync(object body) =>
        await DsarEnvironment.ReadAsync<RequestView>(await _intake.PostJson("api/v1/requests", body));

    private async Task<RequestView> VerifyWithOtpAsync(Guid id)
    {
        (await _verifier.PostEmpty($"api/v1/requests/{id}/verification/challenge")).EnsureSuccessStatusCode();
        var inbox = await DsarEnvironment.ReadAsync<JsonElement>(await _verifier.Get($"demo/inbox/{id}"));
        var confirm = await DsarEnvironment.ReadAsync<JsonElement>(await _verifier.PostJson($"api/v1/requests/{id}/verification/confirm", new { code = inbox.GetProperty("code").GetString() }));
        return confirm.GetProperty("request").Deserialize<RequestView>(DsarEnvironment.Json)!;
    }

    private async Task<string> DiscoverAndPlanAsync(Guid id)
    {
        (await _dpo.PostEmpty($"api/v1/requests/{id}/discovery")).EnsureSuccessStatusCode();
        var plan = await DsarEnvironment.ReadAsync<JsonElement>(await _dpo.PostEmpty($"api/v1/requests/{id}/plan"));
        return plan.GetProperty("planHash").GetString()!;
    }

    [Fact]
    public async Task Erasure_end_to_end_with_otp_four_eyes_letter_and_signed_evidence()
    {
        var person = Next(t => t.Owned.Any(o => o.Table == "customers") && t.Owned.Any(o => o.Table == "hastalar"));
        var created = await CreateAsync(Body(person, [RequestType.Erasure]));
        created.Status.Should().Be(RequestStatus.Received);
        created.RequiredAssurance.Should().Be(Verification.Assurance.High);
        created.Subject["NationalId"].Should().MatchRegex(@"^\d\d\*{7}\d\d$");
        created.DueOn.Should().Be(Deadlines.DeadlineCalculator.Compute(created.ReceivedAt, Deadlines.DeadlinePolicy.Strict).DueOn);

        var verified = await VerifyWithOtpAsync(created.Id);
        verified.Status.Should().Be(RequestStatus.Verified);
        verified.Factors.Should().BeEquivalentTo([Verification.VerificationFactor.OtpToContactOnFile, Verification.VerificationFactor.KnowledgeMatch]);

        var hash = await DiscoverAndPlanAsync(created.Id);
        (await _dpo.PostJson($"api/v1/requests/{created.Id}/approve", new { planHash = hash })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the DPO has no approver role");
        var approved = await DsarEnvironment.ReadAsync<RequestView>(await _legal.PostJson($"api/v1/requests/{created.Id}/approve", new { planHash = hash }));
        approved.ApprovedBy.Should().Be("zeynep.legal");

        var executed = await DsarEnvironment.ReadAsync<JsonElement>(await _ops.PostEmpty($"api/v1/requests/{created.Id}/execute"));
        executed.GetProperty("request").GetProperty("status").GetString().Should().Be("Executed");

        var responded = await DsarEnvironment.ReadAsync<JsonElement>(await _dpo.PostEmpty($"api/v1/requests/{created.Id}/respond"));
        responded.GetProperty("request").GetProperty("identifiersPurged").GetBoolean().Should().BeTrue();
        responded.GetProperty("letterTr").GetString().Should().Contain("**Silindi:** customers (1)").And.Contain("Kişisel Verileri Koruma Kurulu");

        // Evidence: signed, complete, and contains no identifier in clear.
        var pack = await (await _audit.Get($"api/v1/requests/{created.Id}/evidence")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var report = EvidencePack.Verify(new MemoryStream(pack));
        report.Valid.Should().BeTrue(string.Join("; ", report.Problems));
        var allText = System.Text.Encoding.UTF8.GetString(Unzip(pack));
        allText.Should().NotContain(person.Tckn).And.NotContain(person.Phone).And.NotContain(person.Email);
        allText.Should().Contain("execution.completed").And.Contain("subject.identifiers_purged");

        var check = await DsarEnvironment.ReadAsync<VerificationReport>(await _audit.PostAsync(new Uri("api/v1/evidence/verify", UriKind.Relative), new ByteArrayContent(pack)));
        check.Valid.Should().BeTrue();
        check.KeyId.Should().Be(report.KeyId);

        // The customer row is gone from the source.
        await using var cmd = env.Finder.CreateCommand("SELECT count(*) FROM clinic.customers WHERE right(regexp_replace(phone_number, '\\D', '', 'g'), 10) = $1");
        cmd.Parameters.Add(new() { Value = person.Phone });
        ((long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).Should().Be(0);

        // The closed request cannot be reopened, and its identifiers are gone from the store.
        (await _dpo.PostEmpty($"api/v1/requests/{created.Id}/discovery")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Tampered_evidence_is_rejected_by_the_verify_endpoint()
    {
        var person = Next(t => t.Owned.Count > 0);
        var created = await CreateAsync(Body(person, [RequestType.Objection]));
        var pack = await (await _audit.Get($"api/v1/requests/{created.Id}/evidence")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        pack[pack.Length / 2] ^= 0xFF;

        var check = await DsarEnvironment.ReadAsync<VerificationReport>(await _audit.PostAsync(new Uri("api/v1/evidence/verify", UriKind.Relative), new ByteArrayContent(pack)));
        check.Valid.Should().BeFalse();
    }

    [Fact]
    public async Task An_email_that_is_not_on_file_for_the_verified_person_is_never_searched()
    {
        // The applicant gives their own TCKN (OTP goes to their own phone) plus someone else's e-mail.
        var applicant = Next(t => t.Owned.Any(o => o.Table == "hastalar") && !t.Owned.Any(o => o.Table == "customers"));
        var victim = env.Dataset.People.First(p => p.Index != applicant.Index && env.Dataset.Truth[p.Index].Owned.Any(o => o.Table == "customers"));
        var created = await CreateAsync(Body(applicant, [RequestType.Access], email: victim.Email));
        await VerifyWithOtpAsync(created.Id);

        var discovery = await DsarEnvironment.ReadAsync<JsonElement>(await _dpo.PostEmpty($"api/v1/requests/{created.Id}/discovery"));
        var owned = discovery.GetProperty("sources")[0].GetProperty("owned");
        owned.TryGetProperty("customers", out _).Should().BeFalse("the victim's portal account must not appear in the applicant's export");

        var audit = await DsarEnvironment.ReadAsync<List<AuditEvent>>(await _audit.Get($"api/v1/requests/{created.Id}/audit"));
        audit.Should().Contain(e => e.Event == "discovery.recorded" && e.Data.Contains("\"ignoredUnboundIdentifiers\":[\"Email\"]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Access_and_erasure_need_the_export_first_and_the_export_is_not_stored()
    {
        var person = Next(t => t.Owned.Any(o => o.Table == "customers"));
        var created = await CreateAsync(Body(person, [RequestType.Access, RequestType.Erasure], Channel.Kep));
        (await _verifier.PostJson($"api/v1/requests/{created.Id}/verification/channel", new { factor = "QualifiedChannel", evidence = "KEP message 2026/77, e-signature valid" })).EnsureSuccessStatusCode();
        var hash = await DiscoverAndPlanAsync(created.Id);
        (await _legal.PostJson($"api/v1/requests/{created.Id}/approve", new { planHash = hash })).EnsureSuccessStatusCode();

        var tooEarly = await _ops.PostEmpty($"api/v1/requests/{created.Id}/execute");
        tooEarly.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await tooEarly.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("export_first");

        var export = await _dpo.Get($"api/v1/requests/{created.Id}/export");
        export.EnsureSuccessStatusCode();
        var json = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.Should().Contain(person.FirstName);

        var store = env.Services.GetRequiredService<WorkflowStore>();
        var artifacts = await store.ArtifactsAsync(created.Id, TestContext.Current.CancellationToken);
        artifacts.Should().NotContain(a => a.Content.Contains(person.FirstName, StringComparison.Ordinal) && a.Kind != "response-tr" && a.Kind != "response-en");
        artifacts.Single(a => a.Kind == "access-export-sha256").Content.Should().Be(export.Headers.GetValues("X-Export-Sha256").Single());

        (await _ops.PostEmpty($"api/v1/requests/{created.Id}/execute")).EnsureSuccessStatusCode();
        var letter = await DsarEnvironment.ReadAsync<JsonElement>(await _dpo.PostEmpty($"api/v1/requests/{created.Id}/respond"));
        letter.GetProperty("letterEn").GetString().Should().Contain(export.Headers.GetValues("X-Export-Sha256").Single());
    }

    [Fact]
    public async Task Planner_cannot_approve_and_unknown_keys_are_refused()
    {
        var person = Next(t => t.Owned.Any(o => o.Table == "customers"));
        var created = await CreateAsync(Body(person, [RequestType.Erasure], Channel.Kep));
        (await _verifier.PostJson($"api/v1/requests/{created.Id}/verification/channel", new { factor = "QualifiedChannel", evidence = "e-imza ok" })).EnsureSuccessStatusCode();
        var dpo2 = env.Client("key-dpo2"); // dpo + approver roles
        (await dpo2.PostEmpty($"api/v1/requests/{created.Id}/discovery")).EnsureSuccessStatusCode();
        var plan = await DsarEnvironment.ReadAsync<JsonElement>(await dpo2.PostEmpty($"api/v1/requests/{created.Id}/plan"));

        var self = await dpo2.PostJson($"api/v1/requests/{created.Id}/approve", new { planHash = plan.GetProperty("planHash").GetString() });
        self.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await self.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("four_eyes");

        (await env.Client("wrong-key").Get($"api/v1/requests/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _ops.PostEmpty($"api/v1/requests/{created.Id}/plan")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Five_wrong_codes_send_the_request_to_manual_review()
    {
        var person = Next(t => t.Owned.Any(o => o.Table == "hastalar"));
        var created = await CreateAsync(Body(person, [RequestType.Erasure]));
        (await _verifier.PostEmpty($"api/v1/requests/{created.Id}/verification/challenge")).EnsureSuccessStatusCode();
        var code = (await DsarEnvironment.ReadAsync<JsonElement>(await _verifier.Get($"demo/inbox/{created.Id}"))).GetProperty("code").GetString()!;
        var wrong = code == "000000" ? "111111" : "000000";

        JsonElement last = default;
        for (var i = 0; i < 5; i++)
        {
            last = await DsarEnvironment.ReadAsync<JsonElement>(await _verifier.PostJson($"api/v1/requests/{created.Id}/verification/confirm", new { code = wrong }));
        }

        last.GetProperty("outcome").GetString().Should().Be("LockedOut");
        last.GetProperty("request").GetProperty("status").GetString().Should().Be("ManualReview");
        var late = await DsarEnvironment.ReadAsync<JsonElement>(await _verifier.PostJson($"api/v1/requests/{created.Id}/verification/confirm", new { code }));
        late.GetProperty("outcome").GetString().Should().Be("LockedOut", "even the right code no longer works");
        late.GetProperty("request").GetProperty("assurance").GetString().Should().NotBe("High");
    }

    [Fact]
    public async Task Parallel_execute_calls_run_the_plan_once()
    {
        var person = Next(t => t.Owned.Any(o => o.Table == "customers") && t.Owned.Count > 2);
        var created = await CreateAsync(Body(person, [RequestType.Erasure], Channel.Kep));
        (await _verifier.PostJson($"api/v1/requests/{created.Id}/verification/channel", new { factor = "QualifiedChannel", evidence = "KEP ok" })).EnsureSuccessStatusCode();
        var hash = await DiscoverAndPlanAsync(created.Id);
        (await _legal.PostJson($"api/v1/requests/{created.Id}/approve", new { planHash = hash })).EnsureSuccessStatusCode();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Client("key-ops").PostEmpty($"api/v1/requests/{created.Id}/execute")));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(3);
        var audit = await DsarEnvironment.ReadAsync<List<AuditEvent>>(await _audit.Get($"api/v1/requests/{created.Id}/audit"));
        audit.Count(e => e.Event == "execution.started").Should().Be(1);
        audit.Count(e => e.Event == "execution.recorded").Should().Be(1);
        AuditChain.Verify(audit).Should().BeEmpty();
    }

    [Fact]
    public async Task A_crash_after_the_source_commit_is_resumed_without_running_the_plan_twice()
    {
        var person = Next(t => t.Owned.Any(o => o.Table == "customers"));
        var created = await CreateAsync(Body(person, [RequestType.Erasure], Channel.Kep));
        (await _verifier.PostJson($"api/v1/requests/{created.Id}/verification/channel", new { factor = "QualifiedChannel", evidence = "KEP ok" })).EnsureSuccessStatusCode();
        var hash = await DiscoverAndPlanAsync(created.Id);
        (await _legal.PostJson($"api/v1/requests/{created.Id}/approve", new { planHash = hash })).EnsureSuccessStatusCode();

        env.Faults.CrashAfterSourceCommit = true;
        var crashed = await _ops.PostEmpty($"api/v1/requests/{created.Id}/execute");
        crashed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await DsarEnvironment.ReadAsync<RequestView>(await _dpo.Get($"api/v1/requests/{created.Id}"))).Status.Should().Be(RequestStatus.Executing);

        var resumed = await DsarEnvironment.ReadAsync<JsonElement>(await _ops.PostEmpty($"api/v1/requests/{created.Id}/execute"));
        resumed.GetProperty("request").GetProperty("status").GetString().Should().Be("Executed");
        var result = resumed.GetProperty("results")[0];
        result.GetProperty("resumed").GetBoolean().Should().BeTrue("the source already matched the applied plan");
        result.GetProperty("postCheck").GetProperty("unexpected").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Deadline_monitor_marks_an_old_open_request_overdue_once()
    {
        var person = Next(t => t.Owned.Count > 0);
        var created = await CreateAsync(Body(person, [RequestType.Rectification], receivedAt: DateTimeOffset.UtcNow.AddDays(-32)));
        created.DeadlineStage.Should().Be(Deadlines.DeadlineStage.Overdue);
        created.DaysLeft.Should().BeNegative();

        (await _dpo.Get("api/v1/deadlines")).EnsureSuccessStatusCode();
        (await _dpo.Get("api/v1/deadlines")).EnsureSuccessStatusCode();
        var audit = await DsarEnvironment.ReadAsync<List<AuditEvent>>(await _audit.Get($"api/v1/requests/{created.Id}/audit"));
        audit.Count(e => e.Event == "deadline.overdue").Should().Be(1);
    }

    [Fact]
    public async Task Invalid_applications_are_rejected_with_every_problem()
    {
        var response = await _intake.PostJson("api/v1/requests", new { types = ErasureOnly, channel = "Kep", subject = new { nationalId = "10000000147", firstName = "A" } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("check digits").And.Contain("lastName").And.Contain("channelEvidence");
    }

    private static byte[] Unzip(byte[] zip)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        using var all = new MemoryStream();
        foreach (var entry in archive.Entries)
        {
            using var s = entry.Open();
            s.CopyTo(all);
        }

        return all.ToArray();
    }
}
