using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KvkkDsar.Deadlines;
using KvkkDsar.Evidence;
using KvkkDsar.Verification;
using KvkkDsar.Workflow;
using Npgsql;
using NpgsqlTypes;

namespace KvkkDsar.Api.Persistence;

internal sealed record RequestRow(
    Guid Id,
    string Reference,
    DsarState State,
    int Version,
    DateTimeOffset ReceivedAt,
    DateOnly DueOn,
    DateTimeOffset DueBy,
    DeadlinePolicy Policy,
    DeadlineStage Stage,
    string Fingerprint,
    IReadOnlyDictionary<string, string> Masked,
    string? SubjectName,
    byte[]? Sealed,
    string? ChannelEvidence,
    string CreatedBy,
    DateTimeOffset? RespondedAt)
{
    public Deadline Deadline => new(DeadlineCalculator.LocalDate(ReceivedAt), DueOn, DueOn, DueBy, Policy, null);
}

internal sealed record NewRequest(
    Guid Id,
    DsarState State,
    DateTimeOffset ReceivedAt,
    Deadline Deadline,
    string Fingerprint,
    IReadOnlyDictionary<string, string> Masked,
    string SubjectName,
    byte[] Sealed,
    string? ChannelEvidence,
    string CreatedBy);

/// <summary>
/// PostgreSQL store for requests, artifacts, OTP challenges and the audit chain. Every state change runs the pure
/// <see cref="DsarWorkflow"/> under a row lock (<c>SELECT … FOR UPDATE</c>), so two operators acting on one request are
/// serialised: the second one sees the new state and gets a 409, and the audit chain stays strictly ordered.
/// </summary>
internal sealed class WorkflowStore(NpgsqlDataSource dataSource, TimeProvider time)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Columns = """
        id, reference, state::text, version, received_at, due_on, due_by, deadline_policy, deadline_stage, subject_fingerprint,
        subject_masked::text, subject_name, subject_sealed, channel_evidence, created_by, responded_at
        """;

    public async Task<RequestRow> CreateAsync(NewRequest request, string eventData, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO dsar.requests (id, reference, types, channel, status, state, received_at, due_on, due_by, deadline_policy,
                                       subject_fingerprint, subject_masked, subject_name, subject_sealed, channel_evidence, created_by)
            VALUES ($1, 'DSAR-' || to_char($2 AT TIME ZONE 'Europe/Istanbul', 'YYYY') || '-' || lpad(nextval('dsar.request_reference_seq')::text, 6, '0'),
                    $3, $4, $5, $6::jsonb, $2, $7, $8, $9, $10, $11::jsonb, $12, $13, $14, $15)
            RETURNING {Columns}
            """, connection, tx))
        {
            cmd.Parameters.Add(new() { Value = request.Id });
            cmd.Parameters.Add(new() { Value = request.ReceivedAt.ToUniversalTime() });
            cmd.Parameters.Add(new() { Value = request.State.Types.Select(t => t.ToString()).ToArray() });
            cmd.Parameters.Add(new() { Value = request.State.Channel.ToString() });
            cmd.Parameters.Add(new() { Value = request.State.Status.ToString() });
            cmd.Parameters.Add(new() { Value = Serialize(request.State) });
            cmd.Parameters.Add(new() { Value = request.Deadline.DueOn });
            cmd.Parameters.Add(new() { Value = request.Deadline.DueBy.ToUniversalTime() });
            cmd.Parameters.Add(new() { Value = request.Deadline.Policy.ToString() });
            cmd.Parameters.Add(new() { Value = request.Fingerprint });
            cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(request.Masked, Json) });
            cmd.Parameters.Add(new() { Value = request.SubjectName });
            cmd.Parameters.Add(new() { Value = request.Sealed, NpgsqlDbType = NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new() { Value = (object?)request.ChannelEvidence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = request.CreatedBy });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var row = Read(reader);
            await reader.DisposeAsync();
            await AppendAsync(connection, tx, row.Id, request.CreatedBy, "request.received", eventData, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return row;
        }
    }

    public async Task<RequestRow?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Columns} FROM dsar.requests WHERE id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<RequestRow>> ListAsync(bool openOnly, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {Columns} FROM dsar.requests WHERE ($1 = false OR closed_at IS NULL) ORDER BY due_by, reference LIMIT $2");
        cmd.Parameters.Add(new() { Value = openOnly });
        cmd.Parameters.Add(new() { Value = limit });
        var rows = new List<RequestRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    /// <summary>
    /// Applies a workflow command under the row lock. <paramref name="within"/> runs in the same transaction after the
    /// command was accepted (artifacts, OTP rows); a <see cref="WorkflowException"/> anywhere rolls everything back.
    /// </summary>
    public async Task<(RequestRow Row, string Event)> MutateAsync(
        Guid id, WorkflowCommand command, Func<RequestRow, object?>? eventData, CancellationToken cancellationToken,
        Func<NpgsqlConnection, NpgsqlTransaction, RequestRow, Task>? within = null, byte[]? newSealed = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LockAsync(connection, tx, id, cancellationToken) ?? throw new KeyNotFoundException($"Request {id} not found.");
        var (state, @event) = DsarWorkflow.Apply(row.State, command);
        if (within is not null)
        {
            await within(connection, tx, row);
        }

        var closing = state.IsTerminal;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE dsar.requests
            SET status = $2, state = $3::jsonb, version = version + 1,
                responded_at = CASE WHEN $2 = 'Responded' THEN $4 ELSE responded_at END,
                closed_at = CASE WHEN $5 THEN $4 ELSE closed_at END,
                -- Identifiers are kept only while the request is open.
                subject_sealed = CASE WHEN $5 THEN NULL WHEN $6::bytea IS NOT NULL THEN $6 ELSE subject_sealed END
            WHERE id = $1
            """, connection, tx))
        {
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = state.Status.ToString() });
            cmd.Parameters.Add(new() { Value = Serialize(state) });
            cmd.Parameters.Add(new() { Value = time.GetUtcNow() });
            cmd.Parameters.Add(new() { Value = closing });
            cmd.Parameters.Add(new() { Value = (object?)newSealed ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var data = eventData?.Invoke(row with { State = state });
        await AppendAsync(connection, tx, id, command.Actor, @event,
            JsonSerializer.Serialize(new { from = row.State.Status, to = state.Status, details = data }, Json), cancellationToken);
        if (closing)
        {
            await AppendAsync(connection, tx, id, "system", "subject.identifiers_purged", "{\"reason\":\"request closed\"}", cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return ((await GetAsync(id, cancellationToken))!, @event);
    }

    /// <summary>An audit event that is not a state change (an OTP attempt, an export, a deadline stage).</summary>
    public async Task AuditAsync(Guid id, string actor, string @event, object data, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        _ = await LockAsync(connection, tx, id, cancellationToken) ?? throw new KeyNotFoundException($"Request {id} not found.");
        await AppendAsync(connection, tx, id, actor, @event, JsonSerializer.Serialize(data, Json), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task SetStageAsync(Guid id, DeadlineStage stage, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LockAsync(connection, tx, id, cancellationToken);
        if (row is null || row.Stage == stage || row.State.IsTerminal)
        {
            return;
        }

        await using (var cmd = new NpgsqlCommand("UPDATE dsar.requests SET deadline_stage = $2 WHERE id = $1", connection, tx))
        {
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = stage.ToString() });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendAsync(connection, tx, id, "system:deadline-monitor", stage == DeadlineStage.Overdue ? "deadline.overdue" : "deadline.at_risk",
            JsonSerializer.Serialize(new { dueOn = row.DueOn, stage }, Json), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public static async Task<string> SaveArtifactAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, string kind, string source, string content, CancellationToken cancellationToken)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dsar.artifacts (request_id, kind, source, content, sha256) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (request_id, kind, source) DO UPDATE SET content = EXCLUDED.content, sha256 = EXCLUDED.sha256, created_at = now()
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = source });
        cmd.Parameters.Add(new() { Value = content });
        cmd.Parameters.Add(new() { Value = sha });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return sha;
    }

    public async Task<IReadOnlyList<(string Kind, string Source, string Content, string Sha256)>> ArtifactsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT kind, source, content, sha256 FROM dsar.artifacts WHERE request_id = $1 ORDER BY kind, source");
        cmd.Parameters.Add(new() { Value = id });
        var list = new List<(string, string, string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return list;
    }

    public async Task<IReadOnlyList<AuditEvent>> AuditTrailAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT seq, at, actor, event, data, prev_hash, hash FROM dsar.audit_events WHERE request_id = $1 ORDER BY seq");
        cmd.Parameters.Add(new() { Value = id });
        var list = new List<AuditEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new AuditEvent(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }

        return list;
    }

    public async Task<OtpChallenge?> GetChallengeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT code_hash, expires_at, attempts, sent_to FROM dsar.otp_challenges WHERE request_id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OtpChallenge(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2), reader.GetString(3))
            : null;
    }

    public static async Task SaveChallengeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, OtpChallenge challenge, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dsar.otp_challenges (request_id, code_hash, expires_at, attempts, sent_to) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (request_id) DO UPDATE SET code_hash = EXCLUDED.code_hash, expires_at = EXCLUDED.expires_at,
                attempts = EXCLUDED.attempts, sent_to = EXCLUDED.sent_to, created_at = now()
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = challenge.CodeHash });
        cmd.Parameters.Add(new() { Value = challenge.ExpiresAt.ToUniversalTime() });
        cmd.Parameters.Add(new() { Value = challenge.Attempts });
        cmd.Parameters.Add(new() { Value = challenge.SentToMasked });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Counts an attempt atomically, so parallel guesses cannot exceed the attempt limit.</summary>
    public async Task<OtpChallenge?> CountAttemptAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE dsar.otp_challenges SET attempts = attempts + 1 WHERE request_id = $1
            RETURNING code_hash, expires_at, attempts - 1, sent_to
            """);
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OtpChallenge(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2), reader.GetString(3))
            : null;
    }

    public static async Task DeleteChallengeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM dsar.otp_challenges WHERE request_id = $1", connection, tx);
        cmd.Parameters.Add(new() { Value = id });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AppendAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, string actor, string @event, string data, CancellationToken cancellationToken)
    {
        AuditEvent? last = null;
        await using (var cmd = new NpgsqlCommand("SELECT seq, at, actor, event, data, prev_hash, hash FROM dsar.audit_events WHERE request_id = $1 ORDER BY seq DESC LIMIT 1", connection, tx))
        {
            cmd.Parameters.Add(new() { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                last = new AuditEvent(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6));
            }
        }

        var next = AuditChain.Next(last, time.GetUtcNow(), actor, @event, data);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO dsar.audit_events (request_id, seq, at, actor, event, data, prev_hash, hash) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", connection, tx);
        insert.Parameters.Add(new() { Value = id });
        insert.Parameters.Add(new() { Value = next.Seq });
        insert.Parameters.Add(new() { Value = next.At });
        insert.Parameters.Add(new() { Value = next.Actor });
        insert.Parameters.Add(new() { Value = next.Event });
        insert.Parameters.Add(new() { Value = next.Data });
        insert.Parameters.Add(new() { Value = next.PrevHash });
        insert.Parameters.Add(new() { Value = next.Hash });
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RequestRow?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM dsar.requests WHERE id = $1 FOR UPDATE", connection, tx);
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static RequestRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        Deserialize(r.GetString(2)),
        r.GetInt32(3),
        r.GetFieldValue<DateTimeOffset>(4),
        r.GetFieldValue<DateOnly>(5),
        r.GetFieldValue<DateTimeOffset>(6),
        Enum.Parse<DeadlinePolicy>(r.GetString(7)),
        Enum.Parse<DeadlineStage>(r.GetString(8)),
        r.GetString(9),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(10), Json)!,
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<byte[]>(12),
        r.IsDBNull(13) ? null : r.GetString(13),
        r.GetString(14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15));

    private sealed record StateDto(
        Guid Id, RequestType[] Types, Channel Channel, RequestStatus Status, Assurance Required, Assurance Achieved, VerificationFactor[] Factors,
        bool Discovered, bool AccessExportProduced, string? PlanHash, string? PlannedBy, string? ApprovedBy, string? ExecutedBy, string? ClosedReason);

    private static string Serialize(DsarState s) => JsonSerializer.Serialize(new StateDto(s.Id, [.. s.Types], s.Channel, s.Status, s.Required, s.Achieved,
        [.. s.Factors.Order()], s.Discovered, s.AccessExportProduced, s.PlanHash, s.PlannedBy, s.ApprovedBy, s.ExecutedBy, s.ClosedReason), Json);

    private static DsarState Deserialize(string json)
    {
        var d = JsonSerializer.Deserialize<StateDto>(json, Json)!;
        return new DsarState
        {
            Id = d.Id,
            Types = d.Types,
            Channel = d.Channel,
            Status = d.Status,
            Required = d.Required,
            Achieved = d.Achieved,
            Factors = d.Factors.ToHashSet(),
            Discovered = d.Discovered,
            AccessExportProduced = d.AccessExportProduced,
            PlanHash = d.PlanHash,
            PlannedBy = d.PlannedBy,
            ApprovedBy = d.ApprovedBy,
            ExecutedBy = d.ExecutedBy,
            ClosedReason = d.ClosedReason,
        };
    }
}
