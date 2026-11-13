using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KvkkDsar.Verification;
using KvkkDsar.Workflow;

namespace KvkkDsar.UnitTests;

public sealed class WorkflowTests
{
    private static DsarState Erasure(Channel channel = Channel.WebPortal, params RequestType[] extra) =>
        DsarState.New(Guid.NewGuid(), [RequestType.Erasure, .. extra], channel);

    private static DsarState Run(DsarState s, params WorkflowCommand[] commands)
    {
        foreach (var c in commands)
        {
            s = DsarWorkflow.Apply(s, c).State;
        }

        return s;
    }

    [Fact]
    public void Erasure_happy_path_needs_two_people()
    {
        var s = Run(Erasure(),
            new ChallengeSent("intake"),
            new AddFactor("system", VerificationFactor.OtpToContactOnFile),
            new AddFactor("system", VerificationFactor.KnowledgeMatch),
            new RecordDiscovery("dpo"),
            new RecordPlan("dpo", "h1"),
            new Approve("legal", "h1"),
            new BeginExecution("ops"),
            new CompleteExecution("ops", PostCheckClean: true),
            new Respond("dpo"));

        s.Status.Should().Be(RequestStatus.Responded);
        s.Achieved.Should().Be(Assurance.High);
        s.PlannedBy.Should().Be("dpo");
        s.ApprovedBy.Should().Be("legal");
    }

    [Fact]
    public void Planner_cannot_approve_own_plan()
    {
        var s = Run(Erasure(Channel.Kep), new AddFactor("intake", VerificationFactor.QualifiedChannel), new RecordDiscovery("dpo"), new RecordPlan("dpo", "h1"));
        FluentActions.Invoking(() => DsarWorkflow.Apply(s, new Approve("DPO", "h1"))).Should().Throw<WorkflowException>().Which.Code.Should().Be("four_eyes");
        FluentActions.Invoking(() => DsarWorkflow.Apply(s, new Approve("legal", "other"))).Should().Throw<WorkflowException>().Which.Code.Should().Be("plan_mismatch");
    }

    [Fact]
    public void Otp_alone_is_not_enough_for_erasure_but_is_for_access()
    {
        var erasure = Run(Erasure(), new AddFactor("s", VerificationFactor.OtpToContactOnFile));
        erasure.Status.Should().Be(RequestStatus.Received);
        erasure.Achieved.Should().Be(Assurance.Substantial);

        var access = Run(DsarState.New(Guid.NewGuid(), [RequestType.Access], Channel.WebPortal), new AddFactor("s", VerificationFactor.OtpToContactOnFile));
        access.Status.Should().Be(RequestStatus.Verified);
    }

    [Fact]
    public void Qualified_channel_needs_no_otp()
    {
        var s = Run(Erasure(Channel.SecureESignature), new AddFactor("intake", VerificationFactor.QualifiedChannel));
        s.Status.Should().Be(RequestStatus.Verified);
        s.Achieved.Should().Be(Assurance.High);

        // A "qualified channel" factor claimed for a web form changes nothing.
        Run(Erasure(Channel.WebPortal), new AddFactor("intake", VerificationFactor.QualifiedChannel)).Status.Should().Be(RequestStatus.Received);
    }

    [Fact]
    public void Low_assurance_requests_start_verified()
    {
        DsarState.New(Guid.NewGuid(), [RequestType.Objection], Channel.WebPortal).Status.Should().Be(RequestStatus.Verified);
    }

    [Fact]
    public void Access_export_must_come_before_erasure_in_a_combined_request()
    {
        var s = Run(Erasure(Channel.Kep, RequestType.Access), new AddFactor("i", VerificationFactor.QualifiedChannel), new RecordDiscovery("dpo"),
            new RecordPlan("dpo", "h"), new Approve("legal", "h"));
        FluentActions.Invoking(() => DsarWorkflow.Apply(s, new BeginExecution("ops"))).Should().Throw<WorkflowException>().Which.Code.Should().Be("export_first");
        Run(s, new RecordAccessExport("dpo"), new BeginExecution("ops")).Status.Should().Be(RequestStatus.Executing);
    }

    [Fact]
    public void Drift_and_residuals_send_the_request_back_to_discovery()
    {
        var approved = Run(Erasure(Channel.Kep), new AddFactor("i", VerificationFactor.QualifiedChannel), new RecordDiscovery("dpo"),
            new RecordPlan("dpo", "h"), new Approve("legal", "h"), new BeginExecution("ops"));
        var drift = Run(approved, new PlanDrifted("ops"));
        drift.Status.Should().Be(RequestStatus.Discovered);
        drift.PlanHash.Should().BeNull();
        Run(approved, new CompleteExecution("ops", PostCheckClean: false)).Status.Should().Be(RequestStatus.Discovered);
    }

    [Fact]
    public void Terminal_states_are_final_and_steps_need_an_actor()
    {
        var rejected = Run(Erasure(), new Reject("dpo", "not the data subject"));
        FluentActions.Invoking(() => DsarWorkflow.Apply(rejected, new Withdraw("x"))).Should().Throw<WorkflowException>().Which.Code.Should().Be("terminal");
        FluentActions.Invoking(() => DsarWorkflow.Apply(Erasure(), new ChallengeSent(" "))).Should().Throw<WorkflowException>().Which.Code.Should().Be("actor_required");
    }

    [Theory]
    [InlineData(Channel.Kep, new[] { VerificationFactor.QualifiedChannel }, Assurance.High)]
    [InlineData(Channel.WebPortal, new[] { VerificationFactor.OtpToContactOnFile }, Assurance.Substantial)]
    [InlineData(Channel.WebPortal, new[] { VerificationFactor.OtpToContactOnFile, VerificationFactor.KnowledgeMatch }, Assurance.High)]
    [InlineData(Channel.RegisteredEmail, new[] { VerificationFactor.RegisteredEmail }, Assurance.Substantial)]
    [InlineData(Channel.Written, new[] { VerificationFactor.SignedLetter, VerificationFactor.KnowledgeMatch }, Assurance.High)]
    [InlineData(Channel.WebPortal, new[] { VerificationFactor.KnowledgeMatch }, Assurance.Low)]
    public void Assurance_levels(Channel channel, VerificationFactor[] factors, Assurance expected) =>
        VerificationPolicy.Evaluate(channel, factors.ToHashSet()).Should().Be(expected);

    private static readonly string[] Actors = ["alice", "bob", "carol"];

    private static Gen<WorkflowCommand> CommandGen() =>
        from kind in Gen.Choose(0, 12)
        from actor in Gen.Elements(Actors)
        from hash in Gen.Elements("h1", "h2")
        from factor in Gen.Elements(Enum.GetValues<VerificationFactor>())
        from clean in Gen.Elements(true, false)
        select kind switch
        {
            0 => (WorkflowCommand)new AddFactor(actor, factor),
            1 => new ChallengeSent(actor),
            2 => new RecordDiscovery(actor),
            3 => new RecordPlan(actor, hash),
            4 => new Approve(actor, hash),
            5 => new BeginExecution(actor),
            6 => new CompleteExecution(actor, clean),
            7 => new PlanDrifted(actor),
            8 => new Respond(actor),
            9 => new RecordAccessExport(actor),
            10 => new VerificationFailed(actor, "x"),
            11 => new Reject(actor, "x"),
            _ => new AddFactor(actor, VerificationFactor.KnowledgeMatch),
        };

    /// <summary>
    /// Whatever sequence of commands arrives, in whatever order, nothing is executed or answered unless every gate was
    /// passed: verified identity, a discovery, a plan approved by a different person than its author, and (for
    /// combined requests) the access export first.
    /// </summary>
    [Property(MaxTest = 2000)]
    public Property No_command_sequence_bypasses_the_gates() =>
        Prop.ForAll(Gen.ListOf(CommandGen()).ToArbitrary(), Gen.Elements(Enum.GetValues<Channel>()).ToArbitrary(), Gen.Elements(true, false).ToArbitrary(), (commands, channel, withAccess) =>
        {
            RequestType[] types = withAccess ? [RequestType.Erasure, RequestType.Access] : [RequestType.Erasure];
            var s = DsarState.New(Guid.NewGuid(), types, channel);
            var executedOnce = false;
            foreach (var c in commands)
            {
                try
                {
                    s = DsarWorkflow.Apply(s, c).State;
                }
                catch (WorkflowException)
                {
                    continue;
                }

                if (s.Status is RequestStatus.Executing)
                {
                    if (!s.IsVerified || !s.Discovered || s.ApprovedBy is null
                        || string.Equals(s.ApprovedBy, s.PlannedBy, StringComparison.OrdinalIgnoreCase)
                        || (withAccess && !s.AccessExportProduced))
                    {
                        return false;
                    }
                }

                executedOnce |= s.Status == RequestStatus.Executed;
                if (s.Status == RequestStatus.Responded && !executedOnce)
                {
                    return false;
                }
            }

            return true;
        });
}

public sealed class OtpTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    private OtpService Service() => new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), _clock);

    [Fact]
    public void Correct_code_is_accepted_and_the_store_never_holds_it()
    {
        var id = Guid.NewGuid();
        var (code, challenge) = Service().Issue(id, "******2233");
        code.Should().MatchRegex("^[0-9]{6}$");
        challenge.CodeHash.Should().NotContain(code);
        Service().Check(id, challenge, code).Outcome.Should().Be(OtpOutcome.Accepted);
    }

    [Fact]
    public void A_code_for_one_request_does_not_work_for_another()
    {
        var (code, challenge) = Service().Issue(Guid.NewGuid(), "x");
        Service().Check(Guid.NewGuid(), challenge, code).Outcome.Should().Be(OtpOutcome.Wrong);
    }

    [Fact]
    public void Five_wrong_guesses_lock_the_challenge_even_for_the_right_code()
    {
        var id = Guid.NewGuid();
        var (code, challenge) = Service().Issue(id, "x");
        var wrong = code == "000000" ? "000001" : "000000";
        OtpOutcome outcome = default;
        for (var i = 0; i < OtpService.MaxAttempts; i++)
        {
            (outcome, challenge) = Service().Check(id, challenge, wrong);
        }

        outcome.Should().Be(OtpOutcome.LockedOut);
        Service().Check(id, challenge, code).Outcome.Should().Be(OtpOutcome.LockedOut);
    }

    [Fact]
    public void Expired_code_is_refused()
    {
        var id = Guid.NewGuid();
        var (code, challenge) = Service().Issue(id, "x");
        _clock.Now += OtpService.Lifetime + TimeSpan.FromSeconds(1);
        Service().Check(id, challenge, code).Outcome.Should().Be(OtpOutcome.Expired);
    }
}
