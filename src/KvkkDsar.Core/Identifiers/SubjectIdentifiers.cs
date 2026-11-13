using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KvkkDsar.Identifiers;

/// <summary>
/// What the applicant gave us, canonicalised. The KVKK application communiqué asks for name, surname, TCKN (or
/// nationality and passport number for foreigners), address and a contact channel; only the identifiers that
/// can be searched for in a database are kept here.
/// </summary>
public sealed record SubjectIdentifiers
{
    public string? NationalId { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public DateOnly? BirthDate { get; init; }

    /// <summary>Canonicalises raw input; returns the problems instead of throwing so the API can answer 400.</summary>
    public static (SubjectIdentifiers? Value, IReadOnlyList<string> Errors) Create(
        string? nationalId, string? email, string? phone, string? firstName, string? lastName, DateOnly? birthDate)
    {
        var errors = new List<string>();
        string? tckn = null;
        if (!string.IsNullOrWhiteSpace(nationalId))
        {
            tckn = nationalId.Trim();
            if (!Tckn.IsValid(tckn))
            {
                errors.Add("nationalId is not a valid T.C. Kimlik No (check digits).");
            }
        }

        var normalizedEmail = Emails.Normalize(email);
        if (!string.IsNullOrWhiteSpace(email) && normalizedEmail is null)
        {
            errors.Add("email is not a valid e-mail address.");
        }

        var canonicalPhone = PhoneNumbers.Canonical(phone);
        if (!string.IsNullOrWhiteSpace(phone) && canonicalPhone is null)
        {
            errors.Add("phone is not a Turkish phone number.");
        }

        if (tckn is null && normalizedEmail is null && canonicalPhone is null)
        {
            errors.Add("At least one searchable identifier (nationalId, email or phone) is required.");
        }

        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            errors.Add("firstName and lastName are required by the application communiqué.");
        }

        return errors.Count > 0
            ? (null, errors)
            : (new SubjectIdentifiers
            {
                NationalId = tckn,
                Email = normalizedEmail,
                Phone = canonicalPhone,
                FirstName = firstName!.Trim(),
                LastName = lastName!.Trim(),
                BirthDate = birthDate,
            }, errors);
    }

    public IEnumerable<(IdentifierKind Kind, string Value)> Searchable()
    {
        if (NationalId is not null)
        {
            yield return (IdentifierKind.NationalId, NationalId);
        }

        if (Email is not null)
        {
            yield return (IdentifierKind.Email, Email);
        }

        if (Phone is not null)
        {
            yield return (IdentifierKind.Phone, Phone);
        }
    }

    public IReadOnlyDictionary<string, string> Masked() =>
        Searchable().ToDictionary(s => s.Kind.ToString(), s => Mask.Of(s.Kind, s.Value));
}

/// <summary>
/// Keyed fingerprints (HMAC-SHA256) of identifiers. Evidence packs and logs carry fingerprints instead of the
/// identifiers, so the controller can later prove "this pack is about the person with this TCKN" by recomputing the
/// fingerprint, while a leaked pack does not hand out the TCKN. An unkeyed hash would not do: there are only
/// about a billion valid TCKNs, which a laptop enumerates in minutes.
/// </summary>
public sealed class SubjectFingerprinter(byte[] key)
{
    private readonly byte[] _key = key is { Length: >= 32 } ? key : throw new ArgumentException("Fingerprint key must be at least 32 bytes.", nameof(key));

    public string Of(IdentifierKind kind, string canonical)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{kind}:{canonical}"));
        return Convert.ToHexStringLower(mac.AsSpan(0, 16));
    }

    /// <summary>One fingerprint for the subject: of the strongest identifier given.</summary>
    public string Of(SubjectIdentifiers subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var (kind, value) = subject.Searchable().First();
        return Of(kind, value);
    }
}

/// <summary>
/// Finds identifiers inside free text (appointment notes, comments). The database query only pre-filters
/// (digits-only LIKE); this matcher decides, with digit boundaries, so "Dosya 90532111223345" is not a phone number
/// and a TCKN is only a TCKN if its check digits hold.
/// </summary>
public static partial class FreeTextMatcher
{
    // The optional leading "(" covers "(0532) 111-2233", so a redaction does not leave a stray parenthesis behind.
    [GeneratedRegex(@"(?<![\d+])\(?(?:(?:\+|00)?90[\s.\-]?|0)?\(?([2-58]\d{2})\)?[\s.\-]?(\d{3})[\s.\-]?(\d{2})[\s.\-]?(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"(?<![\w.+\-])[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<!\d)[1-9]\d{10}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex TcknPattern();

    /// <summary>Every occurrence of the identifier in the text, as (start, length).</summary>
    public static IReadOnlyList<(int Start, int Length)> Find(string? text, IdentifierKind kind, string canonical)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var hits = new List<(int, int)>();
        switch (kind)
        {
            case IdentifierKind.Phone:
                foreach (Match m in PhonePattern().Matches(text))
                {
                    var national = string.Concat(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
                    if (string.Equals(national, canonical, StringComparison.Ordinal))
                    {
                        hits.Add((m.Index, m.Length));
                    }
                }

                break;
            case IdentifierKind.Email:
                foreach (Match m in EmailPattern().Matches(text))
                {
                    if (string.Equals(Emails.Normalize(m.Value), canonical, StringComparison.Ordinal))
                    {
                        hits.Add((m.Index, m.Length));
                    }
                }

                break;
            case IdentifierKind.NationalId:
                foreach (Match m in TcknPattern().Matches(text))
                {
                    if (string.Equals(m.Value, canonical, StringComparison.Ordinal) && Tckn.IsValid(m.Value))
                    {
                        hits.Add((m.Index, m.Length));
                    }
                }

                break;
        }

        return hits;
    }

    public static bool Contains(string? text, IdentifierKind kind, string canonical) => Find(text, kind, canonical).Count > 0;

    /// <summary>Replaces every occurrence with a marker; the rest of the note (and its meaning) stays.</summary>
    public static string Redact(string text, IEnumerable<(IdentifierKind Kind, string Value)> identifiers, string marker = "[silindi]")
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = identifiers.SelectMany(i => Find(text, i.Kind, i.Value)).OrderByDescending(s => s.Start).ToList();
        var sb = new StringBuilder(text);
        var lastStart = int.MaxValue;
        foreach (var (start, length) in spans)
        {
            if (start + length > lastStart)
            {
                continue; // overlapping hit already redacted
            }

            sb.Remove(start, length).Insert(start, marker);
            lastStart = start;
        }

        return sb.ToString();
    }
}
