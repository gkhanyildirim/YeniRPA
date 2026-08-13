# Marketplace Reporting

ASP.NET Core MVC app hosting the Mirakl marketplace reports and automation modules on a single page,
with tab navigation between them and results rendered in place.

| Module | Input | Output |
|---|---|---|
| **Order Report** (Late Shipment & Cancellation) | `orders.xlsx` | In-page dashboard (20 KPIs, 8 charts, 5 tables, 2 ranked lists) + 4-sheet Excel workbook |
| **Return SLA Report** | `orders` export + 2 return tracking templates (`.xlsx` or `.csv`) | In-page dashboard (6 KPIs, 4 tables) |
| **Create Return** | The two return templates + the returns and orders exports — or a ready `.xlsx` with the order ID in column A and the tracking number in column B | Reviewable list (funnel, ready rows, what was dropped), then files a return on Mirakl per row with a live run log |
| **Data & Methodology** | — | Reference page: source column per metric, calculation rules, known export traps, limits |

The reports are read-only: they never leave the machine and nothing is stored. **Create Return is
not** — it drives a real browser against the Mirakl back office and writes to the marketplace. See
[Create Return](#create-return-automation).

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

Requires the .NET 10 SDK. `ClosedXML` and `Microsoft.Playwright` are the only NuGet dependencies;
Chart.js and the IBM Plex fonts are vendored under `wwwroot/lib`, so the app has **no external
network dependencies at runtime**. Playwright is needed only by Create Return — the report modules
never touch it, and it launches no browser until that module is used.

## Layout

```
src/YeniRPA.Web/
├── Services/
│   ├── OrderReportBuilder.cs        Build() -> xlsx, BuildData() -> dashboard JSON
│   ├── ReturnSlaReportBuilder.cs    BuildData() -> dashboard JSON
│   ├── ReturnListBuilder.cs         4 exports -> the Create Return input list
│   ├── TabularFile.cs               xlsx/csv -> row-column table, shared by both
│   └── Automation/
│       ├── AutomationJobBus.cs      Single-run lock + SSE progress fan-out
│       ├── MiraklBrowser.cs         Playwright browser + encrypted saved login
│       └── CreateReturnRunner.cs    The Create Return flow, one order at a time
├── Models/ReportModels.cs           JSON contract with the dashboard JavaScript
│                                    (terse row fields; extended ones omitted when default)
├── Models/MethodologyViewModel.cs   Reads rules off the builders for the Methodology page
├── Controllers/                     Home, one per report, Automation (session + events)
├── Infrastructure/                  400 { error } filter for input-validation failures
├── Views/Home/Index.cshtml          The single page: every module, one visible at a time
└── wwwroot/
    ├── css/app.css                  Design tokens, light + dark themes
    ├── js/app.js                    Shell: nav, theme, uploads, fetch
    ├── js/order-report.js           Order dashboard aggregation + charts
    ├── js/return-sla-report.js      Return SLA dashboard
    └── js/create-return.js          Create Return: session, upload, live run log
```

## API

| Method | Route | Body | Returns |
|---|---|---|---|
| `POST` | `/api/order-report/data` | `file` | Dashboard JSON |
| `POST` | `/api/order-report/excel` | `file` | `Gec Kargolama ve Iptal Raporu.xlsx` |
| `POST` | `/api/return-sla-report/data` | `orders`, `templateA`, `templateB` | Dashboard JSON |
| `POST` | `/api/create-return/prepare` | `templateA`, `templateB`, `returns`, `orders`, `from`, `to`, `returnsOnly` | Prepared list JSON: ready rows, dropped rows, funnel |
| `POST` | `/api/create-return/list/excel` | JSON `{ rows }` | Two-column `.xlsx` |
| `POST` | `/api/create-return/start-list` | JSON `{ rows }` | `{ count }`; the run continues in the background |
| `POST` | `/api/create-return/start` | `file` | `{ count }`; the run continues in the background |
| `GET` | `/api/automation/status` | — | `{ hasSession, browserReady, isRunning, runningModule }` |
| `POST` | `/api/automation/login` \| `save-session` \| `clear-session` | — | `200` |
| `GET` | `/api/automation/events` | — | `text/event-stream` of run progress |

Input-validation failures return `400 { "error": "..." }` with the message naming the exact problem,
e.g. `Required column 'Shipping deadline' was not found in the uploaded file.`

## Create Return automation

### Preparing the list

`ReturnListBuilder` replaces a manual Excel session. Four exports go in — return template A
(*Marketplace Iade & Degisim Talepleri*), return template B (the *…MP* file), the returns export and
the orders export — and out comes the two-column list the automation runs on, alongside a funnel and
a table of everything that was dropped and why. Nothing is read from disk; all four are uploaded.

Per template: keep rows with a real tracking code, keep rows inside the date range, drop **every**
copy of an order number that appears more than once, optionally keep only `İade` requests, and drop
anything the returns export already covers. The two lists are then merged (an order in both is
dropped from both) and matched against the orders export for the full `01259_311911494-A` form.

Four things about this are load-bearing:

- **The MP export writes the literal text `NULL` into `YK Takip Kodu`** on roughly three rows out of
  four (1991 of 2685 on the sample export). Testing "is it empty" would file the word NULL as a
  tracking number. Every real code on both templates is digits only, so `ReadTracking` treats
  `NULL` as missing and surfaces any other non-numeric value for review instead of sending it.
- **A bare order number can match several full ones.** One customer order splits per seller into
  `…-A`, `…-B`, `…-C`; on the sample orders export 1001 of 31854 order numbers do this. The seller
  id from the template resolves it, and a row that stays ambiguous is dropped and listed rather than
  guessed at. Searching the bare number by hand simply returns several hits, which is the failure
  this replaces.
- **Duplicate order numbers drop every copy, not all-but-one.** Two return requests against one
  order cannot both be right, and silently picking one is worse than leaving it to be checked.
- **`ReturnListBuilder` parses template dates itself** rather than through `TabularFile.ParseDate` —
  see the warning at the end of this section.

The prepared rows go back to the server as JSON for the Excel download and for `start-list`, so the
~30 MB orders export is uploaded once per prepare and never again. The hand-made workbook upload
still works and is unchanged.

### Filing the returns

Ported from the RPA project's Create Return module. For every row of the list it opens
`…/order/{orderId}/create-return` in a real browser, fills the form and confirms it. Only the order
ID and the tracking number come from the file; quantity (`1`), return method (`By mail`), reason
(`Other reason`), carrier (`Other`), carrier name (`Yurtici`) and the Yurtiçi Kargo tracking URL are
fixed constants at the top of `CreateReturnRunner`.

**Signing in.** Mirakl authenticates through Google SSO, which cannot be scripted, so the browser
runs *headed*: **Open login window** launches Chrome on the order list, the operator signs in, and
**Save session** stores the cookies. They are encrypted with ASP.NET Data Protection to
`%LOCALAPPDATA%\YeniRPA\Mirakl\auth.dat` and reused by every later run — they grant full operator
access to the marketplace, so they are never written in the clear. Playwright can only load storage
state from a file path, so a decrypted copy exists in `%TEMP%` for the moment a browser context is
created and is deleted immediately afterwards.

**Progress reporting is SSE, not SignalR.** The original streamed run progress over a SignalR hub.
This app has no external runtime dependencies and the stream only ever flows server to browser, so
adding a SignalR client to get one-way messages was not worth it —
`AutomationJobBus` publishes JSON events on `/api/automation/events` instead, which needs no client
library. The bus keeps the current run's last 500 events, so reloading the page mid-run replays the
log rather than losing it.

**One run at a time, for the whole app.** There is one browser and one saved session, so the bus
holds a single run slot; a second start returns `400`. The run outlives the request that began it —
the POST only reports that the batch was accepted.

Things worth knowing before changing it:

- **A failing order is screenshotted to `artifacts/create-return/` beside the executable and the run
  moves on.** A batch is usually mostly good rows, and the screenshot is what distinguishes an
  expired session from a renamed field. There is deliberately no way to cancel a running batch —
  that was true of the original too.
- **Fields are located by their visible English label**, through XPath such as
  `//label[contains(.,'Quantity')]/following::input[1]`. The form carries no stable ids or test
  hooks. This is the part that breaks when Mirakl changes its UI, and it fails as a 15-second
  timeout waiting for the quantity input.
- **`SlowMo = 300` is load-bearing.** The Mirakl form re-renders as each field is filled and the
  original automation needed the pause between actions to stay reliable.
- **The confirmation dialog is matched on its whole shape** (title *Create return* + *Cancel* +
  *Create*), because after the first click the page carries two buttons reading *Create*.
- Chrome is only launched on first use, not at startup, so the report modules never pay for a
  browser nobody asked for. `browserReady` on the status endpoint reports whether it is already up.
- **There is no way to stop a running batch.** That was true of the original too, but it matters
  more now that a prepare can hand the runner 177 orders in one click. Worth adding.

### Two bugs this work uncovered in the Return SLA report

Both predate the port and neither is fixed here — fixing them moves that report's published numbers,
which is a decision to take on its own terms rather than as a side effect of another module. They
are written down because they are real and currently invisible.

**1. `TabularFile.ParseDate` transposes day and month**

`ParseDate` leads with `DateTime.TryParse(text, InvariantCulture)`, which is month-first and accepts
`.` as a date separator. Every `dd.MM.yyyy` value whose day **and** month are both 12 or under comes
back transposed:

| Input | Read as | Should be |
|---|---|---|
| `13.08.2026 13:21` | 2026-08-13 | correct — day > 12, unambiguous |
| `12.08.2026 22:34` | 2026-12-08 | 2026-08-12 |
| `07.08.2026 10:00` | 2026-07-08 | 2026-08-07 |
| `01.08.2026 09:00` | 2026-01-08 | 2026-08-01 |

The only `dd.MM.yyyy` source is return template A's `Talep Tarihi`, which the Return SLA report uses
as its SLA start date — so its elapsed days, breach flags and warning flags are wrong for most
template A rows. `ReturnListBuilder.ParseTemplateDate` works around it locally by trying the
day-first formats before anything lenient; without it the last-month filter kept 226 rows instead of
245. Fixing it properly is a one-line reorder of the same format list.

**2. `ReadTemplateB` counts `NULL` as a tracking code**

It skips a row when `YK Takip Kodu` is null or whitespace, but the MP export writes the literal text
`NULL`, which is neither. On the sample export all 2685 rows are therefore treated as "shipped back
to the seller" when only 694 actually have a tracking code — so ~74% of the template B rows on that
dashboard, and the SLA breaches counted from them, describe returns that were never shipped.
`ReturnListBuilder.ReadTracking` shows the handling this needs.

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
  line-level cancellation table is therefore ranked by `Total canceled amount` +
  `Total refunded amount` instead. The figure itself is not printed — only the ordering it produces.
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
