using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KvkkDsar.Workflow;

namespace KvkkDsar.Verification;

/// <summary>Identity assurance, ordered: a higher level satisfies every lower requirement.</summary>
public enum Assurance
{
    None = 0,
    Low = 1,
    Substantial = 2,
    High = 3,
}

/// <summary>Evidence that raised a request's assurance; recorded in the evidence pack (never the OTP itself).</summary>
public enum VerificationFactor
{
    /// <summary>KEP, secure e-signature or mobile signature: the channel itself identifies the sender.</summary>
    QualifiedChannel,

    /// <summary>Sent from the e-mail address the subject earlier gave the controller and that is on file.</summary>
    RegisteredEmail,

    /// <summary>Wet-signed letter, checked by an operator against the identity document copy.</summary>
    SignedLetter,

    /// <summary>One-time code delivered to a phone or e-mail already on file (never to one only given in the request).</summary>
    OtpToContactOnFile,

    /// <summary>Birth date given in the request equals the one on file.</summary>
    KnowledgeMatch,
}

/// <summary>
/// Proportionate verification. The Board has held (decision 2019/296) that extra identity information may only be
/// asked for where there is reasonable doubt, and verification must not make exercising the right impractical. So a
/// request is never stepped up beyond what its type needs, and a qualified channel is never asked for an OTP.
/// </summary>
public static class VerificationPolicy
{
    /// <summary>
    /// Erasure changes data irreversibly and access discloses it, so both need assurance; erasure needs the most because
    /// an impostor could destroy someone's records. Objection, rectification and compensation are recorded and handled
    /// by people, and start from the channel's assurance.
    /// </summary>
    public static Assurance RequiredFor(IReadOnlyCollection<RequestType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        return types.Contains(RequestType.Erasure) || types.Contains(RequestType.Rectification) ? Assurance.High
            : types.Contains(RequestType.Access) ? Assurance.Substantial
            : Assurance.Low;
    }

    public static Assurance ChannelAssurance(Channel channel) => channel switch
    {
        Channel.Kep or Channel.SecureESignature or Channel.MobileSignature => Assurance.High,
        Channel.Written => Assurance.Low, // until an operator records the signed-letter check
        Channel.RegisteredEmail => Assurance.Low, // until the sender address is matched against the one on file
        Channel.WebPortal => Assurance.Low,
        _ => Assurance.None,
    };

    public static Assurance Evaluate(Channel channel, IReadOnlySet<VerificationFactor> factors)
    {
        ArgumentNullException.ThrowIfNull(factors);
        if (factors.Contains(VerificationFactor.QualifiedChannel) && ChannelAssurance(channel) == Assurance.High)
        {
            return Assurance.High;
        }

        var possession = factors.Contains(VerificationFactor.OtpToContactOnFile) || factors.Contains(VerificationFactor.RegisteredEmail);
        var signed = factors.Contains(VerificationFactor.SignedLetter);
        var knowledge = factors.Contains(VerificationFactor.KnowledgeMatch);

        // Two independent factors (something you have + something you know, or a checked signature + either) make High.
        if ((possession && knowledge) || (signed && (possession || knowledge)))
        {
            return Assurance.High;
        }

        return possession || signed ? Assurance.Substantial : ChannelAssurance(channel);
    }
}

public enum OtpOutcome
{
    Accepted,
    Wrong,
    Expired,
    LockedOut,
}

/// <summary>A stored challenge: only a keyed hash of the code, never the code.</summary>
public sealed record OtpChallenge(string CodeHash, DateTimeOffset ExpiresAt, int Attempts, string SentToMasked);

/// <summary>
/// Six-digit one-time codes. The hash is HMAC(pepper, requestId|code): a database dump does not reveal codes, and a
/// hash for one request cannot be replayed for another. Five wrong guesses lock the challenge (a 1 in 200 000 chance
/// for a guesser) and the request goes to manual review.
/// </summary>
public sealed class OtpService(byte[] pepper, TimeProvider time)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly byte[] _pepper = pepper is { Length: >= 32 } ? pepper : throw new ArgumentException("OTP pepper must be at least 32 bytes.", nameof(pepper));

    public (string Code, OtpChallenge Challenge) Issue(Guid requestId, string sentToMasked)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        return (code, new OtpChallenge(Hash(requestId, code), time.GetUtcNow() + Lifetime, 0, sentToMasked));
    }

    public (OtpOutcome Outcome, OtpChallenge Updated) Check(Guid requestId, OtpChallenge challenge, string? code)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.Attempts >= MaxAttempts)
        {
            return (OtpOutcome.LockedOut, challenge);
        }

        if (time.GetUtcNow() > challenge.ExpiresAt)
        {
            return (OtpOutcome.Expired, challenge);
        }

        var updated = challenge with { Attempts = challenge.Attempts + 1 };
        var expected = Convert.FromHexString(challenge.CodeHash);
        var actual = Convert.FromHexString(Hash(requestId, code ?? string.Empty));
        if (CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return (OtpOutcome.Accepted, updated);
        }

        return (updated.Attempts >= MaxAttempts ? OtpOutcome.LockedOut : OtpOutcome.Wrong, updated);
    }

    private string Hash(Guid requestId, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes($"{requestId:N}|{code}")));
}
