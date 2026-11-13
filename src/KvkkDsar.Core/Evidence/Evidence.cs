using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KvkkDsar.Evidence;

/// <summary>One audit event. <see cref="Data"/> is stored as the exact JSON text that was hashed (text, not jsonb, which reorders keys).</summary>
public sealed record AuditEvent(int Seq, DateTimeOffset At, string Actor, string Event, string Data, string PrevHash, string Hash);

/// <summary>
/// A per-request hash chain: each event's hash covers the previous hash, so deleting, reordering or editing an event
/// breaks every later link. The database also forbids UPDATE and DELETE on the table; the chain is what lets an
/// outsider check that from the evidence pack alone.
/// </summary>
public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(string prevHash, int seq, DateTimeOffset at, string actor, string @event, string data)
    {
        var material = string.Join('\n', prevHash, seq.ToString(CultureInfo.InvariantCulture),
            at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), actor, @event, data);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static AuditEvent Next(AuditEvent? previous, DateTimeOffset at, string actor, string @event, string data)
    {
        var seq = (previous?.Seq ?? 0) + 1;
        var prev = previous?.Hash ?? Genesis;
        // Microsecond precision: what PostgreSQL timestamptz stores, so a re-read event hashes the same.
        var truncated = new DateTimeOffset(at.UtcTicks - (at.UtcTicks % 10), TimeSpan.Zero);
        return new AuditEvent(seq, truncated, actor, @event, data, prev, ComputeHash(prev, seq, truncated, actor, @event, data));
    }

    /// <summary>Problems found walking the chain; empty means intact.</summary>
    public static IReadOnlyList<string> Verify(IReadOnlyList<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var problems = new List<string>();
        var prev = Genesis;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Seq != i + 1)
            {
                problems.Add($"event {i + 1}: sequence number is {e.Seq} (missing or reordered events)");
            }

            if (!string.Equals(e.PrevHash, prev, StringComparison.Ordinal))
            {
                problems.Add($"event {e.Seq}: previous-hash link broken");
            }

            var expected = ComputeHash(e.PrevHash, e.Seq, e.At, e.Actor, e.Event, e.Data);
            if (!string.Equals(expected, e.Hash, StringComparison.Ordinal))
            {
                problems.Add($"event {e.Seq} ({e.Event}): content does not match its hash");
            }

            prev = e.Hash;
        }

        return problems;
    }
}

public sealed record ManifestFile(string Name, string Sha256, long Bytes);

public sealed record EvidenceManifest(
    string Format,
    Guid RequestId,
    string Reference,
    DateTimeOffset GeneratedAt,
    string AuditHead,
    int AuditEvents,
    IReadOnlyList<ManifestFile> Files,
    string KeyId,
    string PublicKey);

public sealed record VerificationReport(bool Valid, string? Reference, string? KeyId, int Files, IReadOnlyList<string> Problems);

/// <summary>
/// The evidence pack: a ZIP with the request's records (verification method, discovery map, plan, approvals,
/// execution log, post-check, deadline, the response letters and the audit chain), a manifest with the SHA-256 of each
/// file, and an ECDSA P-256 signature over the manifest. Anyone with the public key can check it offline; the pack
/// holds row addresses, counts and keyed fingerprints, never the personal data itself.
/// </summary>
public static class EvidencePack
{
    public const string Format = "kvkk-dsar-evidence/1";
    public const string ManifestName = "manifest.json";
    public const string SignatureName = "manifest.sig";
    public const string AuditLogName = "audit-log.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string KeyId(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()).AsSpan(0, 8));
    }

    public static byte[] AuditLog(IEnumerable<AuditEvent> events) =>
        Encoding.UTF8.GetBytes(string.Concat(events.Select(e => JsonSerializer.Serialize(e, JsonSerializerOptions.Web) + "\n")));

    public static byte[] Build(Guid requestId, string reference, DateTimeOffset generatedAt, IReadOnlyList<AuditEvent> audit,
        IReadOnlyDictionary<string, byte[]> files, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(signingKey);

        var all = new SortedDictionary<string, byte[]>(files.ToDictionary(), StringComparer.Ordinal) { [AuditLogName] = AuditLog(audit) };
        var manifest = new EvidenceManifest(Format, requestId, reference, generatedAt,
            audit.Count == 0 ? AuditChain.Genesis : audit[^1].Hash, audit.Count,
            [.. all.Select(f => new ManifestFile(f.Key, Convert.ToHexStringLower(SHA256.HashData(f.Value)), f.Value.LongLength))],
            KeyId(signingKey), Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo()));
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var signature = signingKey.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Fixed entry times: the same pack content always zips to the same entries.
            Add(zip, ManifestName, manifestBytes, generatedAt);
            Add(zip, SignatureName, Encoding.ASCII.GetBytes(Convert.ToBase64String(signature) + "\n"), generatedAt);
            foreach (var (name, bytes) in all)
            {
                Add(zip, name, bytes, generatedAt);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Checks a pack: signature over the manifest (against the embedded key, and against <paramref name="trustedKeyId"/>
    /// when given, since an attacker can re-sign with their own key), every file's hash, no unlisted files, and the
    /// audit chain from first event to the head the manifest names.
    /// </summary>
    public static VerificationReport Verify(Stream zipStream, string? trustedKeyId = null)
    {
        var problems = new List<string>();
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            return new VerificationReport(false, null, null, 0, [$"not a readable ZIP archive: {ex.Message}"]);
        }

        using (zip)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            try
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.Length > 64L * 1024 * 1024)
                    {
                        return new VerificationReport(false, null, null, 0, [$"{entry.FullName}: larger than 64 MiB, refused"]);
                    }

                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    entries[entry.FullName] = ms.ToArray();
                }
            }
            catch (InvalidDataException ex)
            {
                return new VerificationReport(false, null, null, 0, [$"corrupt archive entry: {ex.Message}"]);
            }

            if (!entries.TryGetValue(ManifestName, out var manifestBytes) || !entries.TryGetValue(SignatureName, out var sigBytes))
            {
                return new VerificationReport(false, null, null, 0, ["manifest.json or manifest.sig is missing"]);
            }

            EvidenceManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<EvidenceManifest>(manifestBytes, Json);
            }
            catch (JsonException ex)
            {
                return new VerificationReport(false, null, null, 0, [$"manifest is not valid JSON: {ex.Message}"]);
            }

            if (manifest is null || manifest.Format != Format)
            {
                return new VerificationReport(false, null, null, 0, [$"unknown manifest format (expected {Format})"]);
            }

            using (var key = ECDsa.Create())
            {
                try
                {
                    key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(manifest.PublicKey), out _);
                    var signature = Convert.FromBase64String(Encoding.ASCII.GetString(sigBytes).Trim());
                    if (!key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    {
                        problems.Add("manifest signature is invalid");
                    }

                    if (!string.Equals(KeyId(key), manifest.KeyId, StringComparison.Ordinal))
                    {
                        problems.Add("manifest keyId does not match the embedded public key");
                    }
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    problems.Add($"signature or public key unreadable: {ex.Message}");
                }
            }

            if (trustedKeyId is not null && !string.Equals(trustedKeyId, manifest.KeyId, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"signed with key {manifest.KeyId}, not the trusted key {trustedKeyId}");
            }

            foreach (var file in manifest.Files)
            {
                if (!entries.TryGetValue(file.Name, out var bytes))
                {
                    problems.Add($"{file.Name}: listed in the manifest but missing");
                }
                else if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal))
                {
                    problems.Add($"{file.Name}: content does not match the manifest hash");
                }
            }

            foreach (var extra in entries.Keys.Except(manifest.Files.Select(f => f.Name).Append(ManifestName).Append(SignatureName), StringComparer.Ordinal))
            {
                problems.Add($"{extra}: present but not listed in the manifest");
            }

            if (entries.TryGetValue(AuditLogName, out var log))
            {
                List<AuditEvent> events;
                try
                {
                    events = [.. Encoding.UTF8.GetString(log).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => JsonSerializer.Deserialize<AuditEvent>(l, JsonSerializerOptions.Web)!)];
                }
                catch (JsonException ex)
                {
                    problems.Add($"audit log is not valid JSON lines: {ex.Message}");
                    events = [];
                }

                problems.AddRange(AuditChain.Verify(events));
                if (events.Count != manifest.AuditEvents || (events.Count > 0 && events[^1].Hash != manifest.AuditHead))
                {
                    problems.Add("audit log does not end at the head named in the manifest");
                }
            }

            return new VerificationReport(problems.Count == 0, manifest.Reference, manifest.KeyId, manifest.Files.Count, problems);
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes, DateTimeOffset at)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = at.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : at;
        using var s = entry.Open();
        s.Write(bytes);
    }
}
