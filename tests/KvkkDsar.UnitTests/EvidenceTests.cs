using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KvkkDsar.Deadlines;
using KvkkDsar.Evidence;
using KvkkDsar.Responses;
using KvkkDsar.Workflow;

namespace KvkkDsar.UnitTests;

public sealed class EvidenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static List<AuditEvent> Chain(int n)
    {
        var events = new List<AuditEvent>();
        for (var i = 0; i < n; i++)
        {
            events.Add(AuditChain.Next(events.LastOrDefault(), T0.AddMinutes(i), "dpo", $"step.{i}", $"{{\"i\":{i}}}"));
        }

        return events;
    }

    private static byte[] Pack(ECDsa key, List<AuditEvent>? audit = null) =>
        EvidencePack.Build(Guid.Parse("8c1f1f9e-7d4f-4c8e-9d2a-1d4a6b9e0c11"), "DSAR-2026-000001", T0, audit ?? Chain(5),
            new Dictionary<string, byte[]> { ["plan.json"] = "{\"a\":1}"u8.ToArray(), ["response-tr.md"] = Encoding.UTF8.GetBytes("# Yanıt\n") }, key);

    [Fact]
    public void Chain_verifies_and_detects_edit_delete_and_reorder()
    {
        var chain = Chain(6);
        AuditChain.Verify(chain).Should().BeEmpty();

        var edited = chain.ToList();
        edited[2] = edited[2] with { Data = "{\"i\":99}" };
        AuditChain.Verify(edited).Should().ContainSingle().Which.Should().Contain("event 3");

        var deleted = chain.Where(e => e.Seq != 3).ToList();
        AuditChain.Verify(deleted).Should().Contain(p => p.Contains("missing or reordered", StringComparison.Ordinal));

        var reordered = chain.ToList();
        (reordered[1], reordered[2]) = (reordered[2], reordered[1]);
        AuditChain.Verify(reordered).Should().NotBeEmpty();
    }

    [Fact]
    public void Pack_verifies_offline_with_its_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var report = EvidencePack.Verify(new MemoryStream(Pack(key)), EvidencePack.KeyId(key));
        report.Problems.Should().BeEmpty();
        report.Valid.Should().BeTrue();
        report.Files.Should().Be(3);
        report.Reference.Should().Be("DSAR-2026-000001");
    }

    [Fact]
    public void Edited_file_is_detected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tampered = Rewrite(Pack(key), "plan.json", "{\"a\":2}"u8.ToArray());
        var report = EvidencePack.Verify(new MemoryStream(tampered));
        report.Valid.Should().BeFalse();
        report.Problems.Should().Contain("plan.json: content does not match the manifest hash");
    }

    [Fact]
    public void Added_file_and_edited_audit_log_are_detected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var extra = Rewrite(Pack(key), "notes.txt", "x"u8.ToArray());
        EvidencePack.Verify(new MemoryStream(extra)).Problems.Should().Contain("notes.txt: present but not listed in the manifest");

        var chain = Chain(5);
        chain[1] = chain[1] with { Actor = "mallory" };
        var badLog = Rewrite(Pack(key), EvidencePack.AuditLogName, EvidencePack.AuditLog(chain));
        EvidencePack.Verify(new MemoryStream(badLog)).Problems.Should().Contain(p => p.Contains("event 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Re_signing_with_another_key_only_passes_without_a_trusted_key_id()
    {
        using var controller = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Pack(attacker);

        EvidencePack.Verify(new MemoryStream(forged)).Valid.Should().BeTrue("a self-consistent pack");
        EvidencePack.Verify(new MemoryStream(forged), EvidencePack.KeyId(controller)).Problems.Should().Contain(p => p.Contains("not the trusted key", StringComparison.Ordinal));
    }

    [Fact]
    public void Garbage_is_reported_not_thrown()
    {
        EvidencePack.Verify(new MemoryStream("not a zip"u8.ToArray())).Valid.Should().BeFalse();
    }

    private static byte[] Rewrite(byte[] zip, string name, byte[] content)
    {
        using var ms = new MemoryStream();
        ms.Write(zip);
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            archive.GetEntry(name)?.Delete();
            using var s = archive.CreateEntry(name).Open();
            s.Write(content);
        }

        return ms.ToArray();
    }
}

public sealed class ResponseLetterTests
{
    [Theory]
    [InlineData(Language.Tr, "response-erasure-tr.md")]
    [InlineData(Language.En, "response-erasure-en.md")]
    public async Task Erasure_letter_is_golden(Language language, string golden)
    {
        // A person with a mix of outcomes: some rows kept, some erased, a mention elsewhere.
        var person = TestData.Dataset.People.First(p =>
        {
            var t = TestData.Dataset.Truth[p.Index];
            return t.Owned.Any(o => o.Table == "customers") && t.Owned.Any(o => o.Table == "faturalar") && t.Mentions.Count > 0;
        });
        var (discovery, plan) = await PlannerTests.PlanFor(person);
        var received = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(3));
        var deadline = DeadlineCalculator.Compute(received, DeadlinePolicy.Strict);
        var model = new LetterModel("DSAR-2026-000042", TestData.Policy.Controller, person.FullName, [RequestType.Erasure],
            deadline.ReceivedOn, deadline.DueOn, PlannerTests.Today, DeadlineCalculator.ComplaintWindowEnds(received, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.FromHours(3))),
            discovery, plan, TestData.Policy, null);

        TestData.AssertGolden(golden, ResponseLetter.Render(model, language));
    }
}
