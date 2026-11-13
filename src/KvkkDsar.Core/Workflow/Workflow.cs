using KvkkDsar.Verification;

namespace KvkkDsar.Workflow;

/// <summary>The rights of KVKK Art. 11(1), grouped by how they are fulfilled.</summary>
public enum RequestType
{
    /// <summary>(a) whether data is processed, (b) information about it, (c) purposes, (ç) recipients. Fulfilled by an export.</summary>
    Access,

    /// <summary>(e) erasure or destruction under Art. 7, with (f) notification of recipients. Fulfilled by an approved plan.</summary>
    Erasure,

    /// <summary>(d) rectification, with (f). Tracked against the deadline; corrections are made by a person.</summary>
    Rectification,

    /// <summary>(g) objection to a result of automated analysis. Tracked; decided by a person.</summary>
    Objection,

    /// <summary>(ğ) compensation for unlawful processing. Tracked; handled by legal.</summary>
    Compensation,
}

/// <summary>Application channels accepted by the Communiqué on Applications to the Data Controller (Art. 5).</summary>
public enum Channel
{
    Written,
    Kep,
    SecureESignature,
    MobileSignature,
    RegisteredEmail,
    WebPortal,
}

public enum RequestStatus
{
    Received,
    AwaitingVerification,
    ManualReview,
    Verified,
    Discovered,
    Planned,
    Approved,
    Executing,
    Executed,
    Responded,
    Rejected,
    Withdrawn,
}

/// <summary>The workflow-relevant facts of one request. Identifiers and data live elsewhere.</summary>
public sealed record DsarState
{
    public required Guid Id { get; init; }

    public required IReadOnlyList<RequestType> Types { get; init; }

    public required Channel Channel { get; init; }

    public RequestStatus Status { get; init; } = RequestStatus.Received;

    public Assurance Required { get; init; }

    public Assurance Achieved { get; init; }

    public IReadOnlySet<VerificationFactor> Factors { get; init; } = new HashSet<VerificationFactor>();

    public bool Discovered { get; init; }

    public bool AccessExportProduced { get; init; }

    public string? PlanHash { get; init; }

    public string? PlannedBy { get; init; }

    public string? ApprovedBy { get; init; }

    public string? ExecutedBy { get; init; }

    public string? ClosedReason { get; init; }

    public bool IsTerminal => Status is RequestStatus.Responded or RequestStatus.Rejected or RequestStatus.Withdrawn;

    public bool IsVerified => Achieved >= Required && Status is not (RequestStatus.Received or RequestStatus.AwaitingVerification or RequestStatus.ManualReview);

    public bool Wants(RequestType type) => Types.Contains(type);

    public static DsarState New(Guid id, IReadOnlyList<RequestType> types, Channel channel) => new()
    {
        Id = id,
        Types = types,
        Channel = channel,
        Required = VerificationPolicy.RequiredFor(types),
        // A qualified channel counts once an operator records the signature check (AddFactor QualifiedChannel).
        Achieved = Assurance.Low,
        Status = VerificationPolicy.RequiredFor(types) <= Assurance.Low ? RequestStatus.Verified : RequestStatus.Received,
    };
}

public abstract record WorkflowCommand(string Actor);

public sealed record AddFactor(string Actor, VerificationFactor Factor) : WorkflowCommand(Actor);

public sealed record ChallengeSent(string Actor) : WorkflowCommand(Actor);

public sealed record VerificationFailed(string Actor, string Reason) : WorkflowCommand(Actor);

public sealed record RecordDiscovery(string Actor) : WorkflowCommand(Actor);

public sealed record RecordAccessExport(string Actor) : WorkflowCommand(Actor);

public sealed record RecordPlan(string Actor, string PlanHash) : WorkflowCommand(Actor);

public sealed record Approve(string Actor, string PlanHash) : WorkflowCommand(Actor);

public sealed record BeginExecution(string Actor) : WorkflowCommand(Actor);

public sealed record CompleteExecution(string Actor, bool PostCheckClean) : WorkflowCommand(Actor);

/// <summary>The source changed between planning and execution; the plan is void and must be redone.</summary>
public sealed record PlanDrifted(string Actor) : WorkflowCommand(Actor);

public sealed record Respond(string Actor) : WorkflowCommand(Actor);

public sealed record Reject(string Actor, string Reason) : WorkflowCommand(Actor);

public sealed record Withdraw(string Actor) : WorkflowCommand(Actor);

/// <summary>A command that the request's current state does not allow; maps to HTTP 409.</summary>
public sealed class WorkflowException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The request state machine as a pure function, so its invariants can be property-tested without a database:
/// nothing is erased without a verified identity, a discovery, a plan and an approval by a second person of
/// exactly that plan; an access export is taken before anything is erased; terminal states are final.
/// </summary>
public static class DsarWorkflow
{
    public static (DsarState State, string Event) Apply(DsarState state, WorkflowCommand command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Actor))
        {
            throw new WorkflowException("actor_required", "Every workflow step needs an identified actor.");
        }

        if (state.IsTerminal)
        {
            throw new WorkflowException("terminal", $"The request is {state.Status}; no further steps are possible.");
        }

        return command switch
        {
            AddFactor f => AddFactorTo(state, f),
            ChallengeSent => (Require(state, "challenge_sent", RequestStatus.Received, RequestStatus.AwaitingVerification) with { Status = RequestStatus.AwaitingVerification }, "verification.challenge_sent"),
            VerificationFailed => (Unverified(state, "verification_failed") with { Status = RequestStatus.ManualReview }, "verification.manual_review"),
            RecordDiscovery => (Verified(state, "discovery") with
            {
                Status = RequestStatus.Discovered,
                Discovered = true,
                PlanHash = null,
                PlannedBy = null,
                ApprovedBy = null,
            }, "discovery.recorded"),
            RecordAccessExport => (AccessExport(state), "access.exported"),
            RecordPlan p => (Plan(state, p), "plan.recorded"),
            Approve a => (ApprovePlan(state, a), "plan.approved"),
            BeginExecution b => (Begin(state, b), "execution.started"),
            CompleteExecution c => (Complete(state, c), c.PostCheckClean ? "execution.completed" : "execution.residual_found"),
            PlanDrifted => (Require(state, "plan_drift", RequestStatus.Executing) with
            {
                Status = RequestStatus.Discovered,
                PlanHash = null,
                PlannedBy = null,
                ApprovedBy = null,
            }, "execution.plan_drift"),
            Respond r => (RespondTo(state, r), "request.responded"),
            Reject r => (state with { Status = RequestStatus.Rejected, ClosedReason = r.Reason }, "request.rejected"),
            Withdraw => (state with { Status = RequestStatus.Withdrawn, ClosedReason = "withdrawn by the data subject" }, "request.withdrawn"),
            _ => throw new WorkflowException("unknown_command", command.GetType().Name),
        };
    }

    private static (DsarState, string) AddFactorTo(DsarState state, AddFactor f)
    {
        Unverified(state, "add_factor");
        var factors = new HashSet<VerificationFactor>(state.Factors) { f.Factor };
        var achieved = VerificationPolicy.Evaluate(state.Channel, factors);
        var next = state with { Factors = factors, Achieved = achieved };
        return achieved >= state.Required
            ? (next with { Status = RequestStatus.Verified }, "verification.verified")
            : (next, "verification.factor_added");
    }

    private static DsarState AccessExport(DsarState state)
    {
        if (!state.Wants(RequestType.Access))
        {
            throw new WorkflowException("not_access", "This request does not ask for access.");
        }

        if (!state.Discovered || state.Status is RequestStatus.Executing or RequestStatus.Executed)
        {
            throw new WorkflowException("export_order", "The access export is taken after discovery and before any erasure.");
        }

        return state with { AccessExportProduced = true };
    }

    private static DsarState Plan(DsarState state, RecordPlan p)
    {
        if (!state.Wants(RequestType.Erasure))
        {
            throw new WorkflowException("not_erasure", "Only erasure requests have an execution plan.");
        }

        Require(state, "plan", RequestStatus.Discovered, RequestStatus.Planned);
        return state with { Status = RequestStatus.Planned, PlanHash = p.PlanHash, PlannedBy = p.Actor, ApprovedBy = null };
    }

    private static DsarState ApprovePlan(DsarState state, Approve a)
    {
        Require(state, "approve", RequestStatus.Planned);
        if (!string.Equals(a.PlanHash, state.PlanHash, StringComparison.Ordinal))
        {
            throw new WorkflowException("plan_mismatch", "The approval names a different plan than the current one; review the current plan.");
        }

        if (string.Equals(a.Actor, state.PlannedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowException("four_eyes", "The person who prepared the plan cannot approve it.");
        }

        return state with { Status = RequestStatus.Approved, ApprovedBy = a.Actor };
    }

    private static DsarState Begin(DsarState state, BeginExecution b)
    {
        Require(state, "execute", RequestStatus.Approved);
        if (state.Wants(RequestType.Access) && !state.AccessExportProduced)
        {
            throw new WorkflowException("export_first", "Produce the access export before erasing: afterwards the data is gone.");
        }

        return state with { Status = RequestStatus.Executing, ExecutedBy = b.Actor };
    }

    private static DsarState Complete(DsarState state, CompleteExecution c)
    {
        Require(state, "complete", RequestStatus.Executing);
        // Residual data after execution means the plan or the source is wrong: back to discovery, never "done".
        return c.PostCheckClean
            ? state with { Status = RequestStatus.Executed }
            : state with { Status = RequestStatus.Discovered, PlanHash = null, PlannedBy = null, ApprovedBy = null };
    }

    private static DsarState RespondTo(DsarState state, Respond r)
    {
        Verified(state, "respond");
        if (state.Wants(RequestType.Erasure) && state.Status != RequestStatus.Executed)
        {
            throw new WorkflowException("not_executed", "An erasure request is answered after the approved plan has been executed and checked.");
        }

        if (state.Wants(RequestType.Access) && !state.AccessExportProduced)
        {
            throw new WorkflowException("no_export", "An access request is answered with its export; produce it first.");
        }

        if (state.Status is RequestStatus.Executing or RequestStatus.Planned or RequestStatus.Approved)
        {
            throw new WorkflowException("in_progress", $"The request is {state.Status}; finish or reject it first.");
        }

        return state with { Status = RequestStatus.Responded, ClosedReason = null };
    }

    private static DsarState Verified(DsarState state, string step)
    {
        if (!state.IsVerified)
        {
            throw new WorkflowException("not_verified", $"Identity is not verified to the level this request needs ({state.Required}); cannot {step}.");
        }

        return state;
    }

    private static DsarState Unverified(DsarState state, string step)
    {
        if (state.Status is not (RequestStatus.Received or RequestStatus.AwaitingVerification or RequestStatus.ManualReview))
        {
            throw new WorkflowException("already_verified", $"Identity is already verified; cannot {step}.");
        }

        return state;
    }

    private static DsarState Require(DsarState state, string step, params RequestStatus[] allowed)
    {
        if (!allowed.Contains(state.Status))
        {
            throw new WorkflowException("invalid_state", $"Cannot {step.Replace('_', ' ')} while the request is {state.Status}.");
        }

        return state;
    }
}
