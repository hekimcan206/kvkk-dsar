using System.Globalization;
using System.Text;

namespace KvkkDsar.Identifiers;

/// <summary>The kinds of identifier a data subject can give and the inventory can mark as a search key.</summary>
public enum IdentifierKind
{
    /// <summary>T.C. Kimlik No: 11 digits, first digit non-zero, two check digits.</summary>
    NationalId,
    Email,
    Phone,
}

/// <summary>T.C. Kimlik No validation (the same algorithm as python-stdnum's <c>stdnum.tr.tckimlik</c>).</summary>
public static class Tckn
{
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != 11 || value[0] == '0')
        {
            return false;
        }

        Span<int> d = stackalloc int[11];
        for (var i = 0; i < 11; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }

            d[i] = value[i] - '0';
        }

        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        var d10 = ((odd * 7) - even) % 10;
        if (d10 < 0)
        {
            d10 += 10;
        }

        var d11 = (odd + even + d[9]) % 10;
        return d[9] == d10 && d[10] == d11;
    }

    /// <summary>Completes nine digits with the two check digits; used by the synthetic generator and the tests.</summary>
    public static string WithCheckDigits(string nineDigits)
    {
        ArgumentNullException.ThrowIfNull(nineDigits);
        if (nineDigits.Length != 9 || nineDigits[0] == '0' || !nineDigits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Expected nine digits with a non-zero first digit.", nameof(nineDigits));
        }

        var d = nineDigits.Select(c => c - '0').ToArray();
        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        var d10 = (((odd * 7) - even) % 10 + 10) % 10;
        var d11 = (odd + even + d10) % 10;
        return string.Create(CultureInfo.InvariantCulture, $"{nineDigits}{d10}{d11}");
    }
}

/// <summary>
/// Turkish phone numbers are stored in every spelling imaginable ("0532 111 22 33", "+90 (532) 111-2233",
/// "5321112233"). Matching compares the 10-digit national significant number, which is what the SQL match expression
/// <c>right(regexp_replace(col, '\D', '', 'g'), 10)</c> computes too.
/// </summary>
public static class PhoneNumbers
{
    public const int NationalLength = 10;

    /// <summary>The 10-digit national number, or null if the input cannot be a Turkish number.</summary>
    public static string? Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = Digits(value);
        if (digits.StartsWith("0090", StringComparison.Ordinal))
        {
            digits = digits[4..];
        }
        else if (digits.Length == 12 && digits.StartsWith("90", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }
        else if (digits.Length == 11 && digits[0] == '0')
        {
            digits = digits[1..];
        }

        // National significant numbers start with 2-5 (geographic, mobile) or 8 (non-geographic).
        return digits.Length == NationalLength && (digits[0] is (>= '2' and <= '5') or '8') ? digits : null;
    }

    /// <summary>What the database expression computes: the last 10 digits of everything numeric in the value.</summary>
    public static string StoredForm(string value)
    {
        var digits = Digits(value);
        return digits.Length <= NationalLength ? digits : digits[^NationalLength..];
    }

    public static string Digits(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>E-mail addresses compare trimmed and case-insensitively (SQL: <c>lower(btrim(col))</c>).</summary>
public static class Emails
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == trimmed.LastIndexOf('@') && at < trimmed.Length - 3 && trimmed.IndexOf('.', at) > at + 1
            && !trimmed.Any(char.IsWhiteSpace)
            ? trimmed
            : null;
    }
}

/// <summary>Masked forms for logs, operator screens and evidence: enough to recognise, not enough to use.</summary>
public static class Mask
{
    public static string NationalId(string value) =>
        value.Length == 11 ? string.Concat(value.AsSpan(0, 2), "*******", value.AsSpan(9, 2)) : "***";

    public static string Phone(string canonical) =>
        canonical.Length >= 4 ? string.Concat(new string('*', canonical.Length - 4), canonical.AsSpan(canonical.Length - 4)) : "***";

    public static string Email(string normalized)
    {
        var at = normalized.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : string.Concat(normalized.AsSpan(0, 1), "***", normalized.AsSpan(at));
    }

    public static string Of(IdentifierKind kind, string canonical) => kind switch
    {
        IdentifierKind.NationalId => NationalId(canonical),
        IdentifierKind.Email => Email(canonical),
        IdentifierKind.Phone => Phone(canonical),
        _ => "***",
    };
}
