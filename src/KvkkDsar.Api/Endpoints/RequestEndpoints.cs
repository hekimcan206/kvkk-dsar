using KvkkDsar.Api.Infrastructure;
using KvkkDsar.Api.Persistence;
using KvkkDsar.Api.Services;
using KvkkDsar.Deadlines;
using KvkkDsar.Postgres;
using KvkkDsar.Verification;
using KvkkDsar.Workflow;

namespace KvkkDsar.Api.Endpoints;

public sealed record CreateRequestBody(
    IReadOnlyList<RequestType> Types, Channel Channel, DateTimeOffset? ReceivedAt, string? ChannelEvidence, string? SenderEmail,
    SubjectBody Subject);

public sealed record SubjectBody(string? NationalId, string? Email, string? Phone, string? FirstName, string? LastName, DateOnly? BirthDate);

public sealed record ConfirmBody(string Code);

public sealed record ChannelFactorBody(VerificationFactor Factor, string? Evidence);

public sealed record ApproveBody(string PlanHash);

public sealed record RejectBody(string Reason);

public sealed record RequestView(
    Guid Id, string Reference, IReadOnlyList<RequestType> Types, Channel Channel, RequestStatus Status, Assurance RequiredAssurance,
    Assurance Assurance, IReadOnlyList<VerificationFactor> Factors, DateTimeOffset ReceivedAt, DateOnly DueOn, DeadlinePolicy DeadlinePolicy,
    DeadlineStage DeadlineStage, int DaysLeft, IReadOnlyDictionary<string, string> Subject, string? PlanHash, string? PlannedBy,
    string? ApprovedBy, bool IdentifiersPurged, string? ClosedReason);

internal static class RequestEndpoints
{
    public static RouteGroupBuilder MapRequestEndpoints(this RouteGroupBuilder api)
    {
        var requests = api.MapGroup("/requests").WithTags("Requests");

        requests.MapPost("/", async (CreateRequestBody body, DsarService service, HttpContext http, CancellationToken ct) =>
        {
            var s = body.Subject ?? new SubjectBody(null, null, null, null, null, null);
            var row = await service.CreateAsync(new CreateCommand(body.Types ?? [], body.Channel, body.ReceivedAt, body.ChannelEvidence, body.SenderEmail,
                s.NationalId, s.Email, s.Phone, s.FirstName, s.LastName, s.BirthDate), http.Operator().Name, ct);
            return Results.Created($"/api/v1/requests/{row.Id}", View(row, TimeProvider.System));
        }).RequireRole(Roles.Intake).WithSummary("Register a data-subject application (KVKK Art. 11/13); starts the 30-day clock.");

        requests.MapGet("/", async (bool? open, WorkflowStore store, TimeProvider time, CancellationToken ct) =>
            (await store.ListAsync(open ?? true, 500, ct)).Select(r => View(r, time)))
            .RequireRole(Roles.Intake, Roles.Dpo, Roles.Auditor).WithSummary("Requests ordered by deadline (open ones by default).");

        requests.MapGet("/{id:guid}", async (Guid id, DsarService service, TimeProvider time, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } row ? Results.Ok(View(row, time)) : Results.NotFound())
            .RequireRole(Roles.Intake, Roles.Verifier, Roles.Dpo, Roles.Approver, Roles.Executor, Roles.Auditor);

        requests.MapPost("/{id:guid}/verification/challenge", async (Guid id, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
        {
            var (row, sentTo) = await service.ChallengeAsync(id, http.Operator().Name, ct);
            return sentTo is null
                ? Results.Conflict(new { status = row.State.Status, detail = "No contact on file for this identifier; verify with a signed letter or a qualified channel." })
                : Results.Ok(new { sentTo, expiresInSeconds = (int)OtpService.Lifetime.TotalSeconds, request = View(row, time) });
        }).RequireRole(Roles.Verifier).WithSummary("Send a one-time code to a phone or e-mail already on file (never to one only given in the application).");

        requests.MapPost("/{id:guid}/verification/confirm", async (Guid id, ConfirmBody body, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
        {
            var (row, outcome, left) = await service.ConfirmAsync(id, body.Code, http.Operator().Name, ct);
            return Results.Ok(new { outcome, attemptsLeft = left, request = View(row, time) });
        }).RequireRole(Roles.Verifier);

        requests.MapPost("/{id:guid}/verification/channel", async (Guid id, ChannelFactorBody body, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
            Results.Ok(View(await service.VerifyChannelAsync(id, body.Factor, body.Evidence, http.Operator().Name, ct), time)))
            .RequireRole(Roles.Verifier).WithSummary("Record a checked qualified signature / KEP, signed letter, or registered sender e-mail.");

        requests.MapPost("/{id:guid}/discovery", async (Guid id, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
        {
            var (row, results) = await service.DiscoverAsync(id, http.Operator().Name, ct);
            return Results.Ok(new
            {
                request = View(row, time),
                sources = results.Select(r => new
                {
                    r.Source,
                    owned = r.OwnedCountByTable(),
                    mentions = r.Mentions.Count,
                    unreachable = r.Unreachable,
                    r.Warnings,
                    r.Queries,
                    elapsedMs = Math.Round(r.ElapsedMs, 1),
                    fingerprint = r.Fingerprint(),
                }),
            });
        }).RequireRole(Roles.Dpo).WithSummary("Find the subject's data in every source via the inventory and foreign keys (row addresses only).");

        requests.MapPost("/{id:guid}/plan", async (Guid id, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
        {
            var (row, plans, hash) = await service.PlanAsync(id, http.Operator().Name, ct);
            return Results.Ok(new { request = View(row, time), planHash = hash, sources = plans.Select(p => new { p.Source, counts = p.Plan.Counts(), p.Plan.NotifyRecipients, p.Plan.DeleteOrder, p.Plan.Actions }) });
        }).RequireRole(Roles.Dpo).WithSummary("Plan erasure, anonymisation and retention per row under the policy.");

        requests.MapPost("/{id:guid}/approve", async (Guid id, ApproveBody body, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
            Results.Ok(View(await service.ApproveAsync(id, body.PlanHash, http.Operator().Name, ct), time)))
            .RequireRole(Roles.Approver).WithSummary("Approve exactly the plan with this hash; the planner cannot approve.");

        requests.MapPost("/{id:guid}/execute", async (Guid id, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
        {
            var (row, results) = await service.ExecuteAsync(id, http.Operator().Name, ct);
            return Results.Ok(new { request = View(row, time), results });
        }).RequireRole(Roles.Executor).WithSummary("Apply the approved plan (one transaction per source, drift-checked), then post-check. Re-run to resume.");

        requests.MapGet("/{id:guid}/export", async (Guid id, DsarService service, HttpContext http, CancellationToken ct) =>
        {
            var (row, export, sha) = await service.ExportAsync(id, http.Operator().Name, ct);
            http.Response.Headers["X-Export-Sha256"] = sha;
            return Results.File(export, "application/json", $"{row.Reference}-access-export.json");
        }).RequireRole(Roles.Dpo).WithSummary("The subject's data for an access request; generated from the source, never stored (only its hash).");

        requests.MapPost("/{id:guid}/respond", async (Guid id, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
        {
            var (row, tr, en) = await service.RespondAsync(id, http.Operator().Name, ct);
            return Results.Ok(new { request = View(row, time), letterTr = tr, letterEn = en });
        }).RequireRole(Roles.Dpo).WithSummary("Write the answer (TR/EN), close the request and purge its identifiers.");

        requests.MapPost("/{id:guid}/reject", async (Guid id, RejectBody body, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
            Results.Ok(View(await service.RejectAsync(id, body.Reason, http.Operator().Name, ct), time))).RequireRole(Roles.Dpo);

        requests.MapPost("/{id:guid}/withdraw", async (Guid id, DsarService service, HttpContext http, TimeProvider time, CancellationToken ct) =>
            Results.Ok(View(await service.WithdrawAsync(id, http.Operator().Name, ct), time))).RequireRole(Roles.Intake, Roles.Dpo);

        requests.MapGet("/{id:guid}/audit", async (Guid id, DsarService service, CancellationToken ct) => Results.Ok(await service.AuditAsync(id, ct)))
            .RequireRole(Roles.Dpo, Roles.Auditor);

        requests.MapGet("/{id:guid}/evidence", async (Guid id, WorkflowStore store, DsarService service, CancellationToken ct) =>
        {
            var row = await store.GetAsync(id, ct);
            return row is null ? Results.NotFound() : Results.File(await service.EvidenceAsync(id, ct), "application/zip", $"{row.Reference}-evidence.zip");
        }).RequireRole(Roles.Dpo, Roles.Auditor).WithSummary("Signed evidence pack: records, plan, execution log, post-check, letters, audit chain.");

        api.MapPost("/evidence/verify", async (HttpRequest request, DsarService service, CancellationToken ct) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return Results.Ok(service.VerifyPack(buffer));
        }).RequireRole(Roles.Dpo, Roles.Auditor).WithTags("Evidence").WithSummary("Verify an evidence pack (application/zip body) against this service's signing key.");

        api.MapGet("/deadlines", async (DeadlineMonitor monitor, CancellationToken ct) => Results.Ok(await monitor.ScanAsync(ct)))
            .RequireRole(Roles.Dpo, Roles.Auditor).WithTags("Deadlines").WithSummary("Open requests by deadline stage (also refreshes stages now).");

        api.MapGet("/sources/{name}/index-advice", async (string name, SourceRegistry registry, CancellationToken ct) =>
        {
            var source = registry.Get(name);
            var engine = await source.EngineAsync(ct);
            return Results.Ok(await IndexAdvisor.AdviseAsync(source.Finder, engine.Catalog, engine.Keys, cancellationToken: ct));
        }).RequireRole(Roles.Dpo).WithTags("Sources").WithSummary("Discovery queries that would scan a whole large table, with the index that fixes each.");

        api.MapGet("/sources/{name}/search-plan", async (string name, SourceRegistry registry, CancellationToken ct) =>
        {
            var source = registry.Get(name);
            var engine = await source.EngineAsync(ct);
            return Results.Ok(new { keys = engine.Keys.Select(k => new { k.Table, k.Column, k.Kind, k.Mode, k.Role }), policyVersion = source.Policy.Version });
        }).RequireRole(Roles.Dpo, Roles.Auditor).WithTags("Sources");

        return api;
    }

    public static IEndpointRouteBuilder MapDemoInbox(this IEndpointRouteBuilder app)
    {
        app.MapGet("/demo/inbox/{id:guid}", (Guid id, IOtpSender sender) =>
            sender is DemoInbox inbox && inbox.Read(id) is { } m ? Results.Ok(new { to = m.To, code = m.Code }) : Results.NotFound())
            .ExcludeFromDescription();
        return app;
    }

    internal static RequestView View(RequestRow r, TimeProvider time) => new(
        r.Id, r.Reference, r.State.Types, r.State.Channel, r.State.Status, r.State.Required, r.State.Achieved, [.. r.State.Factors.Order()],
        r.ReceivedAt, r.DueOn, r.Policy, r.State.IsTerminal ? r.Stage : DeadlineCalculator.Stage(r.Deadline, time.GetUtcNow()),
        DeadlineCalculator.DaysLeft(r.Deadline, time.GetUtcNow()), r.Masked, r.State.PlanHash, r.State.PlannedBy, r.State.ApprovedBy,
        r.Sealed is null, r.State.ClosedReason);
}
