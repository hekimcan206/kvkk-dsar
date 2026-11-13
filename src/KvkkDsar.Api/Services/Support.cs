using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using KvkkDsar.Api.Persistence;
using KvkkDsar.Catalog;
using KvkkDsar.Deadlines;
using KvkkDsar.Discovery;
using KvkkDsar.Identifiers;
using KvkkDsar.Inventory;
using KvkkDsar.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KvkkDsar.Api.Services;

internal sealed class DsarOptions
{
    public const string Section = "Dsar";

    /// <summary>Base64, at least 32 bytes: keys the subject fingerprints in records and evidence.</summary>
    public string FingerprintKey { get; set; } = "";

    /// <summary>Base64, at least 32 bytes: peppers the OTP hashes.</summary>
    public string OtpPepper { get; set; } = "";

    /// <summary>Base64, exactly 32 bytes: AES-256-GCM key for the sealed identifiers of open requests.</summary>
    public string SubjectKey { get; set; } = "";

    public DeadlinePolicy DeadlinePolicy { get; set; } = DeadlinePolicy.Strict;

    public int DeadlineScanSeconds { get; set; } = 60;

    public static byte[] Key(string base64, string name, int minLength)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Dsar:{name} must be base64.");
        }

        return bytes.Length >= minLength ? bytes : throw new InvalidOperationException($"Dsar:{name} must decode to at least {minLength} bytes (generate with `openssl rand -base64 32`).");
    }
}

internal sealed class SourceOptions
{
    public string Name { get; set; } = "";

    public string Schema { get; set; } = "public";

    /// <summary>Read-only role: discovery, contact lookup, access export, post-check.</summary>
    public string FinderConnection { get; set; } = "";

    /// <summary>UPDATE/DELETE role, used only by the executor of an approved plan.</summary>
    public string EraserConnection { get; set; } = "";

    /// <summary>kvkk-data-inventory classification.json for this source.</summary>
    public string Inventory { get; set; } = "";

    public string Policy { get; set; } = "";
}

internal sealed class EvidenceOptions
{
    public const string Section = "Evidence";

    /// <summary>PKCS#8 PEM of an ECDSA P-256 key. Empty: an ephemeral key per process (development only, logged loudly).</summary>
    public string SigningKeyPem { get; set; } = "";
}

/// <summary>What is sealed per open request: what the applicant typed, and what verification bound to them.</summary>
internal sealed record SealedSubject(SubjectIdentifiers Provided, SubjectIdentifiers? Bound, IReadOnlyList<string> Unbound);

/// <summary>
/// AES-256-GCM for the identifiers of open requests, with the request id as associated data so a sealed blob cannot be
/// moved to another request. Purged (set to NULL) by the store when the request closes.
/// </summary>
internal sealed class SubjectVault(IOptions<DsarOptions> options)
{
    private readonly byte[] _key = DsarOptions.Key(options.Value.SubjectKey, nameof(DsarOptions.SubjectKey), 32)[..32];

    public byte[] Seal(Guid requestId, SealedSubject subject)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(subject, WorkflowStore.Json);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, requestId.ToByteArray());
        return [.. nonce, .. tag, .. cipher];
    }

    public SealedSubject Open(Guid requestId, byte[]? sealedBytes)
    {
        if (sealedBytes is null)
        {
            throw new Workflow.WorkflowException("purged", "The request is closed; its identifiers have been purged.");
        }

        var plain = new byte[sealedBytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain, requestId.ToByteArray());
        return JsonSerializer.Deserialize<SealedSubject>(plain, WorkflowStore.Json)!;
    }
}

/// <summary>One searchable system: its inventory, policy, catalog (read once from the database) and access roles.</summary>
internal sealed class SourceContext(SourceOptions options, DataInventory inventory, DsarPolicy policy, NpgsqlDataSource finder, NpgsqlDataSource eraser)
{
    // The catalog is read once, on first use; a failed read is retried on the next call.
    private Lazy<Task<DiscoveryEngine>>? _engine;

    public string Name => options.Name;

    public string Schema => options.Schema;

    public DataInventory Inventory { get; } = inventory;

    public DsarPolicy Policy { get; } = policy;

    public NpgsqlDataSource Finder { get; } = finder;

    public NpgsqlDataSource Eraser { get; } = eraser;

    public async Task<DiscoveryEngine> EngineAsync(CancellationToken cancellationToken)
    {
        var lazy = _engine ??= new Lazy<Task<DiscoveryEngine>>(LoadAsync);
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (NpgsqlException)
        {
            Interlocked.CompareExchange(ref _engine, null, lazy);
            throw;
        }
    }

    private async Task<DiscoveryEngine> LoadAsync() =>
        DiscoveryEngine.Create(Inventory, Policy, await PostgresCatalogReader.ReadAsync(Finder, options.Schema, CancellationToken.None));

    public async Task<SourceCatalog> CatalogAsync(CancellationToken cancellationToken) => (await EngineAsync(cancellationToken)).Catalog;

    public async Task<ISourceReader> ReaderAsync(CancellationToken cancellationToken) => new PostgresSourceReader(Finder, await CatalogAsync(cancellationToken));
}

internal sealed class SourceRegistry : IAsyncDisposable
{
    private readonly List<SourceContext> _sources = [];

    public SourceRegistry(IConfiguration configuration)
    {
        foreach (var o in configuration.GetSection("Sources").Get<List<SourceOptions>>() ?? [])
        {
            string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
            _sources.Add(new SourceContext(o, DataInventory.Load(Resolve(o.Inventory)), DsarPolicy.Load(Resolve(o.Policy)),
                Persistence.PersistenceExtensions.CreateDataSource(o.FinderConnection), Persistence.PersistenceExtensions.CreateDataSource(o.EraserConnection)));
        }
    }

    public IReadOnlyList<SourceContext> All => _sources;

    public SourceContext Get(string name) =>
        _sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"Unknown source '{name}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
        {
            await s.Finder.DisposeAsync();
            await s.Eraser.DisposeAsync();
        }
    }
}

/// <summary>Delivers one-time codes. Production plugs in an SMS / e-mail provider; the demo keeps them in memory.</summary>
internal interface IOtpSender
{
    Task SendAsync(Guid requestId, IdentifierKind channel, string destination, string code, CancellationToken cancellationToken);
}

internal sealed class UnconfiguredOtpSender : IOtpSender
{
    public Task SendAsync(Guid requestId, IdentifierKind channel, string destination, string code, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No OTP delivery channel is configured. Configure an SMS/e-mail sender, or verify through a qualified channel or signed letter.");
}

/// <summary>
/// Demo only (Demo:Inbox=true): codes are kept in process memory and readable at /demo/inbox/{id}, standing in for
/// the subject's phone. Never enable it in production: whoever can read the inbox can verify as anyone.
/// </summary>
internal sealed class DemoInbox : IOtpSender
{
    private readonly ConcurrentDictionary<Guid, (string To, string Code)> _messages = new();

    public Task SendAsync(Guid requestId, IdentifierKind channel, string destination, string code, CancellationToken cancellationToken)
    {
        _messages[requestId] = (Mask.Of(channel, destination), code);
        return Task.CompletedTask;
    }

    public (string To, string Code)? Read(Guid requestId) => _messages.TryGetValue(requestId, out var m) ? m : null;
}

internal sealed class DsarMetrics : IDisposable
{
    public const string MeterName = "KvkkDsar.Api";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _created;
    private readonly Counter<long> _responded;
    private readonly Counter<long> _rows;
    private readonly Histogram<double> _discovery;
    private volatile IReadOnlyDictionary<DeadlineStage, int> _open = new Dictionary<DeadlineStage, int>();

    public DsarMetrics()
    {
        _created = _meter.CreateCounter<long>("dsar.requests.created", description: "Requests received, by type.");
        _responded = _meter.CreateCounter<long>("dsar.requests.responded", description: "Requests answered, by whether within the deadline.");
        _rows = _meter.CreateCounter<long>("dsar.rows.changed", description: "Source rows changed by executed plans, by operation.");
        _discovery = _meter.CreateHistogram<double>("dsar.discovery.duration", unit: "ms", description: "Discovery time per source.");
        _meter.CreateObservableGauge("dsar.requests.open", () => _open.Select(kv => new Measurement<int>(kv.Value, new KeyValuePair<string, object?>("stage", kv.Key.ToString()))),
            description: "Open requests by deadline stage (OnTrack / AtRisk / Overdue).");
    }

    public void Created(string types) => _created.Add(1, new KeyValuePair<string, object?>("types", types));

    public void Responded(bool onTime) => _responded.Add(1, new KeyValuePair<string, object?>("on_time", onTime));

    public void RowsChanged(string operation, int rows) => _rows.Add(rows, new KeyValuePair<string, object?>("operation", operation));

    public void Discovery(string source, double ms) => _discovery.Record(ms, new KeyValuePair<string, object?>("source", source));

    public void OpenByStage(IReadOnlyDictionary<DeadlineStage, int> counts) => _open = counts;

    public void Dispose() => _meter.Dispose();
}
