# Marketplace Reporting

ASP.NET Core MVC app hosting two Mirakl marketplace reports on a single page, with tab navigation
between them and results rendered in place.

| Module | Input | Output |
|---|---|---|
| **Order Report** (Late Shipment & Cancellation) | `orders.xlsx` | In-page dashboard (20 KPIs, 8 charts, 5 tables, 2 ranked lists) + 4-sheet Excel workbook |
| **Return SLA Report** | `orders` export + 2 return tracking templates (`.xlsx` or `.csv`) | In-page dashboard (6 KPIs, 4 tables) |
| **Data & Methodology** | — | Reference page: source column per metric, calculation rules, known export traps, limits |

The Order Report dashboard has two layers. *Key metrics* down to *Late shipment & cancellation —
top 5 sellers* is the original port and is covered by the guarantees below. Below that sit the
extended sections — delivery quality, cancellation/rejection/refund breakdown, lead-time (SLA)
analysis, category performance and data quality. They read **optional** columns, so an export
without them still renders; each affected section shows an empty state naming the missing column,
and a banner above *Key metrics* lists them all.

## Run

```bash
dotnet run --project src/YeniRPA.Web
```

Then open <http://localhost:5080>.

Requires the .NET 10 SDK. `ClosedXML` is the only NuGet dependency; Chart.js and the IBM Plex fonts
are vendored under `wwwroot/lib`, so the app has **no external network dependencies at runtime**.

## Layout

```
src/YeniRPA.Web/
├── Services/
│   ├── OrderReportBuilder.cs        Build() -> xlsx, BuildData() -> dashboard JSON
│   └── ReturnSlaReportBuilder.cs    BuildData() -> dashboard JSON; xlsx/csv readers
├── Models/ReportModels.cs           JSON contract with the dashboard JavaScript
│                                    (terse row fields; extended ones omitted when default)
├── Models/MethodologyViewModel.cs   Reads rules off the builders for the Methodology page
├── Controllers/                     Home + one controller per report
├── Infrastructure/                  400 { error } filter for input-validation failures
├── Views/Home/Index.cshtml          The single page: both modules, one visible at a time
└── wwwroot/
    ├── css/app.css                  Design tokens, light + dark themes
    ├── js/app.js                    Shell: nav, theme, uploads, fetch
    ├── js/order-report.js           Order dashboard aggregation + charts
    └── js/return-sla-report.js      Return SLA dashboard
```

## API

| Method | Route | Body | Returns |
|---|---|---|---|
| `POST` | `/api/order-report/data` | `file` | Dashboard JSON |
| `POST` | `/api/order-report/excel` | `file` | `Gec Kargolama ve Iptal Raporu.xlsx` |
| `POST` | `/api/return-sla-report/data` | `orders`, `templateA`, `templateB` | Dashboard JSON |

Input-validation failures return `400 { "error": "..." }` with the message naming the exact problem,
e.g. `Required column 'Shipping deadline' was not found in the uploaded file.`

## Notes for maintainers

**Most report rules are a verbatim port** from the previous RPA project and were verified to produce
byte-identical output. Specifically:

- Late = shipping date present **and** later than the shipping deadline. Rows with no shipping date
  are excluded from the late-rate denominator.
- Carriers count as integrated when the name contains `Aras`, `Yurtici`, `Yurtiçi`, `DHL` or
  `Hepsijet`.
- Return SLA is **15 days** from the ship-back date, early warning at **10 days**. Return template A
  has no ship-date column, so `Talep Tarihi` is used as a proxy.
- `pastWarning` is deliberately not gated on `isConfirmedReturn`, matching the original.

**One rule was deliberately changed away from the port.** *Key metrics* used to report *shipped
orders*, which counted rows with a shipping date rather than rows with a status. The outcome counts
on that row — received, rejected, canceled — now come from the `Status` column alone
(`ReceivedStatus` / `RejectedStatus` / `CanceledStatus` on `OrderReportBuilder`), and each is also
shown as a share of all order lines. The late-shipment rate is the one metric that keeps the
narrower "lines that have actually shipped" denominator, because a line with no shipping date is
neither late nor on time. The Excel workbook's Summary sheet mirrors the same nine boxes, so the two
outputs cannot tell different stories — its late-rate formula inlines the shipped-line `COUNTIF`
rather than pointing at a KPI cell.

The extended Order Report sections are **additive** and must stay that way: nothing under
`renderExtended()` feeds a ported metric, so the original numbers cannot drift when they change.

### What the export gets wrong, and what the extended sections do about them

These are properties of the Mirakl export, not bugs in this app. Each one silently corrupts a total
if the column is summed naively, so the handling is deliberate and should not be "simplified" away.

- **Canceled lines keep their payout.** `Amount transferred to seller (including taxes)` is *not*
  cleared when a line is canceled — it keeps showing what would have been paid. Summing the raw
  column overstates settlement (by ~22% on the sample export), so it is never summed as a headline
  figure. *Data quality* counts the canceled lines that still carry a payout and names the amount.
- **A refunded line keeps its `Amount`.** Unlike a cancellation (which zeroes `Amount`), a refund
  leaves revenue in place, so a refunded line reads as zero loss if you go by `Amount`. The
  *lost amount* column on the line-level cancellation table comes from `Total canceled amount` +
  `Total refunded amount` instead.
- **`Total order taxes` is not VAT.** It repeats the Turkish withholding tax value exactly, and the
  `… (VAT - vat)` columns are all zero. VAT cannot be split out of revenue from this file. Nothing
  reads the commission or withholding-tax columns any more; do not reintroduce a VAT figure derived
  from them.

Two more rules worth knowing:

- Lines whose `Reason` is `Received automatically` were closed by the platform without a carrier
  delivery notification; their received date is a bulk timestamp. Every duration in *Delivery
  quality* excludes them. The *Avg. transit on auto-closed lines* card measures that cohort **on its
  own** — 24.3 days against 2.5 for real deliveries on the current export — which is why they cannot
  be averaged in. Note that this card is *not* the blended figure: averaging both sets together
  would read 3.5 days, so do not relabel it as "what the average would have been". The ported
  *Avg. hours to receive* KPIs do **not** exclude them, which is why both are shown.
- Lead-time advice uses the **90th percentile** of a seller's shipping times, not the mean, and is
  computed per seller **and** per promised lead time. Averaging a seller's lead times together would
  recommend cutting a 15-day white-goods promise to a day because their accessory orders ship fast.

Payload shape: rows carry terse field names, extended fields are omitted when they hold their default
value, and category/brand/city travel as indexes into dictionaries on the payload rather than as
strings on every row. Index 0 in each dictionary is the "unknown" slot.

### Keeping the Methodology page honest

The Methodology tab documents these rules for the operator. It must not become a second, stale copy
of them, so every threshold and code list on it is read from the code that applies it —
`MethodologyViewModel` pulls `RequiredColumns`, `OptionalColumns`, `IntegratedCarrierKeywords`,
`ReasonLabels`, the status strings, `MinSampleSize` / `MinLeadTimeSample` and the return SLA day
counts straight off the builders. The two sample-size thresholds also travel in the dashboard payload
so the JavaScript uses the same values rather than its own literals.

When you change a rule, change it in the builder. The page follows. Prose that cannot be derived from
code (what a metric *means*, why a denominator excludes something) is written by hand in the view and
does need updating alongside.

Two things that look like bugs but are pre-existing behaviour, left unchanged so the numbers keep
matching the old reports:

- `ConfirmedReturnKeywords` contains `"iade"`, which does **not** match the spelling `"İade"`
  (Turkish dotted capital İ lowercases to `i` + U+0307).
- `"ret"` is a loose substring match.

**Column names read from uploaded files are Turkish on purpose** (`SiparişNo`, `Kargo Takip Kodu`,
`YK Takip Kodu`, `Kargo Kodu Oluşturma Tarihi`, …). They are data, not UI text — never translate
them. Save source files as UTF-8.

The Excel `Data` sheet's column **order is load-bearing**: the Summary and Top-5 sheets reference it
by column letter (`Data!K`, `Data!I`, …), and columns K, L, O, P, Q are live formulas.
