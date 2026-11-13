using System.Globalization;
using Bogus;
using KvkkDsar.Identifiers;
using NpgsqlTypes;

namespace KvkkDsar.Synthetic;

/// <summary>A synthetic person: the identifiers a data subject would put in an application.</summary>
public sealed record Person(int Index, string Tckn, string FirstName, string LastName, DateOnly BirthDate, string Gender, string Phone, string Email)
{
    public SubjectIdentifiers Identifiers(bool withEmail = true, bool withPhone = true) => new()
    {
        NationalId = Tckn,
        Email = withEmail ? Email : null,
        Phone = withPhone ? Phone : null,
        FirstName = FirstName,
        LastName = LastName,
        BirthDate = BirthDate,
    };

    public string FullName => $"{FirstName} {LastName}";
}

public sealed record ColumnDef(string Name, NpgsqlDbType Type);

public sealed class TableData(string name, IReadOnlyList<ColumnDef> columns)
{
    public string Name { get; } = name;

    public IReadOnlyList<ColumnDef> Columns { get; } = columns;

    public List<object?[]> Rows { get; } = [];

    public long NextId => Rows.Count + 1;

    public Dictionary<string, object?> RowAsDictionary(object?[] row) =>
        Columns.Select((c, i) => (c.Name, row[i])).ToDictionary(x => x.Name, x => x.Item2, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What a correct discovery must find for one person: the rows that are theirs, and their identifiers in other people's rows.</summary>
public sealed class GroundTruth
{
    public HashSet<(string Table, string Key)> Owned { get; } = [];

    public HashSet<(string Table, string Key, string Column)> Mentions { get; } = [];
}

/// <summary>
/// A synthetic clinic + HR + online-portal database with the same table and column names as kvkk-data-inventory's
/// labelled clinic schema, so that tool's classification.json is this project's inventory as is. Added for this
/// project: declared foreign keys, and the dates retention is counted from (invoice, prescription, test dates,
/// termination date). Identifiers are random with valid checksums; nobody here exists.
///
/// The data is built to make discovery hard in the ways real systems are: phone numbers in five spellings, e-mail in
/// mixed case with stray spaces, portal accounts under a different e-mail than the patient file, an employee TCKN
/// stored as bigint, people mentioned as emergency contacts and in appointment notes, and decoy notes where a phone
/// number's digits sit inside a longer reference number. Every person's correct answer is recorded in
/// <see cref="Truth"/>.
/// </summary>
public sealed class ClinicDataset
{
    public const string Schema = "clinic";

    private static readonly string[] MailDomains = ["gmail.com", "hotmail.com", "yandex.com", "outlook.com", "icloud.com"];
    private static readonly string[] Departments = ["Kardiyoloji", "Dahiliye", "Ortopedi", "Göz Hastalıkları", "Nöroloji", "Psikiyatri", "Dermatoloji", "Genel Cerrahi"];
    private static readonly string[] Complaints = ["migren", "hipertansiyon", "bel ağrısı", "astım", "diyabet", "gastrit"];
    private static readonly (string Code, string Text)[] Diagnoses =
    [
        ("I10", "Esansiyel (primer) hipertansiyon"), ("E11.9", "Tip 2 diyabet"), ("J45.9", "Astım"), ("G43.9", "Migren"),
        ("M54.5", "Bel ağrısı"), ("K29.7", "Gastrit"), ("F32.9", "Depresif epizod"), ("E78.0", "Hiperkolesterolemi"),
    ];
    private static readonly string[] Drugs = ["Parol 500 mg", "Arveles 25 mg", "Augmentin 1000 mg", "Coraspin 100 mg", "Glifor 1000 mg", "Nexium 40 mg"];
    private static readonly string[] MobilePrefixes = ["530", "532", "533", "535", "536", "538", "541", "542", "505", "506", "555", "553"];

    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public IReadOnlyList<Person> People { get; }

    public IReadOnlyDictionary<string, TableData> Tables { get; }

    public IReadOnlyDictionary<int, GroundTruth> Truth { get; }

    public int Seed { get; }

    private ClinicDataset(int seed, IReadOnlyList<Person> people, IReadOnlyDictionary<string, TableData> tables, IReadOnlyDictionary<int, GroundTruth> truth)
    {
        Seed = seed;
        People = people;
        Tables = tables;
        Truth = truth;
    }

    /// <summary>Tables in load order (parents first).</summary>
    private static readonly string[] LoadOrder = ["hastalar", "randevular", "tanilar", "receteler", "faturalar", "genetik_testler", "sosyal_degerlendirme", "personel", "personel_izinleri", "customers", "audit_log", "subeler"];

    public IEnumerable<TableData> InLoadOrder() => LoadOrder.Select(n => Tables[n]);

    public static ClinicDataset Generate(int people, int seed = 2026)
    {
        Randomizer.Seed = new Random(seed);
        var f = new Faker("tr");
        var r = f.Random;
        var persons = MakePeople(people, f);
        var truth = persons.ToDictionary(p => p.Index, _ => new GroundTruth());
        var t = CreateTables();

        void Own(Person p, string table, long id) => truth[p.Index].Owned.Add((table, id.ToString(CultureInfo.InvariantCulture)));

        foreach (var p in persons)
        {
            if (r.Bool(0.70f))
            {
                AddPatient(p, f, t, persons, truth, Own);
            }

            if (r.Bool(0.35f))
            {
                var customers = t["customers"];
                var id = customers.NextId;
                // 15 % of portal accounts use another address: only the phone links them to the person.
                var email = r.Bool(0.15f) ? $"{TurkishFold(p.FirstName)}{r.Number(100, 999)}@{f.PickRandom(MailDomains)}" : EmailSpelling(p.Email, r);
                customers.Rows.Add([id, p.FullName, email, r.Bool() ? "Y" : "N", PhoneSpelling(p.Phone, r), CardNumber(r),
                    r.Hexadecimal(64, ""), r.Hexadecimal(64, ""), Ipv4(r), r.Bool() ? "Y" : "N", f.Date.Between(Now.AddYears(-6), Now)]);
                Own(p, "customers", id);
            }
        }

        // About 3 % of people are employees (at least 5).
        foreach (var p in persons.Where((_, i) => i % 33 == 7 || (persons.Count < 170 && i % 20 == 3)))
        {
            AddEmployee(p, f, t, persons, truth, Own);
        }

        for (var i = 0; i < Math.Max(50, people / 10); i++)
        {
            t["audit_log"].Rows.Add([(long)(i + 1), $"{TurkishFold(f.Name.FirstName())[..1]}{TurkishFold(f.Name.LastName())}", Ipv4(r),
                f.PickRandom("LOGIN", "VIEW_PATIENT", "EXPORT", "UPDATE"), f.Date.Between(Now.AddDays(-90), Now), "Mozilla/5.0"]);
        }

        for (var i = 0; i < 5; i++)
        {
            t["subeler"].Rows.Add([(long)(i + 1), $"{f.Address.City()} Şubesi", $"0{f.PickRandom("212", "216", "312")} {r.ReplaceNumbers("### ## ##")}", Address(f, r)]);
        }

        return new ClinicDataset(seed, persons, t, truth);
    }

    private static void AddPatient(Person p, Faker f, Dictionary<string, TableData> t, List<Person> persons,
        Dictionary<int, GroundTruth> truth, Action<Person, string, long> own)
    {
        var r = f.Random;
        var patients = t["hastalar"];
        var pid = patients.NextId;
        // 15 % of files were opened before 2006, so a 20-year clinical retention period has ended for them.
        var created = r.Bool(0.15f) ? f.Date.Between(new DateTime(1998, 1, 1), new DateTime(2005, 12, 31)) : f.Date.Between(new DateTime(2008, 1, 1), Now.AddDays(-30));
        patients.Rows.Add([pid, p.Tckn, p.FirstName, p.LastName, p.BirthDate, p.Gender,
            r.Bool(0.95f) ? PhoneSpelling(p.Phone, r) : null,
            r.Bool(0.8f) ? EmailSpelling(p.Email, r) : null,
            Address(f, r), f.PickRandom("A Rh+", "A Rh-", "B Rh+", "0 Rh+", "AB Rh+"),
            r.Number(1, 9).ToString(CultureInfo.InvariantCulture) + r.ReplaceNumbers("##########"), r.Bool(0.9f) ? "E" : "H", created]);
        own(p, "hastalar", pid);

        var old = created.Year < 2006;
        DateTime When() => old && r.Bool(0.8f) ? f.Date.Between(created, new DateTime(2012, 12, 31)) : f.Date.Between(created < new DateTime(2008, 1, 1) ? new DateTime(2008, 1, 1) : created, Now);

        for (var i = r.Number(0, 4); i > 0; i--)
        {
            var appointments = t["randevular"];
            var id = appointments.NextId;
            var (note, mentioned) = Note(p, f, persons);
            appointments.Rows.Add([id, pid, $"Dr. {f.Name.FullName()}", f.PickRandom(Departments), When(), $"{f.PickRandom("A", "B", "C")}{r.Number(1, 40)}", note]);
            own(p, "randevular", id);
            if (mentioned is not null)
            {
                truth[mentioned.Index].Mentions.Add(("randevular", id.ToString(CultureInfo.InvariantCulture), "notlar"));
            }
        }

        for (var i = r.Number(0, 3); i > 0; i--)
        {
            var dx = f.PickRandom(Diagnoses);
            var id = t["tanilar"].NextId;
            t["tanilar"].Rows.Add([id, pid, dx.Code, dx.Text, DateOnly.FromDateTime(When())]);
            own(p, "tanilar", id);
        }

        for (var i = r.Number(0, 3); i > 0; i--)
        {
            var id = t["receteler"].NextId;
            t["receteler"].Rows.Add([id, pid, f.PickRandom(Drugs), f.PickRandom("1x1", "2x1", "3x1"), r.AlphaNumeric(7).ToUpperInvariant(), DateOnly.FromDateTime(When())]);
            own(p, "receteler", id);
        }

        for (var i = r.Number(0, 3); i > 0; i--)
        {
            var id = t["faturalar"].NextId;
            t["faturalar"].Rows.Add([id, pid, "GIB" + r.ReplaceNumbers("#############"), DateOnly.FromDateTime(When()), Math.Round(r.Decimal(150, 25000), 2),
                r.ReplaceNumbers("##########"), r.Bool(0.6f) ? CardNumber(r) : null]);
            own(p, "faturalar", id);
        }

        if (r.Bool(0.05f))
        {
            var id = t["genetik_testler"].NextId;
            t["genetik_testler"].Rows.Add([id, pid, "DNA-" + r.ReplaceNumbers("######"), f.PickRandom("BRCA1 varyantı saptanmadı", "BRCA2 heterozigot varyant"), DateOnly.FromDateTime(When())]);
            own(p, "genetik_testler", id);
        }

        if (r.Bool(0.10f))
        {
            var id = t["sosyal_degerlendirme"].NextId;
            t["sosyal_degerlendirme"].Rows.Add([id, pid, r.Bool(0.7f) ? "Belirtilmemiş" : $"Kod-0{r.Number(1, 6)}", f.PickRandom("Beyan yok", "Beyan edildi"),
                f.PickRandom("Belirtilmemiş", "Sünni", "Alevi"), DateOnly.FromDateTime(When())]);
            own(p, "sosyal_degerlendirme", id);
        }
    }

    private static void AddEmployee(Person p, Faker f, Dictionary<string, TableData> t, List<Person> persons,
        Dictionary<int, GroundTruth> truth, Action<Person, string, long> own)
    {
        var r = f.Random;
        var staff = t["personel"];
        var id = staff.NextId;
        var contact = persons[(p.Index + 1 + r.Number(0, persons.Count - 2)) % persons.Count];
        if (contact.Index == p.Index)
        {
            contact = persons[(p.Index + 1) % persons.Count];
        }

        var hired = DateOnly.FromDateTime(f.Date.Between(new DateTime(1998, 1, 1), new DateTime(2024, 1, 1)));
        DateOnly? left = r.Number(0, 9) switch
        {
            < 5 => null, // still employed
            < 8 => DateOnly.FromDateTime(f.Date.Between(new DateTime(2008, 1, 1), new DateTime(2015, 12, 31))),
            _ => DateOnly.FromDateTime(f.Date.Between(new DateTime(2020, 1, 1), new DateTime(2026, 6, 30))),
        };
        if (left is not null && left < hired)
        {
            hired = left.Value.AddYears(-3);
        }

        staff.Rows.Add([id, long.Parse(p.Tckn, CultureInfo.InvariantCulture), p.FullName, p.BirthDate, p.Gender == "E" ? "Erkek" : "Kadın",
            // Work e-mail, not the private one in applications: the TCKN and phone are what link this record.
            $"{TurkishFold(p.FirstName)}.{TurkishFold(p.LastName)}@klinik.example", PhoneSpelling(p.Phone, r), Address(f, r),
            "TR" + r.ReplaceNumbers("########################"), Math.Round(r.Decimal(22000, 140000), 2), hired, left,
            f.PickRandom("A Rh+", "0 Rh+"), r.Bool(0.85f) ? "Yok" : "%40 ortopedik engelli", f.PickRandom("Sağlık-Sen", "Yok", "Yok"),
            f.PickRandom("Yok", "Mezunlar Derneği"), f.PickRandom("Belirtilmemiş", "İslam"), r.Bool(0.95f) ? "Yok" : "Var", r.Bytes(32),
            r.Bool(0.6f) ? $"{r.Number(1, 81):00} {r.String2(2, "ABCDEFGHJKLMNPRSTUVYZ")} {r.ReplaceNumbers("###")}" : null,
            contact.FullName, PhoneSpelling(contact.Phone, r)]);
        own(p, "personel", id);
        truth[contact.Index].Mentions.Add(("personel", id.ToString(CultureInfo.InvariantCulture), "acil_durum_tel"));

        for (var i = r.Number(0, 3); i > 0; i--)
        {
            var lid = t["personel_izinleri"].NextId;
            var start = DateOnly.FromDateTime(f.Date.Between(hired.ToDateTime(TimeOnly.MinValue), (left ?? DateOnly.FromDateTime(Now)).ToDateTime(TimeOnly.MinValue)));
            t["personel_izinleri"].Rows.Add([lid, id, f.PickRandom("Yıllık", "Mazeret", "Rapor"), start, start.AddDays(r.Number(1, 14)), r.Bool(0.3f) ? f.PickRandom(Diagnoses).Code : null]);
            own(p, "personel_izinleri", lid);
        }
    }

    /// <summary>An appointment note; returns the person (other than the patient) whose phone it genuinely mentions.</summary>
    private static (string? Note, Person? Mentioned) Note(Person patient, Faker f, List<Person> persons)
    {
        var r = f.Random;
        var other = persons[r.Number(0, persons.Count - 1)];
        var complaint = f.PickRandom(Complaints);
        switch (r.Number(0, 9))
        {
            case 0:
            case 1:
                return ($"Hasta {PhoneSpelling(patient.Phone, r)} numarasından aranacak, {complaint} kontrolü.", null);
            case 2:
                return other.Index == patient.Index ? ("Kontrol randevusu.", null)
                    : ($"Randevu ertelendi, hasta yakını ile {PhoneSpelling(other.Phone, r)} üzerinden görüşüldü.", other);
            case 3:
                // Decoy: another person's phone digits embedded in a longer reference number. Not a mention.
                return ($"Dosya ref 90{other.Phone}{r.Number(10, 99)} ile arşivlendi, {complaint}.", null);
            case 4:
                return ($"Sonuçlar e-posta ile iletilecek, {complaint} takibi.", null);
            case 5:
                return ($"Önceki ziyarette {complaint} tanısı kondu, tedaviye devam.", null);
            default:
                return (null, null);
        }
    }

    private static List<Person> MakePeople(int count, Faker f)
    {
        var r = f.Random;
        var tckns = new HashSet<string>(StringComparer.Ordinal);
        var phones = new HashSet<string>(StringComparer.Ordinal);
        var emails = new HashSet<string>(StringComparer.Ordinal);
        var people = new List<Person>(count);
        while (people.Count < count)
        {
            var tckn = Tckn.WithCheckDigits((char)('1' + r.Number(0, 8)) + r.ReplaceNumbers("########"));
            var phone = f.PickRandom(MobilePrefixes) + r.ReplaceNumbers("#######");
            var first = f.Name.FirstName();
            var last = f.Name.LastName();
            var email = $"{TurkishFold(first)}.{TurkishFold(last)}{r.Number(1, 9999)}@{f.PickRandom(MailDomains)}";
            if (!tckns.Add(tckn) || !phones.Add(phone) || !emails.Add(email))
            {
                continue;
            }

            people.Add(new Person(people.Count, tckn, first, last, DateOnly.FromDateTime(f.Date.Between(new DateTime(1940, 1, 1), new DateTime(2006, 12, 31))),
                r.Bool() ? "E" : "K", phone, email));
        }

        return people;
    }

    private static Dictionary<string, TableData> CreateTables()
    {
        static TableData T(string name, params (string, NpgsqlDbType)[] cols) => new(name, [.. cols.Select(c => new ColumnDef(c.Item1, c.Item2))]);
        const NpgsqlDbType Big = NpgsqlDbType.Bigint, Txt = NpgsqlDbType.Varchar, Date = NpgsqlDbType.Date, Ts = NpgsqlDbType.Timestamp, Num = NpgsqlDbType.Numeric;
        return new[]
        {
            T("hastalar", ("id", Big), ("tc_kimlik_no", Txt), ("ad", Txt), ("soyad", Txt), ("dogum_tarihi", Date), ("cinsiyet", Txt), ("cep_telefonu", Txt),
                ("eposta", Txt), ("adres", Txt), ("kan_grubu", Txt), ("kayit_no", Txt), ("kvkk_onay", Txt), ("created_at", Ts)),
            T("randevular", ("id", Big), ("hasta_id", Big), ("doktor_adi", Txt), ("bolum_adi", Txt), ("randevu_zamani", Ts), ("oda_no", Txt), ("notlar", NpgsqlDbType.Text)),
            T("tanilar", ("id", Big), ("hasta_id", Big), ("icd10_kodu", Txt), ("tani_aciklamasi", Txt), ("kayit_tarihi", Date)),
            T("receteler", ("id", Big), ("hasta_id", Big), ("ilac_adi", Txt), ("doz", Txt), ("recete_no", Txt), ("recete_tarihi", Date)),
            T("faturalar", ("id", Big), ("hasta_id", Big), ("fatura_no", Txt), ("fatura_tarihi", Date), ("tutar", Num), ("sigorta_vkn", Txt), ("odeme_karti", Txt)),
            T("genetik_testler", ("id", Big), ("hasta_id", Big), ("dna_ornek_kodu", Txt), ("sonuc", Txt), ("test_tarihi", Date)),
            T("sosyal_degerlendirme", ("id", Big), ("hasta_id", Big), ("etnik_koken", Txt), ("siyasi_gorus", Txt), ("mezhep", Txt), ("degerlendirme_tarihi", Date)),
            T("personel", ("id", Big), ("tckn", Big), ("ad_soyad", Txt), ("birth_date", Date), ("gender", Txt), ("email", Txt), ("gsm", Txt), ("ikamet_adresi", Txt),
                ("iban", Txt), ("maas", Num), ("ise_giris_tarihi", Date), ("isten_cikis_tarihi", Date), ("kan_grubu", Txt), ("engel_durumu", Txt),
                ("sendika_uyeligi", Txt), ("dernek_uyelikleri", Txt), ("din", Txt), ("sabika_kaydi", Txt), ("parmak_izi_sablonu", NpgsqlDbType.Bytea),
                ("arac_plakasi", Txt), ("acil_durum_kisi", Txt), ("acil_durum_tel", Txt)),
            T("personel_izinleri", ("id", Big), ("personel_id", Big), ("izin_turu", Txt), ("baslangic_tarihi", Date), ("bitis_tarihi", Date), ("rapor_tani_kodu", Txt)),
            T("customers", ("id", Big), ("full_name", Txt), ("email_address", Txt), ("email_verified", Txt), ("phone_number", Txt), ("card_number", Txt),
                ("password_hash", Txt), ("tc_hash", Txt), ("last_login_ip", Txt), ("marketing_consent", Txt), ("created_at", Ts)),
            T("audit_log", ("id", Big), ("kullanici_adi", Txt), ("ip_adresi", Txt), ("islem", Txt), ("zaman", Ts), ("user_agent", Txt)),
            T("subeler", ("id", Big), ("sube_adi", Txt), ("telefon", Txt), ("adres", Txt)),
        }.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The same number in one of the spellings real systems hold.</summary>
    public static string PhoneSpelling(string national, Randomizer r)
    {
        ArgumentNullException.ThrowIfNull(national);
        ArgumentNullException.ThrowIfNull(r);
        var (a, b, c, d) = (national[..3], national[3..6], national[6..8], national[8..]);
        return r.Number(0, 4) switch
        {
            0 => $"0{a} {b} {c} {d}",
            1 => $"+90 {a} {b} {c} {d}",
            2 => $"0{national}",
            3 => $"(0{a}) {b}-{c}{d}",
            _ => national,
        };
    }

    private static string EmailSpelling(string email, Randomizer r) => r.Number(0, 3) switch
    {
        0 => char.ToUpperInvariant(email[0]) + email[1..],
        1 => $" {email.ToUpperInvariant()}",
        _ => email,
    };

    private static string CardNumber(Randomizer r)
    {
        var payload = r.ArrayElement(["454360", "540061", "979200"]) + r.ReplaceNumbers("#########");
        var sum = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            var digit = payload[payload.Length - 1 - i] - '0';
            if (i % 2 == 0)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return payload + ((10 - (sum % 10)) % 10).ToString(CultureInfo.InvariantCulture);
    }

    private static string Ipv4(Randomizer r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.Number(1, 223)}.{r.Number(0, 255)}.{r.Number(0, 255)}.{r.Number(1, 254)}");

    private static string Address(Faker f, Randomizer r) => $"{f.Address.StreetName()} Sok. No:{r.Number(1, 180)} {f.Address.City()}";

    private static string TurkishFold(string text) =>
        new([.. text.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)
            .Select(c => c switch { 'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u', 'İ' => 'i', _ => c })
            .Where(char.IsAsciiLetterOrDigit)]);
}
