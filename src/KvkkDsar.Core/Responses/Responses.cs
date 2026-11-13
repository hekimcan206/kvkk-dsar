using System.Globalization;
using System.Text;
using System.Text.Json;
using KvkkDsar.Discovery;
using KvkkDsar.Inventory;
using KvkkDsar.Planning;
using KvkkDsar.Workflow;

namespace KvkkDsar.Responses;

public enum Language
{
    Tr,
    En,
}

public sealed record LetterModel(
    string Reference,
    ControllerInfo Controller,
    string SubjectName,
    IReadOnlyList<RequestType> Types,
    DateOnly ReceivedOn,
    DateOnly DueOn,
    DateOnly RespondedOn,
    DateOnly ComplaintBy,
    DiscoveryResult Discovery,
    ErasurePlan? Plan,
    DsarPolicy Policy,
    string? AccessExportSha256);

/// <summary>
/// The written answer (KVKK Art. 13; the Board's October 2026 announcement repeats that a phone call is not an
/// answer). Built only from the discovery, the plan and the policy, so it says exactly what was done: what was found
/// where, what was erased or anonymised, what was kept, on which legal basis and until when, who was notified, and how
/// to complain. Deterministic, so it is golden-tested.
/// </summary>
public static class ResponseLetter
{
    public static string Render(LetterModel m, Language lang)
    {
        ArgumentNullException.ThrowIfNull(m);
        var tr = lang == Language.Tr;
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        string D(DateOnly d) => d.ToString(tr ? "dd.MM.yyyy" : "yyyy-MM-dd", CultureInfo.InvariantCulture);

        Line(tr ? $"# Kişisel veri başvurunuza yanıt — {m.Reference}" : $"# Response to your personal data request — {m.Reference}");
        Line();
        Line($"**{m.Controller.Name}** · {m.Controller.Address} · {m.Controller.ContactEmail} · KEP: {m.Controller.Kep}");
        Line();
        Line(tr ? $"Sayın {m.SubjectName}," : $"Dear {m.SubjectName},");
        Line();
        Line(tr
            ? $"{D(m.ReceivedOn)} tarihinde bize ulaşan, 6698 sayılı Kişisel Verilerin Korunması Kanunu'nun (KVKK) 11. maddesi kapsamındaki başvurunuzu ({string.Join(", ", m.Types.Select(t => TypeName(t, tr)))}) inceledik. Yasal süre {D(m.DueOn)} tarihinde dolmaktadır; yanıtımız {D(m.RespondedOn)} tarihlidir. Başvurunuz ücretsiz olarak sonuçlandırılmıştır."
            : $"We have reviewed your request under Article 11 of Law No. 6698 on the Protection of Personal Data (KVKK) received on {D(m.ReceivedOn)} ({string.Join(", ", m.Types.Select(t => TypeName(t, tr)))}). The legal deadline is {D(m.DueOn)}; this response is dated {D(m.RespondedOn)}. No fee is charged.");
        Line();

        Line(tr ? "## Kişisel verileriniz nerede işleniyor" : "## Where your personal data is processed");
        Line();
        var found = m.Discovery.OwnedCountByTable();
        if (found.Count == 0 && m.Discovery.Mentions.Count == 0)
        {
            Line(tr ? "Verdiğiniz kimlik bilgileriyle sistemlerimizde size ait bir kayıt bulunamadı." : "We found no records about you using the identifiers you gave.");
        }
        else
        {
            Line(tr ? "| Kayıt grubu | Kayıt sayısı | Veri kategorileri | Amaç | Hukuki sebep |" : "| Record group | Records | Data categories | Purpose | Legal basis |");
            Line("|---|---:|---|---|---|");
            foreach (var (table, count) in found)
            {
                var rule = m.Policy.RuleFor(table);
                var categories = m.Plan?.Actions.FirstOrDefault(a => a.Table == table)?.Categories ?? [];
                Line($"| {table} | {count} | {(categories.Count == 0 ? "—" : Cell(string.Join(", ", categories)))} | {Cell(rule.Purpose)} | {Cell(rule.LegalBasis)} |");
            }

            if (m.Discovery.Mentions.Count > 0)
            {
                Line();
                Line(tr
                    ? $"Ayrıca iletişim bilginiz başka kişilere ait {m.Discovery.Mentions.Count} kayıtta geçmektedir (ör. acil durum kişisi, not). Bu kayıtların geri kalanı başkalarına ait olduğundan yalnızca sizinle ilgili kısım işleme alınmıştır."
                    : $"Your contact details also appear in {m.Discovery.Mentions.Count} record(s) about other people (e.g. as an emergency contact or in a note). The rest of those records is about someone else, so only the part about you was handled.");
            }
        }

        Line();
        if (m.Types.Contains(RequestType.Access))
        {
            Line(tr ? "## Erişim talebiniz" : "## Your access request");
            Line();
            Line(tr
                ? $"Hakkınızda işlenen verilerin dökümü bu yanıtın ekidir (SHA-256: `{m.AccessExportSha256}`). Başka kişilere ait kayıtlarda geçen bilgileriniz için o kayıtların içeriği, ilgili kişilerin haklarını korumak amacıyla paylaşılmamıştır."
                : $"A copy of the data we process about you is attached (SHA-256: `{m.AccessExportSha256}`). Where your details appear in records about other people, the rest of those records is withheld to protect their rights.");
            Line();
        }

        if (m.Plan is { } plan)
        {
            Line(tr ? "## Silme talebiniz" : "## Your erasure request");
            Line();
            foreach (var group in plan.Actions.GroupBy(a => a.Kind).OrderBy(g => g.Key))
            {
                var tables = string.Join(", ", group.GroupBy(a => a.Table).Select(g => $"{g.Key} ({g.Count()})"));
                Line(group.Key switch
                {
                    PlannedActionKind.Erase => tr ? $"- **Silindi:** {tables}." : $"- **Erased:** {tables}.",
                    PlannedActionKind.Anonymise => tr ? $"- **Anonim hale getirildi** (kişisel veri alanları temizlendi, kayıt istatistik amacıyla kaldı): {tables}." : $"- **Anonymised** (personal-data fields cleared, the record remains for statistics): {tables}.",
                    PlannedActionKind.RedactMention => tr ? $"- **Başka kişilerin kayıtlarındaki bilgileriniz silindi:** {tables}." : $"- **Your details removed from other people's records:** {tables}.",
                    _ => tr ? $"- **Saklanmaya devam ediyor:** {tables}." : $"- **Kept:** {tables}.",
                });
            }

            var retained = plan.Actions.Where(a => a.Kind == PlannedActionKind.Retain).GroupBy(a => (a.Table, a.LegalBasis)).ToList();
            if (retained.Count > 0)
            {
                Line();
                Line(tr
                    ? "Aşağıdaki kayıtlar, kanunlarda öngörülen saklama yükümlülüğü (KVKK m. 5/2-ç) nedeniyle şimdilik silinemez; bu kısım için talebiniz reddedilmiştir. Süre dolduğunda periyodik imha kapsamında silinecektir. Saklama süresince bu veriler başka amaçla işlenmez."
                    : "The records below cannot be erased yet because the law requires us to keep them (KVKK Art. 5(2)(ç)); your request is refused for this part. They will be destroyed in the periodic destruction run once the period ends, and are not processed for any other purpose meanwhile.");
                Line();
                Line(tr ? "| Kayıt grubu | Kayıt | Hukuki sebep | En geç saklama |" : "| Record group | Records | Legal basis | Kept until at most |");
                Line("|---|---:|---|---|");
                foreach (var g in retained)
                {
                    var until = g.Max(a => a.RetainUntil);
                    var open = g.Any(a => a.RetainUntil is null);
                    var untilText = open ? (tr ? "ilişki sona erdikten sonra hesaplanır" : "counted from the end of the relationship") : D(until!.Value);
                    Line($"| {g.Key.Table} | {g.Count()} | {Cell(g.Key.LegalBasis ?? "")} | {untilText} |");
                }

                var cleared = plan.Actions.Where(a => a.Kind == PlannedActionKind.Retain && a.Changes.Count > 0)
                    .SelectMany(a => a.Changes.Select(c => $"{a.Table}.{c.Column}")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (cleared.Count > 0)
                {
                    Line();
                    Line(tr
                        ? $"Saklanan kayıtlarda saklama amacı için gerekli olmayan alanlar yine de silinmiştir: {string.Join(", ", cleared)}."
                        : $"Fields of the kept records that the retention purpose does not need were still erased: {string.Join(", ", cleared)}.");
                }
            }

            Line();
            Line(plan.NotifyRecipients.Count == 0
                ? (tr ? "Verileriniz üçüncü kişilere aktarılmadığından bildirim yapılması gereken bir alıcı yoktur (KVKK m. 11/1-f)." : "Your data was not transferred to third parties, so there is no recipient to notify (KVKK Art. 11(1)(f)).")
                : (tr ? $"Silme işlemi, verilerinizin aktarıldığı alıcılara bildirilmiştir (KVKK m. 11/1-f): {string.Join("; ", plan.NotifyRecipients)}." : $"The erasure has been notified to the recipients your data was transferred to (KVKK Art. 11(1)(f)): {string.Join("; ", plan.NotifyRecipients)}."));
            Line();
        }

        if (m.Types.Any(t => t is RequestType.Rectification or RequestType.Objection or RequestType.Compensation))
        {
            Line(tr ? "## Diğer talepleriniz" : "## Your other requests");
            Line();
            Line(tr ? "Düzeltme, itiraz ve tazminat talepleriniz ilgili birim tarafından incelenmiş olup sonucu bu yanıtın ekinde ayrıca bildirilmiştir." : "Your rectification, objection or compensation requests were reviewed by the responsible team; the outcome is attached separately.");
            Line();
        }

        if (m.Discovery.Unreachable.Count > 0)
        {
            Line(tr ? "## Aranamayan kayıtlar" : "## Records that could not be searched");
            Line();
            Line(tr
                ? $"Şu kayıt gruplarında kimlik numarası, e-posta veya telefon tutulmadığı için size ait kayıtlar aranamamıştır: {string.Join(", ", m.Discovery.Unreachable.Select(u => u.Table))}. Bu kayıtlar saklama ve imha politikamızdaki sürelere göre periyodik olarak imha edilir."
                : $"These record groups hold no ID number, e-mail or phone, so they could not be searched for you: {string.Join(", ", m.Discovery.Unreachable.Select(u => u.Table))}. They are destroyed periodically under our retention and destruction policy.");
            Line();
        }

        Line(tr ? "## Şikâyet hakkınız" : "## Your right to complain");
        Line();
        Line(tr
            ? $"Yanıtımızı yetersiz bulursanız, KVKK m. 14 uyarınca yanıtı öğrendiğiniz tarihten itibaren 30 gün ve her hâlde başvuru tarihinden itibaren 60 gün içinde (en geç {D(m.ComplaintBy)}) Kişisel Verileri Koruma Kurulu'na şikâyette bulunabilirsiniz."
            : $"If you find this response insufficient, you may complain to the Personal Data Protection Board within 30 days of receiving it and in any case within 60 days of your application (by {D(m.ComplaintBy)} at the latest), under KVKK Art. 14.");
        Line();
        Line(tr ? $"Saygılarımızla,  \n{m.Controller.Name} — Veri Sorumlusu" : $"Kind regards,  \n{m.Controller.Name} — Data Controller");
        return sb.ToString();
    }

    public static string TypeName(RequestType type, bool tr) => (type, tr) switch
    {
        (RequestType.Access, true) => "bilgi ve erişim (m. 11/1-a, b, c, ç)",
        (RequestType.Erasure, true) => "silme (m. 11/1-e, f)",
        (RequestType.Rectification, true) => "düzeltme (m. 11/1-d, f)",
        (RequestType.Objection, true) => "itiraz (m. 11/1-g)",
        (RequestType.Compensation, true) => "tazminat (m. 11/1-ğ)",
        (RequestType.Access, false) => "access (Art. 11(1)(a)-(ç))",
        (RequestType.Erasure, false) => "erasure (Art. 11(1)(e), (f))",
        (RequestType.Rectification, false) => "rectification (Art. 11(1)(d), (f))",
        (RequestType.Objection, false) => "objection (Art. 11(1)(g))",
        _ => "compensation (Art. 11(1)(ğ))",
    };

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}

/// <summary>
/// The access export for the data subject (KVKK Art. 11(1)(a)-(ç)): every personal-data column of every owned row,
/// grouped by record group with purpose, legal basis and recipients from the policy. Mentions show only that the
/// subject's identifier appears, never the rest of another person's record.
/// </summary>
public static class AccessExport
{
    // Relaxed escaping: the export is read by a person; Turkish letters stay letters instead of  escapes.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Field(string Column, string Category, bool Special, string? Value);

    public sealed record Record(string Key, IReadOnlyList<Field> Fields);

    public sealed record Group(string Table, string Purpose, string LegalBasis, IReadOnlyList<string> Recipients, IReadOnlyList<Record> Records);

    public sealed record MentionNote(string Table, string Column, string Note);

    public sealed record Document(string Reference, DateTimeOffset GeneratedAt, string Controller, IReadOnlyList<Group> Groups, IReadOnlyList<MentionNote> Mentions);

    /// <summary>Which columns to read per table: the inventory's personal-data columns.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ColumnsToRead(DiscoveryResult discovery, DataInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(inventory);
        return discovery.Owned.Select(o => o.Table).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t, t => (IReadOnlyList<string>)[.. inventory.ColumnsOf(t).Select(c => c.Column).Distinct(StringComparer.OrdinalIgnoreCase)], StringComparer.OrdinalIgnoreCase);
    }

    public static byte[] Build(string reference, DateTimeOffset generatedAt, DiscoveryResult discovery, DataInventory inventory, DsarPolicy policy,
        IReadOnlyDictionary<(string Table, string Key), IReadOnlyDictionary<string, string?>> values)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(values);

        var groups = discovery.Owned.GroupBy(o => o.Table).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var rule = policy.RuleFor(g.Key);
            var columns = inventory.ColumnsOf(g.Key).GroupBy(c => c.Column, StringComparer.OrdinalIgnoreCase).Select(c => c.First()).ToList();
            var records = g.OrderBy(o => o.Key, KeyComparer.Instance).Select(o =>
            {
                var row = values.GetValueOrDefault((o.Table, o.Key));
                return new Record(o.Key, [.. columns.Select(c => new Field(c.Column, c.CategoryName, c.Special, row?.GetValueOrDefault(c.Column)))]);
            }).ToList();
            return new Group(g.Key, rule.Purpose, rule.LegalBasis, rule.Recipients, records);
        }).ToList();

        var mentions = discovery.Mentions
            .Where(m => !discovery.Owned.Any(o => o.Table == m.Table && o.Key == m.Key))
            .Select(m => new MentionNote(m.Table, m.Column, $"Your {m.Kind} appears in a record about another person; the rest of that record is withheld."))
            .ToList();

        return JsonSerializer.SerializeToUtf8Bytes(new Document(reference, generatedAt, policy.Controller.Name, groups, mentions), Json);
    }
}
