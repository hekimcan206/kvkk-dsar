using Bogus;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KvkkDsar.Identifiers;
using KvkkDsar.Synthetic;

namespace KvkkDsar.UnitTests;

public sealed class IdentifierTests
{
    [Theory]
    [InlineData("10000000146", true)] // the well-known valid test TCKN
    [InlineData("10000000147", false)]
    [InlineData("01234567890", false)] // leading zero
    [InlineData("1000000014", false)]
    [InlineData("1000000014a", false)]
    [InlineData(null, false)]
    public void Tckn_validation(string? value, bool valid) => Tckn.IsValid(value).Should().Be(valid);

    [Property(MaxTest = 500)]
    public Property Generated_tckn_is_valid_and_any_single_digit_change_breaks_it() =>
        Prop.ForAll(Gen.Choose(100_000_000, 999_999_999).ToArbitrary(), Gen.Choose(0, 10).ToArbitrary(), Gen.Choose(1, 9).ToArbitrary(), (nine, position, delta) =>
        {
            var tckn = Tckn.WithCheckDigits(nine.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var chars = tckn.ToCharArray();
            chars[position] = (char)('0' + ((chars[position] - '0' + delta) % 10));
            var changed = new string(chars);
            return Tckn.IsValid(tckn) && (changed[0] == '0' || !Tckn.IsValid(changed));
        });

    [Theory]
    [InlineData("0532 111 22 33")]
    [InlineData("+90 532 111 22 33")]
    [InlineData("00905321112233")]
    [InlineData("(0532) 111-2233")]
    [InlineData("05321112233")]
    [InlineData("5321112233")]
    public void Phone_spellings_share_one_canonical_form(string spelling) =>
        PhoneNumbers.Canonical(spelling).Should().Be("5321112233");

    [Theory]
    [InlineData("12345")]
    [InlineData("0632 111 22 33")] // no Turkish numbers start with 6
    [InlineData("")]
    public void Not_a_turkish_phone(string spelling) => PhoneNumbers.Canonical(spelling).Should().BeNull();

    [Property(MaxTest = 300)]
    public Property Every_generated_spelling_canonicalises_back_and_matches_the_sql_stored_form() =>
        Prop.ForAll(Gen.Choose(0, 9_999_999).ToArbitrary(), Gen.Choose(0, 1000).ToArbitrary(), (rest, seed) =>
        {
            var national = "532" + rest.ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
            var spelling = ClinicDataset.PhoneSpelling(national, new Randomizer(seed));
            return PhoneNumbers.Canonical(spelling) == national && PhoneNumbers.StoredForm(spelling) == national;
        });

    [Theory]
    [InlineData(" Ali.Veli@Gmail.COM ", "ali.veli@gmail.com")]
    [InlineData("not-an-email", null)]
    [InlineData("a@b", null)]
    [InlineData("two@@at.com", null)]
    public void Email_normalisation(string input, string? expected) => Emails.Normalize(input).Should().Be(expected);

    [Fact]
    public void Masks_keep_enough_to_recognise_and_no_more()
    {
        Mask.NationalId("10000000146").Should().Be("10*******46");
        Mask.Phone("5321112233").Should().Be("******2233");
        Mask.Email("ali.veli@gmail.com").Should().Be("a***@gmail.com");
    }

    [Fact]
    public void Subject_creation_reports_every_problem()
    {
        var (value, errors) = SubjectIdentifiers.Create("10000000147", "x", "123", "", null, null);
        value.Should().BeNull();
        errors.Should().HaveCount(4);

        var (ok, none) = SubjectIdentifiers.Create("10000000146", " A@B.co ", "0532 111 22 33", "Ayşe", "Yılmaz", new DateOnly(1980, 1, 2));
        none.Should().BeEmpty();
        ok!.Email.Should().Be("a@b.co");
        ok.Phone.Should().Be("5321112233");
        ok.Masked().Should().ContainKey("NationalId").WhoseValue.Should().Be("10*******46");
    }

    [Fact]
    public void Fingerprints_are_keyed_and_stable()
    {
        var key1 = new SubjectFingerprinter(new byte[32]);
        var key2 = new SubjectFingerprinter(Enumerable.Repeat((byte)1, 32).ToArray());
        key1.Of(IdentifierKind.NationalId, "10000000146").Should().Be(key1.Of(IdentifierKind.NationalId, "10000000146")).And.HaveLength(32);
        key1.Of(IdentifierKind.NationalId, "10000000146").Should().NotBe(key2.Of(IdentifierKind.NationalId, "10000000146"));
        FluentActions.Invoking(() => new SubjectFingerprinter(new byte[8])).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Hasta 0532 111 22 33 numarasından aranacak", 1)]
    [InlineData("Yakını +90 (532) 111-22-33 ile görüşüldü", 1)]
    [InlineData("İki kez: 5321112233 ve 0532 111 2233", 2)]
    [InlineData("Dosya ref 90532111223345 ile arşivlendi", 0)] // digits embedded in a longer number
    [InlineData("Ref 15321112233", 0)]
    [InlineData("0532 111 22 34", 0)]
    public void Free_text_phone_matches_respect_digit_boundaries(string text, int expected) =>
        FreeTextMatcher.Find(text, IdentifierKind.Phone, "5321112233").Should().HaveCount(expected);

    [Fact]
    public void Free_text_email_and_tckn()
    {
        FreeTextMatcher.Contains("Sonuç Ali.Veli@Gmail.com adresine", IdentifierKind.Email, "ali.veli@gmail.com").Should().BeTrue();
        FreeTextMatcher.Contains("xali.veli@gmail.com", IdentifierKind.Email, "ali.veli@gmail.com").Should().BeFalse();
        FreeTextMatcher.Contains("TC 10000000146 doğrulandı", IdentifierKind.NationalId, "10000000146").Should().BeTrue();
        FreeTextMatcher.Contains("No 910000000146", IdentifierKind.NationalId, "10000000146").Should().BeFalse();
    }

    [Property(MaxTest = 200)]
    public Property Redaction_removes_every_occurrence_and_keeps_the_rest() =>
        Prop.ForAll(Gen.Choose(0, 1000).ToArbitrary(), seed =>
        {
            var r = new Randomizer(seed);
            var phone = "541" + r.ReplaceNumbers("#######");
            var text = $"Not {ClinicDataset.PhoneSpelling(phone, r)} arandı; tekrar {ClinicDataset.PhoneSpelling(phone, r)}. Bitti.";
            var redacted = FreeTextMatcher.Redact(text, [(IdentifierKind.Phone, phone)]);
            return !FreeTextMatcher.Contains(redacted, IdentifierKind.Phone, phone)
                && redacted.StartsWith("Not [silindi] arandı; tekrar [silindi]", StringComparison.Ordinal)
                && redacted.EndsWith(". Bitti.", StringComparison.Ordinal);
        });
}
