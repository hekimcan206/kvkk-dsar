using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KvkkDsar.Api.Persistence;
using KvkkDsar.Deadlines;
using KvkkDsar.Discovery;
using KvkkDsar.Evidence;
using KvkkDsar.Identifiers;
using KvkkDsar.Planning;
using KvkkDsar.Postgres;
using KvkkDsar.Responses;
using KvkkDsar.Verification;
using KvkkDsar.Workflow;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KvkkDsar.Api.Services;

/// <summary>Bad input from the caller (maps to 400).</summary>
internal sealed class DsarValidationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Test hook: fail after a source committed but before the workflow recorded it (a crash in between).</summary>
internal interface IDsarFaults
{
    void AfterSourceCommit(string source);
}

internal sealed record CreateCommand(
    IReadOnlyList<RequestType> Types, Channel Channel, DateTimeOffset? ReceivedAt, string? ChannelEvidence, string? SenderEmail,
    string? NationalId, string? Email, string? Phone, string? FirstName, string? LastName, DateOnly? BirthDate);

internal sealed record OnFileContacts(IReadOnlyList<string> Phones, IReadOnlyList<string> Emails, IReadOnlyList<DateOnly> BirthDates, bool FoundByNationalId);

internal sealed record SourcePlan(string Source, ErasurePlan Plan, string Sha256);

/// <summary>
/// Orchestrates one request end to end. The pure core decides; this class reads and writes. Rules worth knowing:
/// <list type="bullet">
/// <item>Discovery searches only <b>bound</b> identifiers: those verification tied to the person (the TCKN behind an
/// on-file contact that received the OTP, contacts on file for that TCKN). An e-mail typed into the form that is not on
/// file for the verified person is ignored, otherwise anyone could verify with their own phone and pull someone else's
/// portal account into "their" access export.</item>
/// <item>The access export is built on demand from the source and never stored; only its hash is.</item>
/// <item>Execution holds a per-request advisory lock: at most one executor runs, also across API instances.</item>
/// </list>
/// </summary>
internal sealed class DsarService(
    WorkflowStore store,
    SourceRegistry sources,
    SubjectVault vault,
    IOtpSender otpSender,
    NpgsqlDataSource workflowDb,
    IOptions<DsarOptions> options,
    EvidenceSigner signer,
    DsarMetrics metrics,
    TimeProvider time,
    IDsarFaults? faults = null)
{
    private static readonly JsonSerializerOptions Json = WorkflowStore.Json;
    private readonly SubjectFingerprinter _fingerprinter = new(DsarOptions.Key(options.Value.FingerprintKey, nameof(DsarOptions.FingerprintKey), 32));
    private readonly OtpService _otp = new(DsarOptions.Key(options.Value.OtpPepper, nameof(DsarOptions.OtpPepper), 32), time);

    public async Task<RequestRow> CreateAsync(CreateCommand c, string actor, CancellationToken ct)
    {
        var errors = new List<string>();
        if (c.Types.Count == 0)
        {
            errors.Add("At least one request type is required.");
        }

        var (subject, subjectErrors) = SubjectIdentifiers.Create(c.NationalId, c.Email, c.Phone, c.FirstName, c.LastName, c.BirthDate);
        errors.AddRange(subjectErrors);
        var now = time.GetUtcNow();
        var receivedAt = c.ReceivedAt ?? now;
        if (receivedAt > now.AddMinutes(5))
        {
            errors.Add("receivedAt cannot be in the future.");
        }

        if (c.Channel is Channel.Kep or Channel.SecureESignature or Channel.MobileSignature && string.IsNullOrWhiteSpace(c.ChannelEvidence))
        {
            errors.Add("A KEP / e-signature / mobile-signature application needs channelEvidence (message or signature reference).");
        }

        Deadline? deadline = null;
        try
        {
            deadline = DeadlineCalculator.Compute(receivedAt, options.Value.DeadlinePolicy);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            errors.Add(ex.Message);
        }

        if (errors.Count > 0)
        {
            throw new DsarValidationException(errors);
        }

        var id = Guid.CreateVersion7();
        var state = DsarState.New(id, [.. c.Types.Distinct()], c.Channel);
        var sealedSubject = new SealedSubject(subject!, null, []);
        var masked = new Dictionary<string, string>(subject!.Masked());
        if (c.SenderEmail is not null && Emails.Normalize(c.SenderEmail) is { } sender)
        {
            masked["SenderEmail"] = Mask.Email(sender);
            sealedSubject = sealedSubject with { Unbound = [$"sender:{sender}"] };
        }

        var row = await store.CreateAsync(new NewRequest(id, state, receivedAt, deadline!, _fingerprinter.Of(subject), masked,
            $"{subject.FirstName} {subject.LastName}", vault.Seal(id, sealedSubject), c.ChannelEvidence, actor),
            JsonSerializer.Serialize(new
            {
                types = state.Types,
                channel = state.Channel,
                subject = masked,
                fingerprint = _fingerprinter.Of(subject),
                receivedAt,
                dueOn = deadline!.DueOn,
                deadlinePolicy = deadline.Policy,
                requiredAssurance = state.Required,
            }, Json), ct);
        metrics.Created(string.Join('+', state.Types));
        return row;
    }

    public Task<RequestRow?> GetAsync(Guid id, CancellationToken ct) => store.GetAsync(id, ct);

    // ---------------------------------------------------------------- identity verification

    /// <summary>On-file contacts and birth dates for the applicant's strongest identifier, from every source.</summary>
    private async Task<OnFileContacts> LookupAsync(SubjectIdentifiers provided, CancellationToken ct)
    {
        // Only the strongest identifier given is searched. With a TCKN, contacts come from the records that carry it.
        var probe = provided.NationalId is not null
            ? new SubjectIdentifiers { NationalId = provided.NationalId }
            : provided.Phone is not null ? new SubjectIdentifiers { Phone = provided.Phone } : new SubjectIdentifiers { Email = provided.Email };
        var phones = new SortedSet<string>(StringComparer.Ordinal);
        var emails = new SortedSet<string>(StringComparer.Ordinal);
        var births = new SortedSet<DateOnly>();
        foreach (var source in sources.All)
        {
            var engine = await source.EngineAsync(ct);
            var reader = await source.ReaderAsync(ct);
            var found = await engine.DiscoverAsync(source.Name, reader, probe, DiscoveryOptions.Full with { FollowForeignKeys = false }, ct);
            foreach (var group in found.Owned.GroupBy(o => o.Table))
            {
                var table = engine.Catalog.Table(group.Key)!;
                var contactColumns = source.Inventory.ColumnsOf(group.Key)
                    .Where(col => col.Type is "Phone" or "Email" or "DateOfBirth" && !source.Policy.IsExcluded(group.Key, col.Column) && source.Policy.MentionFor(group.Key, col.Column) is null)
                    .ToList();
                var values = await reader.FetchAsync(table, [.. group.Select(o => o.Key)], [.. contactColumns.Select(col => col.Column)], ct);
                foreach (var row in values.Values)
                {
                    foreach (var col in contactColumns)
                    {
                        var v = row.GetValueOrDefault(col.Column);
                        switch (col.Type)
                        {
                            case "Phone" when PhoneNumbers.Canonical(v) is { } p:
                                phones.Add(p);
                                break;
                            case "Email" when Emails.Normalize(v) is { } e:
                                emails.Add(e);
                                break;
                            case "DateOfBirth" when ValueText.AsDate(v) is { } d:
                                births.Add(d);
                                break;
                        }
                    }
                }
            }
        }

        return new OnFileContacts([.. phones], [.. emails], [.. births], provided.NationalId is not null);
    }

    /// <summary>The identifiers discovery may use once a contact on file (or a signed channel) has proven the person.</summary>
    private static (SubjectIdentifiers Bound, List<string> Unbound) Bind(SubjectIdentifiers provided, OnFileContacts onFile, string? provenPhone, string? provenEmail)
    {
        var phone = provided.Phone is not null && onFile.Phones.Contains(provided.Phone) ? provided.Phone : provenPhone;
        var email = provided.Email is not null && onFile.Emails.Contains(provided.Email) ? provided.Email : provenEmail;
        var bound = new SubjectIdentifiers
        {
            NationalId = onFile.FoundByNationalId ? provided.NationalId : null,
            Phone = phone,
            Email = email,
            FirstName = provided.FirstName,
            LastName = provided.LastName,
            BirthDate = provided.BirthDate,
        };
        var unbound = new List<string>();
        if (provided.Phone is not null && bound.Phone != provided.Phone)
        {
            unbound.Add("Phone");
        }

        if (provided.Email is not null && bound.Email != provided.Email)
        {
            unbound.Add("Email");
        }

        return (bound, unbound);
    }

    public async Task<(RequestRow Row, string? SentTo)> ChallengeAsync(Guid id, string actor, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        var subject = vault.Open(id, row.Sealed);
        var onFile = await LookupAsync(subject.Provided, ct);
        var (kind, destination) = onFile.Phones.Count > 0 ? (IdentifierKind.Phone, onFile.Phones[0])
            : onFile.Emails.Count > 0 ? (IdentifierKind.Email, onFile.Emails[0])
            : (IdentifierKind.Phone, (string?)null);

        if (destination is null)
        {
            var (failed, _) = await store.MutateAsync(id, new VerificationFailed(actor, "no contact on file"),
                _ => new { reason = "no phone or e-mail on file for the given identifier; verify with a signed letter or qualified channel" }, ct);
            return (failed, null);
        }

        var masked = Mask.Of(kind, destination);
        var (code, challenge) = _otp.Issue(id, masked);
        var (updated, _) = await store.MutateAsync(id, new ChallengeSent(actor), _ => new { sentTo = masked, channel = kind, expiresAt = challenge.ExpiresAt }, ct,
            within: (conn, tx, _) => WorkflowStore.SaveChallengeAsync(conn, tx, id, challenge, ct));
        await otpSender.SendAsync(id, kind, destination, code, ct);
        return (updated, masked);
    }

    public async Task<(RequestRow Row, OtpOutcome Outcome, int AttemptsLeft)> ConfirmAsync(Guid id, string code, string actor, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        var counted = await store.CountAttemptAsync(id, ct) ?? throw new WorkflowException("no_challenge", "No one-time code was sent for this request.");
        var (outcome, after) = _otp.Check(id, counted, code);
        var left = Math.Max(0, OtpService.MaxAttempts - after.Attempts);
        switch (outcome)
        {
            case OtpOutcome.Accepted:
                {
                    var subject = vault.Open(id, row.Sealed);
                    var onFile = await LookupAsync(subject.Provided, ct);
                    var provenPhone = onFile.Phones.FirstOrDefault(p => Mask.Phone(p) == counted.SentToMasked);
                    var provenEmail = provenPhone is null ? onFile.Emails.FirstOrDefault(e => Mask.Email(e) == counted.SentToMasked) : null;
                    var (bound, unbound) = Bind(subject.Provided, onFile, provenPhone, provenEmail);
                    var sealedNew = vault.Seal(id, subject with { Bound = bound, Unbound = unbound });
                    var (r1, _) = await store.MutateAsync(id, new AddFactor(actor, VerificationFactor.OtpToContactOnFile),
                        _ => new { factor = VerificationFactor.OtpToContactOnFile, sentTo = counted.SentToMasked, attempts = after.Attempts, ignoredUnboundIdentifiers = unbound }, ct,
                        within: (conn, tx, _) => WorkflowStore.DeleteChallengeAsync(conn, tx, id, ct), newSealed: sealedNew);
                    if (!r1.State.IsVerified && subject.Provided.BirthDate is { } dob && onFile.BirthDates.Contains(dob))
                    {
                        (r1, _) = await store.MutateAsync(id, new AddFactor(actor, VerificationFactor.KnowledgeMatch), _ => new { factor = VerificationFactor.KnowledgeMatch }, ct);
                    }

                    return (r1, outcome, left);
                }

            case OtpOutcome.LockedOut:
                {
                    var (r2, _) = await store.MutateAsync(id, new VerificationFailed(actor, "otp locked"), _ => new { reason = "one-time code attempts exhausted", attempts = after.Attempts }, ct);
                    return (r2, outcome, 0);
                }

            default:
                await store.AuditAsync(id, actor, outcome == OtpOutcome.Expired ? "verification.otp_expired" : "verification.otp_wrong", new { attemptsLeft = left }, ct);
                return (row, outcome, left);
        }
    }

    /// <summary>Records a channel-based factor: a checked qualified signature, a signed letter, or a registered e-mail sender.</summary>
    public async Task<RequestRow> VerifyChannelAsync(Guid id, VerificationFactor factor, string? evidence, string actor, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        var subject = vault.Open(id, row.Sealed);
        if (factor is VerificationFactor.OtpToContactOnFile or VerificationFactor.KnowledgeMatch)
        {
            throw new DsarValidationException(["OTP and knowledge factors are established by the system, not recorded by hand."]);
        }

        if (factor is VerificationFactor.QualifiedChannel or VerificationFactor.SignedLetter && string.IsNullOrWhiteSpace(evidence))
        {
            throw new DsarValidationException(["Record what was checked (signature / KEP message reference, identity document check)."]);
        }

        if (factor == VerificationFactor.QualifiedChannel && subject.Provided.NationalId is null)
        {
            throw new DsarValidationException(["A qualified signature identifies a TCKN; the request has none."]);
        }

        var onFile = await LookupAsync(subject.Provided, ct);
        string? provenEmail = null;
        if (factor == VerificationFactor.RegisteredEmail)
        {
            var sender = subject.Unbound.FirstOrDefault(u => u.StartsWith("sender:", StringComparison.Ordinal))?["sender:".Length..];
            if (sender is null || !onFile.Emails.Contains(sender))
            {
                throw new WorkflowException("sender_not_on_file", "The sender address is not an e-mail address on file for this person (Communiqué Art. 5).");
            }

            provenEmail = sender;
        }

        var (bound, unbound) = Bind(subject.Provided, onFile, null, provenEmail);
        var (updated, _) = await store.MutateAsync(id, new AddFactor(actor, factor), _ => new { factor, evidence, ignoredUnboundIdentifiers = unbound }, ct,
            newSealed: vault.Seal(id, subject with { Bound = bound, Unbound = unbound }));
        if (!updated.State.IsVerified && subject.Provided.BirthDate is { } dob && onFile.BirthDates.Contains(dob))
        {
            (updated, _) = await store.MutateAsync(id, new AddFactor(actor, VerificationFactor.KnowledgeMatch), _ => new { factor = VerificationFactor.KnowledgeMatch }, ct);
        }

        return updated;
    }

    // ---------------------------------------------------------------- discovery, plan, approval

    public async Task<(RequestRow Row, IReadOnlyList<DiscoveryResult> Results)> DiscoverAsync(Guid id, string actor, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        if (!row.State.IsVerified)
        {
            throw new WorkflowException("not_verified", $"Identity is not verified to the level this request needs ({row.State.Required}).");
        }

        var subject = vault.Open(id, row.Sealed);
        var bound = subject.Bound ?? subject.Provided;
        var results = new List<DiscoveryResult>();
        foreach (var source in sources.All)
        {
            var result = await (await source.EngineAsync(ct)).DiscoverAsync(source.Name, await source.ReaderAsync(ct), bound, cancellationToken: ct);
            metrics.Discovery(source.Name, result.ElapsedMs);
            results.Add(result);
        }

        var (updated, _) = await store.MutateAsync(id, new RecordDiscovery(actor), _ => new
        {
            searchedWith = bound.Masked().Keys,
            ignoredUnboundIdentifiers = subject.Unbound.Where(u => !u.StartsWith("sender:", StringComparison.Ordinal)),
            sources = results.Select(r => new { source = r.Source, owned = r.Owned.Count, mentions = r.Mentions.Count, fingerprint = r.Fingerprint(), unreachable = r.Unreachable.Select(u => u.Table) }),
        }, ct, within: async (conn, tx, _) =>
        {
            foreach (var r in results)
            {
                await WorkflowStore.SaveArtifactAsync(conn, tx, id, "discovery", r.Source, JsonSerializer.Serialize(r, Json), ct);
            }
        });
        return (updated, results);
    }

    public async Task<(RequestRow Row, IReadOnlyList<SourcePlan> Plans, string Hash)> PlanAsync(Guid id, string actor, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        var discoveries = await DiscoveriesAsync(id, ct);
        var today = DeadlineCalculator.LocalDate(time.GetUtcNow());
        var plans = new List<SourcePlan>();
        foreach (var source in sources.All)
        {
            var discovery = discoveries.GetValueOrDefault(source.Name) ?? throw new WorkflowException("no_discovery", "Run discovery first.");
            var catalog = await source.CatalogAsync(ct);
            var reader = await source.ReaderAsync(ct);
            var anchors = new Dictionary<(string, string), IReadOnlyDictionary<string, string?>>();
            foreach (var (table, columns) in ErasurePlanner.AnchorColumns(source.Policy, discovery))
            {
                var keys = discovery.Owned.Where(o => o.Table == table).Select(o => o.Key).ToList();
                foreach (var (key, values) in await reader.FetchAsync(catalog.Table(table)!, keys, columns, ct))
                {
                    anchors[(table, key)] = values;
                }
            }

            var plan = ErasurePlanner.Plan(discovery, catalog, source.Inventory, source.Policy, anchors, today);
            plans.Add(new SourcePlan(source.Name, plan, plan.Hash()));
        }

        var hash = CombinedHash(plans);
        var (updated, _) = await store.MutateAsync(id, new RecordPlan(actor, hash), _ => new
        {
            planHash = hash,
            sources = plans.Select(p => new { source = p.Source, sha256 = p.Sha256, counts = p.Plan.Counts(), notify = p.Plan.NotifyRecipients }),
        }, ct, within: async (conn, tx, _) =>
        {
            foreach (var p in plans)
            {
                await WorkflowStore.SaveArtifactAsync(conn, tx, id, "plan", p.Source, JsonSerializer.Serialize(p.Plan, ErasurePlan.Json), ct);
            }
        });
        return (updated, plans, hash);
    }

    public async Task<RequestRow> ApproveAsync(Guid id, string planHash, string actor, CancellationToken ct) =>
        (await store.MutateAsync(id, new Approve(actor, planHash), r => new { planHash, plannedBy = r.State.PlannedBy }, ct)).Row;

    // ---------------------------------------------------------------- access export

    public async Task<(RequestRow Row, byte[] Export, string Sha256)> ExportAsync(Guid id, string actor, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        var discoveries = await DiscoveriesAsync(id, ct);
        var documents = new List<byte[]>();
        foreach (var source in sources.All)
        {
            var discovery = discoveries.GetValueOrDefault(source.Name) ?? throw new WorkflowException("no_discovery", "Run discovery first.");
            var catalog = await source.CatalogAsync(ct);
            var reader = await source.ReaderAsync(ct);
            var values = new Dictionary<(string, string), IReadOnlyDictionary<string, string?>>();
            foreach (var (table, columns) in AccessExport.ColumnsToRead(discovery, source.Inventory))
            {
                var keys = discovery.Owned.Where(o => o.Table == table).Select(o => o.Key).ToList();
                foreach (var (key, v) in await reader.FetchAsync(catalog.Table(table)!, keys, columns, ct))
                {
                    values[(table, key)] = v;
                }
            }

            documents.Add(AccessExport.Build(row.Reference, time.GetUtcNow(), discovery, source.Inventory, source.Policy, values));
        }

        var export = documents.Count == 1 ? documents[0] : Encoding.UTF8.GetBytes("[" + string.Join(",", documents.Select(Encoding.UTF8.GetString)) + "]");
        var sha = Convert.ToHexStringLower(SHA256.HashData(export));
        var (updated, _) = await store.MutateAsync(id, new RecordAccessExport(actor), _ => new { sha256 = sha, bytes = export.Length }, ct,
            within: (conn, tx, _) => WorkflowStore.SaveArtifactAsync(conn, tx, id, "access-export-sha256", "", sha, ct));
        return (updated, export, sha);
    }

    // ---------------------------------------------------------------- execution

    public async Task<(RequestRow Row, IReadOnlyList<object> Results)> ExecuteAsync(Guid id, string actor, CancellationToken ct)
    {
        // One executor per request, across instances: a session advisory lock on the workflow database.
        await using var lockConnection = await workflowDb.OpenConnectionAsync(ct);
        var lockKey = BitConverter.ToInt64(SHA256.HashData(id.ToByteArray()), 0);
        await using (var tryLock = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", lockConnection))
        {
            tryLock.Parameters.Add(new() { Value = lockKey });
            if (!(bool)(await tryLock.ExecuteScalarAsync(ct))!)
            {
                throw new WorkflowException("execution_in_progress", "Another executor is working on this request.");
            }
        }

        try
        {
            var row = await RequireAsync(id, ct);
            if (row.State.Status == RequestStatus.Approved)
            {
                (row, _) = await store.MutateAsync(id, new BeginExecution(actor), r => new { planHash = r.State.PlanHash, approvedBy = r.State.ApprovedBy }, ct);
            }
            else if (row.State.Status != RequestStatus.Executing)
            {
                throw new WorkflowException("invalid_state", $"Cannot execute while the request is {row.State.Status}.");
            }

            var subject = vault.Open(id, row.Sealed);
            var bound = subject.Bound ?? subject.Provided;
            var plans = await PlansAsync(id, ct);
            if (CombinedHash(plans) != row.State.PlanHash)
            {
                throw new WorkflowException("plan_mismatch", "The stored plan is not the approved plan.");
            }

            var results = new List<object>();
            var allClean = true;
            foreach (var p in plans)
            {
                var source = sources.Get(p.Source);
                var engine = await source.EngineAsync(ct);
                var before = await engine.DiscoverAsync(source.Name, await source.ReaderAsync(ct), bound, cancellationToken: ct);
                ExecutionResult? execution = null;
                if (before.Fingerprint() == p.Plan.DiscoveryFingerprint)
                {
                    execution = await new PlanExecutor(source.Eraser, engine.Catalog, engine).ExecuteAsync(p.Plan, bound, ct);
                    if (execution.Drift)
                    {
                        return await DriftAsync(id, actor, p.Source, execution.DriftDetail!, ct);
                    }

                    metrics.RowsChanged("delete", execution.RowsDeleted);
                    metrics.RowsChanged("update", execution.RowsUpdated);
                    faults?.AfterSourceCommit(p.Source);
                }

                var after = await engine.DiscoverAsync(source.Name, await source.ReaderAsync(ct), bound, cancellationToken: ct);
                var residual = PostCheck(p.Plan, after);
                if (execution is null && residual.Unexpected.Count > 0)
                {
                    // Neither the planned state nor the applied state: someone changed the data in between.
                    return await DriftAsync(id, actor, p.Source, $"source matches neither the plan nor its applied result ({residual.Unexpected.Count} unexpected rows)", ct);
                }

                allClean &= residual.Unexpected.Count == 0;
                var record = new
                {
                    source = p.Source,
                    resumed = execution is null,
                    applied = execution?.Applied ?? true,
                    steps = execution?.Steps ?? [],
                    rowsDeleted = execution?.RowsDeleted ?? 0,
                    rowsUpdated = execution?.RowsUpdated ?? 0,
                    elapsedMs = execution?.ElapsedMs ?? 0,
                    postCheck = residual,
                };
                results.Add(record);
                await SaveArtifactAsync(id, "execution", p.Source, JsonSerializer.Serialize(record, Json), ct);
            }

            var (done, _) = await store.MutateAsync(id, new CompleteExecution(actor, allClean), _ => new { results = results.Count, clean = allClean }, ct);
            return (done, results);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", lockConnection);
            unlock.Parameters.Add(new() { Value = lockKey });
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    internal sealed record PostCheckResult(int ResidualOwned, int ResidualMentions, int ExpectedKept, IReadOnlyList<string> Unexpected);

    /// <summary>
    /// After execution, discovery may only find rows the plan kept (retained rows, and anonymised rows still linked to a
    /// retained parent). Anything else — an erased row, a mention that should be gone — is residual data.
    /// </summary>
    internal static PostCheckResult PostCheck(ErasurePlan plan, DiscoveryResult after)
    {
        var kept = plan.Actions.Where(a => a.Kind is PlannedActionKind.Retain or PlannedActionKind.Anonymise).Select(a => (a.Table, a.Key)).ToHashSet();
        var unexpected = after.Owned.Where(o => !kept.Contains((o.Table, o.Key))).Select(o => $"{o.Table}#{o.Key}")
            .Concat(after.Mentions.Where(m => !kept.Contains((m.Table, m.Key))).Select(m => $"{m.Table}#{m.Key}.{m.Column}"))
            .Distinct(StringComparer.Ordinal).ToList();
        return new PostCheckResult(after.Owned.Count, after.Mentions.Count, kept.Count, unexpected);
    }

    private async Task<(RequestRow, IReadOnlyList<object>)> DriftAsync(Guid id, string actor, string source, string detail, CancellationToken ct)
    {
        var (row, _) = await store.MutateAsync(id, new PlanDrifted(actor), _ => new { source, detail }, ct);
        return (row, [new { source, drift = true, detail }]);
    }

    // ---------------------------------------------------------------- response, rejection, evidence

    public async Task<(RequestRow Row, string LetterTr, string LetterEn)> RespondAsync(Guid id, string actor, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        var artifacts = await store.ArtifactsAsync(id, ct);
        var discoveries = await DiscoveriesAsync(id, ct);
        var plans = row.State.Wants(RequestType.Erasure) ? await PlansAsync(id, ct) : [];
        var exportSha = artifacts.FirstOrDefault(a => a.Kind == "access-export-sha256").Content;
        var now = time.GetUtcNow();
        var source = sources.All[0];
        var discovery = MergeDiscoveries(discoveries.Values);
        var plan = plans.Count == 0 ? null : plans[0].Plan with { Actions = [.. plans.SelectMany(p => p.Plan.Actions)], NotifyRecipients = [.. plans.SelectMany(p => p.Plan.NotifyRecipients).Distinct()] };
        var model = new LetterModel(row.Reference, source.Policy.Controller, row.SubjectName ?? "", row.State.Types,
            DeadlineCalculator.LocalDate(row.ReceivedAt), row.DueOn, DeadlineCalculator.LocalDate(now),
            DeadlineCalculator.ComplaintWindowEnds(row.ReceivedAt, now), discovery, plan, source.Policy, exportSha);
        var tr = ResponseLetter.Render(model, Language.Tr);
        var en = ResponseLetter.Render(model, Language.En);
        var onTime = now <= row.DueBy;
        var (updated, _) = await store.MutateAsync(id, new Respond(actor), _ => new { onTime, dueOn = row.DueOn, respondedOn = DeadlineCalculator.LocalDate(now) }, ct,
            within: async (conn, tx, _) =>
            {
                await WorkflowStore.SaveArtifactAsync(conn, tx, id, "response-tr", "", tr, ct);
                await WorkflowStore.SaveArtifactAsync(conn, tx, id, "response-en", "", en, ct);
            });
        metrics.Responded(onTime);
        return (updated, tr, en);
    }

    public async Task<RequestRow> RejectAsync(Guid id, string reason, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
        {
            throw new DsarValidationException(["A rejection needs a reason (1-1000 characters); KVKK Art. 13(3) requires one in the answer."]);
        }

        return (await store.MutateAsync(id, new Reject(actor, reason), _ => new { reason }, ct)).Row;
    }

    public async Task<RequestRow> WithdrawAsync(Guid id, string actor, CancellationToken ct) =>
        (await store.MutateAsync(id, new Withdraw(actor), _ => new { }, ct)).Row;

    public async Task<byte[]> EvidenceAsync(Guid id, CancellationToken ct)
    {
        var row = await RequireAsync(id, ct);
        var artifacts = await store.ArtifactsAsync(id, ct);
        var audit = await store.AuditTrailAsync(id, ct);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["request.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                row.Reference,
                row.State.Types,
                row.State.Channel,
                row.ReceivedAt,
                subject = row.Masked,
                row.Fingerprint,
                status = row.State.Status,
                row.State.ClosedReason,
                row.RespondedAt,
                policyVersions = sources.All.ToDictionary(s => s.Name, s => s.Policy.Version),
            }, Json),
            ["verification.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                required = row.State.Required,
                achieved = row.State.Achieved,
                factors = row.State.Factors.Order(),
                channelEvidence = row.ChannelEvidence,
            }, Json),
            ["deadline.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                receivedOn = DeadlineCalculator.LocalDate(row.ReceivedAt),
                row.DueOn,
                row.DueBy,
                row.Policy,
                respondedOn = row.RespondedAt is { } r ? DeadlineCalculator.LocalDate(r) : (DateOnly?)null,
                onTime = row.RespondedAt is { } r2 ? r2 <= row.DueBy : (bool?)null,
                complaintWindowEnds = DeadlineCalculator.ComplaintWindowEnds(row.ReceivedAt, row.RespondedAt),
            }, Json),
        };
        foreach (var (kind, source, content, _) in artifacts.Where(a => a.Content != "{}"))
        {
            var name = kind switch
            {
                "response-tr" => "response-tr.md",
                "response-en" => "response-en.md",
                "access-export-sha256" => "access-export.sha256",
                _ => $"{kind}-{source}.json",
            };
            files[name] = Encoding.UTF8.GetBytes(content);
        }

        return EvidencePack.Build(id, row.Reference, time.GetUtcNow(), audit, files, signer.Key);
    }

    public VerificationReport VerifyPack(Stream zip) => EvidencePack.Verify(zip, signer.KeyId);

    public Task<IReadOnlyList<AuditEvent>> AuditAsync(Guid id, CancellationToken ct) => store.AuditTrailAsync(id, ct);

    // ---------------------------------------------------------------- helpers

    private async Task<RequestRow> RequireAsync(Guid id, CancellationToken ct) =>
        await store.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Request {id} not found.");

    private async Task<Dictionary<string, DiscoveryResult>> DiscoveriesAsync(Guid id, CancellationToken ct) =>
        (await store.ArtifactsAsync(id, ct)).Where(a => a.Kind == "discovery")
            .ToDictionary(a => a.Source, a => JsonSerializer.Deserialize<DiscoveryResult>(a.Content, Json)!, StringComparer.OrdinalIgnoreCase);

    private async Task<List<SourcePlan>> PlansAsync(Guid id, CancellationToken ct) =>
        [.. (await store.ArtifactsAsync(id, ct)).Where(a => a.Kind == "plan" && a.Source.Length > 0)
            .Select(a => JsonSerializer.Deserialize<ErasurePlan>(a.Content, ErasurePlan.Json)!)
            .Select(p => new SourcePlan(p.Source, p, p.Hash()))
            .OrderBy(p => p.Source, StringComparer.Ordinal)];

    private async Task SaveArtifactAsync(Guid id, string kind, string source, string content, CancellationToken ct)
    {
        await using var conn = await workflowDb.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var sha = await WorkflowStore.SaveArtifactAsync(conn, tx, id, kind, source, content, ct);
        await tx.CommitAsync(ct);
        await store.AuditAsync(id, "system", $"{kind}.recorded", new { source, sha256 = sha }, ct);
    }

    internal static string CombinedHash(IReadOnlyList<SourcePlan> plans) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', plans.OrderBy(p => p.Source, StringComparer.Ordinal).Select(p => $"{p.Source}:{p.Sha256}")))));

    private static DiscoveryResult MergeDiscoveries(IEnumerable<DiscoveryResult> results)
    {
        var list = results.ToList();
        return list.Count == 1 ? list[0] : new DiscoveryResult(string.Join('+', list.Select(r => r.Source)),
            [.. list.SelectMany(r => r.Owned)], [.. list.SelectMany(r => r.Mentions)], [.. list.SelectMany(r => r.SearchedColumns)],
            [.. list.SelectMany(r => r.Unreachable)], [.. list.SelectMany(r => r.Warnings)], list.Sum(r => r.Queries), list.Sum(r => r.ElapsedMs));
    }
}

/// <summary>The evidence signing key: configured PEM, or an ephemeral key for development (logged as such).</summary>
internal sealed class EvidenceSigner : IDisposable
{
    public EvidenceSigner(IOptions<EvidenceOptions> options, ILogger<EvidenceSigner> logger)
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (string.IsNullOrWhiteSpace(options.Value.SigningKeyPem))
        {
            Ephemeral = true;
            logger.LogWarning("Evidence:SigningKeyPem is not set: evidence packs are signed with an ephemeral key {KeyId} that dies with this process. Development only.", EvidencePack.KeyId(Key));
        }
        else
        {
            Key.ImportFromPem(options.Value.SigningKeyPem);
        }

        KeyId = EvidencePack.KeyId(Key);
    }

    public ECDsa Key { get; }

    public string KeyId { get; }

    public bool Ephemeral { get; }

    public void Dispose() => Key.Dispose();
}
