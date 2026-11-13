using KvkkDsar.Catalog;
using KvkkDsar.Discovery;
using Npgsql;

namespace KvkkDsar.Synthetic;

/// <summary>
/// Creates and fills the synthetic clinic schema in PostgreSQL. Only primary keys and the application's own TCKN
/// indexes exist, as in a typical legacy schema: PostgreSQL does not index foreign-key columns by itself, and nobody
/// indexed <c>right(regexp_replace(phone, '\D', '', 'g'), 10)</c>. The index advisor and the benchmark start from here.
/// </summary>
public static class ClinicDatabase
{
    public const string Ddl = """
        CREATE TABLE clinic.hastalar (
            id bigint PRIMARY KEY, tc_kimlik_no varchar(11) NOT NULL, ad varchar(100), soyad varchar(100), dogum_tarihi date,
            cinsiyet varchar(1), cep_telefonu varchar(20), eposta varchar(100), adres varchar(250), kan_grubu varchar(8),
            kayit_no varchar(11), kvkk_onay varchar(1), created_at timestamp NOT NULL);
        CREATE INDEX hastalar_tc_kimlik_no_idx ON clinic.hastalar (tc_kimlik_no);
        COMMENT ON TABLE clinic.hastalar IS 'Hasta kayıtları';
        CREATE TABLE clinic.randevular (
            id bigint PRIMARY KEY, hasta_id bigint NOT NULL CONSTRAINT randevular_hasta_fk REFERENCES clinic.hastalar (id),
            doktor_adi varchar(100), bolum_adi varchar(100), randevu_zamani timestamp NOT NULL, oda_no varchar(10), notlar text);
        CREATE TABLE clinic.tanilar (
            id bigint PRIMARY KEY, hasta_id bigint NOT NULL CONSTRAINT tanilar_hasta_fk REFERENCES clinic.hastalar (id),
            icd10_kodu varchar(10), tani_aciklamasi varchar(200), kayit_tarihi date NOT NULL);
        CREATE TABLE clinic.receteler (
            id bigint PRIMARY KEY, hasta_id bigint NOT NULL CONSTRAINT receteler_hasta_fk REFERENCES clinic.hastalar (id),
            ilac_adi varchar(100), doz varchar(10), recete_no varchar(10), recete_tarihi date NOT NULL);
        CREATE TABLE clinic.faturalar (
            id bigint PRIMARY KEY, hasta_id bigint NOT NULL CONSTRAINT faturalar_hasta_fk REFERENCES clinic.hastalar (id),
            fatura_no varchar(16), fatura_tarihi date NOT NULL, tutar numeric(12,2), sigorta_vkn varchar(10), odeme_karti varchar(19));
        CREATE TABLE clinic.genetik_testler (
            id bigint PRIMARY KEY, hasta_id bigint NOT NULL CONSTRAINT genetik_hasta_fk REFERENCES clinic.hastalar (id),
            dna_ornek_kodu varchar(20), sonuc varchar(100), test_tarihi date NOT NULL);
        CREATE TABLE clinic.sosyal_degerlendirme (
            id bigint PRIMARY KEY, hasta_id bigint NOT NULL CONSTRAINT sosyal_hasta_fk REFERENCES clinic.hastalar (id),
            etnik_koken varchar(20), siyasi_gorus varchar(20), mezhep varchar(20), degerlendirme_tarihi date);
        CREATE TABLE clinic.personel (
            id bigint PRIMARY KEY, tckn bigint NOT NULL, ad_soyad varchar(100), birth_date date, gender varchar(10), email varchar(100),
            gsm varchar(20), ikamet_adresi varchar(250), iban varchar(32), maas numeric(12,2), ise_giris_tarihi date, isten_cikis_tarihi date,
            kan_grubu varchar(8), engel_durumu varchar(100), sendika_uyeligi varchar(100), dernek_uyelikleri varchar(100), din varchar(30),
            sabika_kaydi varchar(100), parmak_izi_sablonu bytea, arac_plakasi varchar(12), acil_durum_kisi varchar(100), acil_durum_tel varchar(20));
        CREATE INDEX personel_tckn_idx ON clinic.personel (tckn);
        CREATE TABLE clinic.personel_izinleri (
            id bigint PRIMARY KEY, personel_id bigint NOT NULL CONSTRAINT izin_personel_fk REFERENCES clinic.personel (id),
            izin_turu varchar(20), baslangic_tarihi date, bitis_tarihi date, rapor_tani_kodu varchar(10));
        CREATE TABLE clinic.customers (
            id bigint PRIMARY KEY, full_name varchar(100), email_address varchar(100), email_verified varchar(1), phone_number varchar(20),
            card_number varchar(19), password_hash varchar(64), tc_hash varchar(64), last_login_ip varchar(45), marketing_consent varchar(1),
            created_at timestamp);
        CREATE TABLE clinic.audit_log (
            id bigint PRIMARY KEY, kullanici_adi varchar(40), ip_adresi varchar(45), islem varchar(20), zaman timestamp, user_agent varchar(400));
        CREATE TABLE clinic.subeler (id bigint PRIMARY KEY, sube_adi varchar(100), telefon varchar(20), adres varchar(250));
        """;

    /// <summary>
    /// Least privilege on the source: the finder may only read; the eraser may read, update and delete, never insert or
    /// change the schema. Run by the schema owner after loading.
    /// </summary>
    public const string Grants = """
        GRANT USAGE ON SCHEMA clinic TO dsar_finder, dsar_eraser;
        GRANT SELECT ON ALL TABLES IN SCHEMA clinic TO dsar_finder;
        GRANT SELECT, UPDATE, DELETE ON ALL TABLES IN SCHEMA clinic TO dsar_eraser;
        """;

    /// <summary>Drops and recreates the schema's tables, bulk-loads the dataset with binary COPY and grants access.</summary>
    public static async Task LoadAsync(NpgsqlDataSource owner, ClinicDataset dataset, bool grant = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(dataset);
        await using var connection = await owner.OpenConnectionAsync(cancellationToken);
        await using (var drop = new NpgsqlCommand(string.Join('\n', dataset.InLoadOrder().Reverse().Select(t => $"DROP TABLE IF EXISTS clinic.{t.Name};")) + Ddl, connection))
        {
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var table in dataset.InLoadOrder())
        {
            var columns = string.Join(", ", table.Columns.Select(c => c.Name));
            await using var import = await connection.BeginBinaryImportAsync($"COPY clinic.{table.Name} ({columns}) FROM STDIN (FORMAT BINARY)", cancellationToken);
            foreach (var row in table.Rows)
            {
                await import.StartRowAsync(cancellationToken);
                for (var i = 0; i < row.Length; i++)
                {
                    if (row[i] is null)
                    {
                        await import.WriteNullAsync(cancellationToken);
                    }
                    else
                    {
                        await import.WriteAsync(row[i], table.Columns[i].Type, cancellationToken);
                    }
                }
            }

            await import.CompleteAsync(cancellationToken);
        }

        await using (var analyze = new NpgsqlCommand("ANALYZE;" + (grant ? Grants : ""), connection))
        {
            await analyze.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>The same dataset as an in-memory source with an equivalent catalog, for unit tests and parity checks.</summary>
    public static InMemorySource InMemory(ClinicDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var source = new InMemorySource(Catalog());
        foreach (var table in dataset.Tables.Values)
        {
            foreach (var row in table.Rows)
            {
                source.Add(table.Name, table.RowAsDictionary(row));
            }
        }

        return source;
    }

    /// <summary>The catalog PostgreSQL reports for <see cref="Ddl"/>, written out for database-free tests.</summary>
    public static SourceCatalog Catalog()
    {
        static ColumnInfo V(string n, int len, bool nullable = true) => new(n, LogicalType.Text, "character varying", len, nullable);
        static ColumnInfo B(string n, bool nullable = false) => new(n, LogicalType.WholeNumber, "bigint", null, nullable);
        static ColumnInfo D(string n, bool nullable = true) => new(n, LogicalType.Date, "date", null, nullable);
        static ColumnInfo Ts(string n, bool nullable = true) => new(n, LogicalType.Timestamp, "timestamp without time zone", null, nullable);
        static ColumnInfo N(string n) => new(n, LogicalType.ExactNumeric, "numeric", null, true);
        static TableInfo T(string n, params ColumnInfo[] c) => new(n, "id", c);

        return new SourceCatalog(ClinicDataset.Schema,
        [
            T("audit_log", B("id"), V("kullanici_adi", 40), V("ip_adresi", 45), V("islem", 20), Ts("zaman"), V("user_agent", 400)),
            T("customers", B("id"), V("full_name", 100), V("email_address", 100), V("email_verified", 1), V("phone_number", 20), V("card_number", 19),
                V("password_hash", 64), V("tc_hash", 64), V("last_login_ip", 45), V("marketing_consent", 1), Ts("created_at")),
            T("faturalar", B("id"), B("hasta_id"), V("fatura_no", 16), D("fatura_tarihi", false), N("tutar"), V("sigorta_vkn", 10), V("odeme_karti", 19)),
            T("genetik_testler", B("id"), B("hasta_id"), V("dna_ornek_kodu", 20), V("sonuc", 100), D("test_tarihi", false)),
            T("hastalar", B("id"), V("tc_kimlik_no", 11, false), V("ad", 100), V("soyad", 100), D("dogum_tarihi"), V("cinsiyet", 1), V("cep_telefonu", 20),
                V("eposta", 100), V("adres", 250), V("kan_grubu", 8), V("kayit_no", 11), V("kvkk_onay", 1), Ts("created_at", false)),
            T("personel", B("id"), B("tckn"), V("ad_soyad", 100), D("birth_date"), V("gender", 10), V("email", 100), V("gsm", 20), V("ikamet_adresi", 250),
                V("iban", 32), N("maas"), D("ise_giris_tarihi"), D("isten_cikis_tarihi"), V("kan_grubu", 8), V("engel_durumu", 100), V("sendika_uyeligi", 100),
                V("dernek_uyelikleri", 100), V("din", 30), V("sabika_kaydi", 100), new ColumnInfo("parmak_izi_sablonu", LogicalType.Binary, "bytea", null, true),
                V("arac_plakasi", 12), V("acil_durum_kisi", 100), V("acil_durum_tel", 20)),
            T("personel_izinleri", B("id"), B("personel_id"), V("izin_turu", 20), D("baslangic_tarihi"), D("bitis_tarihi"), V("rapor_tani_kodu", 10)),
            T("randevular", B("id"), B("hasta_id"), V("doktor_adi", 100), V("bolum_adi", 100), Ts("randevu_zamani", false), V("oda_no", 10),
                new ColumnInfo("notlar", LogicalType.LongText, "text", null, true)),
            T("receteler", B("id"), B("hasta_id"), V("ilac_adi", 100), V("doz", 10), V("recete_no", 10), D("recete_tarihi", false)),
            T("sosyal_degerlendirme", B("id"), B("hasta_id"), V("etnik_koken", 20), V("siyasi_gorus", 20), V("mezhep", 20), D("degerlendirme_tarihi")),
            T("subeler", B("id"), V("sube_adi", 100), V("telefon", 20), V("adres", 250)),
            T("tanilar", B("id"), B("hasta_id"), V("icd10_kodu", 10), V("tani_aciklamasi", 200), D("kayit_tarihi", false)),
        ],
        [
            new ForeignKey("faturalar_hasta_fk", "faturalar", "hasta_id", "hastalar", "id"),
            new ForeignKey("genetik_hasta_fk", "genetik_testler", "hasta_id", "hastalar", "id"),
            new ForeignKey("izin_personel_fk", "personel_izinleri", "personel_id", "personel", "id"),
            new ForeignKey("randevular_hasta_fk", "randevular", "hasta_id", "hastalar", "id"),
            new ForeignKey("receteler_hasta_fk", "receteler", "hasta_id", "hastalar", "id"),
            new ForeignKey("sosyal_hasta_fk", "sosyal_degerlendirme", "hasta_id", "hastalar", "id"),
            new ForeignKey("tanilar_hasta_fk", "tanilar", "hasta_id", "hastalar", "id"),
        ]);
    }
}
