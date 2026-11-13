# 0002. The 30-day deadline: counting, holidays and no pause

- Status: accepted
- Date: 2026-11-13

## Context

KVKK Art. 13(2) requires the controller to conclude an application "as soon as possible and within thirty days at the
latest", free of charge (a fee per the Board's tariff only where the answer causes extra cost; the Communiqué makes
written answers up to ten pages free). The Regulation on Erasure, Destruction or Anonymisation (Art. 12) repeats the
30 days for erasure requests. Art. 14 gives the subject 30 days from the answer, and in any case 60 days from the
application, to complain to the Board. Three things are not spelled out in KVKK itself:

1. **Counting.** Turkish private law counts day periods without the starting day (TBK Art. 92).
2. **Weekends and holidays.** TBK Art. 93 and HMK Art. 93 move a deadline that ends on an official holiday to the next
   working day; whether that applies to a controller's KVKK deadline is not settled.
3. **Pausing.** The Communiqué lets the controller ask the applicant questions; nothing says the clock stops meanwhile.

## Options considered

- Count calendar days from the receipt instant (24 h × 30): simple, but "received on 1 March at 17:00, due 31 March at
  17:00" is not how anyone reads a legal deadline.
- Count local dates, skip the receipt day, end at the end of the local day 30 (chosen as the base).
- For the last day: always strict, always roll, or configurable with strict as the default (chosen).
- Pause the clock while identity is being verified (rejected: no legal basis found; a controller who pauses and is
  wrong is late).

## Decision

- Dates are Türkiye local dates. Türkiye has used UTC+03:00 all year since September 2016, so a fixed offset is used
  instead of a tz database (the chiseled runtime image has none).
- `DueOn = ReceivedOn + 30`; `DueBy` is the last tick of `DueOn` in Türkiye.
- `DeadlinePolicy.Strict` (default): day 30 is the deadline even on a Sunday or a bayram. A response sent early is
  never late.
- `DeadlinePolicy.RollToNextWorkingDay` (opt-in, needs legal sign-off): Saturdays, Sundays and official holidays roll
  forward; arife half days count as working days. The holiday table (fixed holidays plus Ramazan and Kurban Bayramı
  for 2025-2027) refuses to answer for years it does not cover instead of guessing.
- The clock never pauses. Requests enter *at risk* with 7 days left and *overdue* after `DueBy`; the deadline monitor
  writes each transition once to the audit chain and exposes open requests by stage as a metric.
- The answer must be written (the Board's October 2026 announcement repeats that a phone call is not an answer), so
  "responded" means the letter was generated and recorded.

## Consequences

- Golden cases cover a receipt at 22:30 UTC (already the next day in Türkiye), the 2026
  and 2027 bayrams and arife days; a 1,000-case FsCheck property checks: strict due = received + 30, rolled due is a
  working day with only non-working days skipped, at most 9 days of rolling, `DueBy` falls on the due date.
- The religious holiday table must be extended every year (dates follow the Hijri calendar and are announced by the
  Presidency of Religious Affairs); with `Strict` nothing breaks when it is not.
- The complaint window shown in letters takes the earlier of the two Art. 14 limits; that reading is conservative and
  informational only.

## Sources

- KVKK Art. 13 and 14; Communiqué on Applications (fee and written answer): <https://www.alomaliye.com/2018/03/10/veri-sorumlusuna-basvuru/amp/>
- Regulation on Erasure, Destruction or Anonymisation of Personal Data (RG 28.10.2017, No. 30224), Art. 12:
  <https://www.kvkk.gov.tr/Icerik/5441/KISISEL-VERILERIN-SILINMESI-YOK-EDILMESI-VEYA-ANONIM-HALE-GETIRILMESI-HAKKINDA-YONETMELIK>
- TBK Art. 92 and 93: <https://av-saimincekas.com/kanun/turk-borclar-kanunu/madde-92-tbk/>,
  <https://av-saimincekas.com/kanun/turk-borclar-kanunu/madde-93-tbk/>; HMK Art. 91-94 commentary:
  <https://or.av.tr/ozel-hukukta-sureler-hmk-m-91-92-93-94/>
- Official holidays 2026: <https://www.cnnturk.com/guncel/2026da-hangi-gunler-resmi-tatil-liste-liste-resmi-tatil-gunleri-2026-2362840>;
  2027: <https://www.cnnturk.com/turkiye/2027-resmi-tatiller-takvimi-dini-bayramlar-resmi-tatil-gunler-ne-zaman-2027-ramazan-ve-kurban-bayrami-tarihleri-3447415>,
  <https://www.harbiyehukuk.com/yili-resmi-tatil-gunleri>
- KVKK announcement on written answers (October 2026), as reported: <https://www.alomaliye.com/2026/10/01/kvkk-basvurularinda-sozlu-yanit-yeterli-degil/>
