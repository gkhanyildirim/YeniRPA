# Marketplace Reporting

ASP.NET Core MVC app hosting the Mirakl marketplace reports and automation modules on a single page,
with tab navigation between them and results rendered in place.

| Module | Input | Output |
|---|---|---|
| **Order Report** (Late Shipment & Cancellation) | `orders.xlsx` | In-page dashboard (20 KPIs, 8 charts, 5 tables, 2 ranked lists), filterable by date range and seller + 4-sheet Excel workbook |
| **Return SLA Report** | `orders` export + 1–2 return tracking templates (`.xlsx` or `.csv`) | In-page dashboard (6 KPIs, 4 tables) |
| **Create Return** | The two return templates + the returns and orders exports — or a ready `.xlsx` with the order ID in column A and the tracking number in column B | Reviewable list (funnel, ready rows, what was dropped), then files a return on Mirakl per row with a live run log |
| **Late Order Warnings** | `orders` export (`.xlsx` or `.csv`) + the seller → WhatsApp group mapping | Overdue orders by seller, a funnel, the rows set aside for review, and one composed warning message per seller (copy to clipboard or export to Excel) |
| **Seller Offer Warnings** | The seller → e-mail → attachment mapping + a folder of per-seller offer workbooks | One warning mail per seller with that seller's own offer list attached, previewed in full, then sent through Outlook with a live run log |
| **Data & Methodology** | — | Reference page: source column per metric, calculation rules, known export traps, limits |

The reports are read-only: they never leave the machine and nothing is stored. **Create Return is
not** — it drives a real browser against the Mirakl back office and writes to the marketplace. See
[Create Return](#create-return-automation). **Late Order Warnings is not either**, and goes further:
it posts messages to external parties in WhatsApp groups, and a sent message cannot be recalled. See
[Late Order Warnings](#late-order-warnings). **Seller Offer Warnings** is the same class of thing
again, with one extra hazard: every mail carries a commercially sensitive attachment, so the file
that goes out matters as much as the address. See
[Seller Offer Warnings](#seller-offer-warnings).

## Late Order Warnings

Finds orders that are **overdue right now** — `Shipping date` empty, status one the seller can still
act on, `Shipping deadline` passed — groups them by seller and posts one message per seller in that
seller's WhatsApp group.

This is a **different rule** from the Order Report's late-shipment rate. That one is retrospective
(`Shipping date > Shipping deadline`, i.e. it shipped, late) and is used to rank sellers. This one is
prospective, and an order can be late here while being invisible there because it has no shipping
date to compare yet. Do not merge them.

### Why WhatsApp Web and not the Cloud API

Meta's WhatsApp Business Cloud API **cannot send to groups** — only to individual phone numbers, and
`wa.me` links cannot target a group either. Groups are the requirement, so the only route is driving
WhatsApp Web. That is an unsupported use of the product; it is worth knowing that before relying on
it.

### The session trap

`WhatsAppBrowser` uses `LaunchPersistentContextAsync` with a profile directory, **not**
`StorageStateAsync()` like `MiraklBrowser`. WhatsApp Web keeps its authentication material in
IndexedDB, which storage state does not capture — and it does not fail while not capturing it. It
writes a perfectly valid state file, the badge goes green, and every run lands on a QR code. The
symptom reads as "the session keeps expiring", which sends you looking in the wrong place entirely.
The two browser classes differ for four independent reasons; do not DRY them together.

The profile is **not** encrypted by this app (Chrome's own DPAPI protection covers it). A live
session can read and send in every chat the operator is in, not just the seller groups — treat
`%LOCALAPPDATA%\YeniRPA\WhatsApp\profile` as a credential.

### The three guards

A WhatsApp message cannot be recalled and lands in front of an external party, so:

1. A chat is opened only on an **exact, case-sensitive title match**, and only when exactly one chat
   carries that title. Never `.First`. **No fuzzy matching may ever be added** — an 85 %-similar
   match posts one seller's order list into a different seller's group, which is a competitor data
   leak that looks like a working system until someone complains.
2. After the click the conversation header is **read back and compared again**. The result list
   re-sorts as it loads, so the click can land one row over.
3. The composed text is **read back out of the box** and compared to the approved body before Enter.
   This catches a dropped keystroke, an emoji auto-conversion and a focus steal while everything is
   still reversible.

Plus: only groups present in `seller-groups.json` can be posted to, dry run is the default, at most
40 groups per run (refused, not truncated), and 6–12 s randomised between groups. A lost session
aborts the whole run rather than producing forty screenshots of the same QR code.

`Services/Automation/WhatsAppSelectors.cs` is the single place to fix when WhatsApp changes its
markup — each selector is a candidate list, and the failure message names what was being looked for
and lists everything tried.

## Seller Offer Warnings

Mails each seller a warning about their offer lead times, attaching **that seller's own offer list**.
The mapping table — `Seller`, `SellerId`, `Email`, `DosyaAdi`, `LeadTime0`, `LeadTime1` — is the
input; the attachment folder holds one `.xlsx` per seller, named exactly as `DosyaAdi` says.

### Where the addresses come from

`Fetch e-mails from Mirakl` reads each seller's *Users* tab in the operator back office
(`/mmp/operator/shop/{sellerId}/user`) and puts every **enabled** user on that seller's row. A seller
usually has several users and they all belong on **one** mail's To line, not on one mail each — the
`Email` cell holds a `;`-separated list and one mail goes out per seller.

It runs on the Mirakl session Create Return already owns, driving one browser page from seller to
seller — roughly 4 s each, so ~190 sellers take about 13 minutes. It is a background run on the
shared job bus with a live log, and it holds the app-wide automation slot for its duration.

### Why it drives a page instead of calling an API

The Users tab is a React micro-frontend. The server-rendered HTML is an empty shell (14 KB, no table
in it) and the list arrives from an internal endpoint under `/private/organizations/{org}/users`,
reached by resolving the shop to an organisation first. That chain is undocumented, unversioned, and
free to change on any Mirakl release — and it does not fail loudly when it does, it returns nothing,
which written back into the table would erase every address in it. The public `/api/` operator API
answers `401` to session cookies; it needs an API key, and it does not expose a shop's users.

Reading the page the operator would have read is slower and true by construction. This runs once a
month.

Two more things are load-bearing:

- **An expired session answers `200`.** The back office redirects to `/login` and serves the sign-in
  page, so nothing about the status code looks wrong and a naive parser reads every seller as "no
  users". So the check is on the *final URL*, and a sign-in page aborts the whole fetch — the table
  comes back untouched rather than half-cleared.
- **It does not save.** Like the Excel import it hands back the merged table for review; the operator
  presses Save. A fetch that rewrote 190 addresses in place would only be recoverable from the `.bak`
  generation, with nothing to compare against.

The parser deliberately does not know Mirakl's class names. It takes table rows carrying both an
address and a status word — which survives a CSS refactor on their side, and keeps the operator's own
address (present in the page chrome on every page) out of the results. `Enabled` is matched as a
whole word, because `Contains("Enabled")` is also true of `Disabled`.

### Why Outlook and not SMTP

The operator's mailbox is corporate Exchange behind modern authentication. An SMTP path would need
either an app password the tenant does not issue or a service account whose address is not the one
sellers already correspond with. Driving the desktop client borrows a session that is already
authenticated, and every warning lands in the operator's own **Sent Items**, where the audit trail
belongs. Nothing in this app stores a password.

### The STA trap

ASP.NET Core request threads are MTA. Outlook's object model is an STA apartment-threaded server:
calls from an MTA thread go through a marshalling proxy that mostly works and intermittently does
not — `RPC_E_SERVERFAULT` halfway through a batch, with no pattern to it. `OutlookMailSender` owns
**one long-lived STA thread** and every COM call happens on it; nothing outside that file ever sees a
COM object. It is late-bound through reflection, so the app builds and runs without Outlook installed
and is not pinned to an Outlook version.

### The new Outlook has no COM at all

If `Check Outlook` reports `CO_E_SERVER_EXEC_FAILURE` (`0x80080005`), the machine is very likely
running the **new Outlook for Windows** (`olk.exe`), a store app with no object model. COM then tries
to cold-start classic Outlook in its place and that is what fails. Start classic Outlook
(`Office16\OUTLOOK.EXE`) once and check again — the probe attaches to a running instance. The badge
says as much rather than printing the bare HRESULT, because the raw error sends you looking at DCOM
permissions for an afternoon. Outlook must also run as the **same Windows user at the same
elevation** as this app.

### The guards

A mail cannot be recalled, and its attachment is a complete price and stock list, so:

1. **Each mail names a seller, and that seller must resolve to exactly one row** of the saved mapping
   table (id first, folded name as the fallback). Two candidate rows are refused, never picked
   between. Keyed by seller rather than by address because neither side is unique on its own: a
   seller has several users on one mail, and one agency address can be a recipient for several
   sellers.
2. **Recipients and attachment are both re-derived server-side** from that row, never taken from the
   browser. What the browser sent is compared to the table and a difference is refused by name — it
   never wins.
3. **The path is resolved inside the attachment folder and nowhere else.** A file name is a *name*:
   anything containing a separator, a drive letter or an invalid character is refused, so a stray
   `..\` in a spreadsheet cell cannot mail a seller a file off your disk.
4. **Nothing is matched approximately, and none may ever be added.** Not "starts with", not "closest
   file name", not "the only workbook containing the seller's name". An 85 %-similar match attaches
   one seller's price list to a different seller's mail — a competitor data leak delivered by our own
   automation, and it would look exactly like a working system until someone complained.
   `OfferMailBuilderTests.ResolutionIsByNameAloneAndNeverGuessesANeighbour` exists to fail if anyone
   tries.

Plus: one seller per mail (the same seller twice in a run is refused; the same *address* across
different sellers is fine and expected — each of those mails carries a different attachment, and the
panel says how many are affected), the file confirmed on disk again immediately before Outlook is
called, dry run the default
(it `Save`s a real draft with the real attachment instead of sending), at most 250 mails per run
(refused, not truncated) and 2–5 s randomised between them.

**Keep the attachment folder out of `wwwroot`.** Anything under there is served to the browser and
copied into the build output on every build — 188 × ~1.2 MB of seller price lists in both places. The
default is `%LOCALAPPDATA%\YeniRPA\Offers`.

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

```bash
dotnet test
```

`tests/YeniRPA.Tests` covers the rules that decide what an operator acts on: the order-number join
and the SLA verdict behind the Return SLA report, the day-first template dates, what counts as a
tracking code, and carrier canonicalisation. They run on CSV built in memory and go through the same
readers the app does. The test packages are a **build-time** dependency only — the app itself still
ships with no external runtime dependencies.

Requires the .NET 10 SDK. `ClosedXML` and `Microsoft.Playwright` are the only NuGet dependencies;
Chart.js and the IBM Plex fonts are vendored under `wwwroot/lib`, so the app has **no external
network dependencies at runtime**. Playwright is needed only by Create Return — the report modules
never touch it, and it launches no browser until that module is used.

## Layout

## Design system

`wwwroot/css/app.css` is the whole of it: tokens first, components after, no framework. The
direction is a **monitoring console** — a near-black canvas with layered surfaces, one luminous
accent, hairline rules, and figures in IBM Plex Mono with tabular numerals. Colour is reserved:
red/amber/green mean *this is bad / watch it / this is fine* and never decorate anything.

Things worth knowing before changing it:

- **Dark is the default theme.** The inline script in `_Layout.cshtml` stamps `data-theme="dark"`
  before first paint unless a choice is stored; the toggle wins in both directions. Every colour is
  a token declared on bare `:root` (light) and redefined twice for dark — once under
  `prefers-color-scheme`, once under `[data-theme="dark"]`.
- **Charts have their own eight-slot palette** (`--series-1…8`), assigned by series identity and
  never cycled, validated for the lightness band, chroma floor, colour-blind separation and contrast
  against both surfaces. **Status colours are not in it**: filled marks use `--mark-critical` /
  `--mark-serious` / `--mark-warning` / `--mark-good`, which are separate from the text-grade
  `--red` / `--amber` / `--green` because a bar filled with a text colour reads brown.
- **The Order Report opens with a hero row** of four figures plus a sparkline, and its sections
  number themselves through a CSS counter. The chips in the sticky control deck are built from the
  sections themselves (`RPA.initSectionNav` reads `data-section`), so a section cannot be renamed in
  one place and stay stale in the other.
- `RPA.renderKpis` takes `{ group: '…' }` band labels between tiles and an `exportRows` override —
  the order report shows eight tiles and still exports all twelve key metrics, four of which are in
  the hero.
- Motion is one orchestrated entrance per report (`RPA.revealResults`) plus small state
  transitions, and all of it is switched off under `prefers-reduced-motion`.

```
src/YeniRPA.Web/
├── Services/
│   ├── OrderReportBuilder.cs        Build() -> xlsx, BuildData() -> dashboard JSON
│   ├── CarrierNames.cs              Free-text shipping company -> canonical carrier
│   ├── ReturnSlaReportBuilder.cs    BuildData() -> dashboard JSON
│   ├── ReturnListBuilder.cs         4 exports -> the Create Return input list
│   ├── SellerMailStore.cs           seller-mails.json: the mapping, templates and folder
│   ├── OfferMailBuilder.cs          One seller -> subject, body and which file is theirs
│   ├── TabularFile.cs               xlsx/csv -> table, plus the order-number key, the
│   │                                day-first template dates and the tracking-code rule
│   └── Automation/
│       ├── AutomationJobBus.cs      Single-run lock + SSE progress fan-out
│       ├── MiraklBrowser.cs         Playwright browser + encrypted saved login
│       ├── CreateReturnRunner.cs    The Create Return flow, one order at a time
│       ├── MiraklSellerUserScraper.cs  Seller -> its back-office users, one page at a time
│       ├── OutlookMailSender.cs     Outlook COM on one dedicated STA thread
│       └── OfferMailRunner.cs       The seller warning batch, one mail at a time
├── Models/ReportModels.cs           JSON contract with the dashboard JavaScript
│                                    (terse row fields; extended ones omitted when default)
├── Models/MethodologyViewModel.cs   Reads rules off the builders for the Methodology page
├── Controllers/                     Home, one per report, Automation (session + events)
├── Infrastructure/                  400 { error } filter for input-validation failures
├── Views/Home/Index.cshtml          The single page: every module, one visible at a time
└── wwwroot/
    ├── css/app.css                  Design tokens, light + dark themes

tests/YeniRPA.Tests/                 Join, SLA verdict, template reading, carrier names
    ├── js/app.js                    Shell: nav, theme, uploads, fetch
    ├── js/order-report.js           Order dashboard aggregation + charts
    ├── js/return-sla-report.js      Return SLA dashboard
    ├── js/create-return.js          Create Return: session, upload, live run log
    ├── js/late-orders.js            Late Order Warnings: mapping editor, preview, messages
    └── js/offer-warnings.js         Seller Offer Warnings: mapping editor, preview, send
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
| `GET` | `/api/automation/status` | — | `{ hasSession, browserReady, isRunning, runningModule }` |
| `POST` | `/api/automation/login` \| `save-session` \| `clear-session` | — | `200` |
| `GET` | `/api/automation/events` | — | `text/event-stream` of run progress |
| `POST` | `/api/late-orders/prepare` | `file`, `offsetHours` | Overdue orders by seller, funnel, review rows, warnings |
| `POST` | `/api/late-orders/messages` | JSON `{ sellers, referenceTime, template, orderLineTemplate }` | `{ messages, warnings }` |
| `POST` | `/api/late-orders/messages/excel` | JSON `{ messages }` | `.xlsx` (one row per message, body wrapped) |
| `GET` \| `PUT` | `/api/late-orders/mapping` | JSON `{ entries, template, orderLineTemplate }` | The seller → group mapping and the message templates |
| `POST` | `/api/late-orders/mapping/import` | `file` | Merged table for review — **does not save** |
| `POST` | `/api/late-orders/mapping/excel` | JSON `{ entries }` | `seller-groups.xlsx`, re-importable |
| `POST` | `/api/late-orders/send` | JSON `{ messages, dryRun }` | `{ count, dryRun }`; the run continues in the background |
| `GET` | `/api/late-orders/status` | — | `{ hasProfile, signedIn, browserReady, isRunning, runningModule, profilePath }` |
| `POST` | `/api/late-orders/login` \| `check-session` \| `clear-session` | — | `200` |
| `GET` \| `PUT` | `/api/offer-warnings/mapping` | JSON `{ entries, subjectTemplate, bodyTemplate, attachmentFolder }` | The seller → e-mail → attachment mapping, the templates and the folder |
| `POST` | `/api/offer-warnings/mapping/import` | `file` | Merged table for review — **does not save** |
| `POST` | `/api/offer-warnings/mapping/fetch-emails` | JSON `{ entries, onlyMissing }` | `{ started, rows }`; the fetch continues in the background |
| `GET` | `/api/offer-warnings/mapping/fetch-result` | — | The table the last fetch produced — **does not save** |
| `POST` | `/api/offer-warnings/mapping/excel` | JSON `{ entries }` | `satici-mail-eslesme.xlsx`, re-importable |
| `POST` | `/api/offer-warnings/prepare` | JSON `{ subjectTemplate, bodyTemplate }` (both optional) | One rendered mail per row with its resolved attachment, a funnel and warnings |
| `POST` | `/api/offer-warnings/mails/excel` | JSON `{ mails }` | `.xlsx` (one row per mail, body wrapped) |
| `POST` | `/api/offer-warnings/send` | JSON `{ mails, dryRun }` | `{ count, dryRun }`; the run continues in the background |
| `GET` | `/api/offer-warnings/status` | — | `{ outlookAvailable, attachmentFolder, folderExists, filesInFolder, isRunning, runningModule }` |
| `POST` | `/api/offer-warnings/check-outlook` | — | `{ available, error }` — starts Outlook if it is not running |

Input-validation failures return `400 { "error": "..." }` with the message naming the exact problem,
e.g. `Required column 'Shipping deadline' was not found in the uploaded file.`

## Create Return automation

### Preparing the list

`ReturnListBuilder` replaces a manual Excel session. Four exports go in — return template A
(*Marketplace Iade & Degisim Talepleri*), return template B (the *…MP* file), the returns export and
the orders export — and out comes the two-column list the automation runs on, alongside a funnel and
a table of everything that was dropped and why. Nothing is read from disk; all four are uploaded.

Per template: keep rows with a real tracking code, keep rows inside the date range, drop **every**
copy of an order number that appears more than once, optionally keep only `İade` requests, drop
requests the marketplace has already cancelled, and drop anything the returns export already covers.
The two lists are then merged (an order in both is dropped from both) and matched against the orders
export for the full `01259_311911494-A` form — which is also where a **canceled order** is caught.

Five things about this are load-bearing:

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
- **Cancelled rows are dropped twice over, from two different files.** Template B's `State` column
  says whether the request itself was cancelled; the orders export's `Status` column says whether the
  order was — and it is the only place a template A row carries a status at all. Neither can take a
  return, so filing one costs a page load, a failure and a screenshot for nothing. Both checks match
  positively (`IsCanceledState`, `OrderReportBuilder.CanceledStatus`), so an unfamiliar state reaches
  the review table instead of being dropped as if it were a cancellation, and both drops are listed
  with their reason. The `Status` column is read as optional: an export without it still builds a
  list, only without that check.
- **`ReturnListBuilder` parses template dates itself** rather than through `TabularFile.ParseDate` —
  see the warning at the end of this section.

The prepared rows go back to the server as JSON for the Excel download and for `start-list`, so the
~30 MB orders export is uploaded once per prepare and never again. Preparing the list is the only way
into a run: the hand-made workbook upload it replaced — the *Or upload a ready list* card and its
`POST /api/create-return/start` endpoint — is gone. **Download Excel** stays, as a record of what a
run was given rather than as an input.

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

## Return SLA report

The report answers one question per return: *the parcel went back to the seller N days ago — is that
return finished?* The answer is the **order's status**, so everything rests on joining the return
templates to the orders export. Three bugs used to break that join; all three are fixed and covered
by tests.

**1. The join never matched — the report's central failure**

The templates carry the bare customer order number (`321097726`); the orders export carries the full
Mirakl form (`01259_321097726-A`). The old key was "every digit in the number", which turns the full
form into `01259321097726` and the bare one into `321097726`: **no row ever matched**. Every seller
came out blank, every status read *"not matched"*, and so every return past the SLA window was
reported as an SLA breach — including the ones whose order had been canceled weeks earlier. The join
now uses `TabularFile.OrderCore`, the same key `ReturnListBuilder` and `TicketSellerBuilder` use.

Resolution order, since one customer order splits per seller into `…-A` / `…-B`:

| Situation | Result |
|---|---|
| Template B's `MarketPlaceId` is in the export | matched to that order |
| The bare number matches one order | matched |
| Several, and the template's seller id picks one | matched |
| Several, all agreeing on whether the return closed | `matched-by-status` — the verdict holds whichever it was |
| Several that disagree, or nothing at all | listed under *Needs review*, **never** counted as a breach |

A return with no order behind it has no status to be late against; reporting those as breaches is
what made the old list unusable. `slaMissed` and `pastWarning` therefore both require a resolved,
still-open return — the second of those is a deliberate change from the original, where a completed
return still showed up as an early warning. The status column now prints the order's own Mirakl
status next to the verdict badge, which is what an operator would otherwise look up by hand.

**2. `Talep Tarihi` was read month-first**

`TabularFile.ParseDate` leads with `DateTime.TryParse(text, InvariantCulture)`, which is month-first
and accepts `.` as a separator, so every `dd.MM.yyyy` value whose day **and** month are 12 or under
came back transposed (`12.08.2026` → 8 December). That column is the SLA start date. Template dates
now go through `TabularFile.ParseDayFirstDate`, which `ReturnListBuilder` had been carrying locally.

**3. `NULL` counted as a tracking code**

The MP export writes the literal text `NULL` into `YK Takip Kodu` on roughly three rows out of four
(1991 of 2685 on the sample export). "Not empty" therefore read as "shipped back to the seller", so
most of template B's rows were on an SLA clock for a parcel that had never been sent.
`TabularFile.ReadTracking` — the rule Create Return already used — is now applied by both reports,
to both templates.

## Notes for maintainers

**Most report rules are a verbatim port** from the previous RPA project and were verified to produce
byte-identical output. Specifically:

- Late = shipping date present **and** later than the shipping deadline. Rows with no shipping date
  are excluded from the late-rate denominator.
- Carriers count as integrated when the name contains `Aras`, `Yurtici`, `Yurtiçi`, `DHL`,
  `Hepsijet` or `MNG` — applied to the **canonical** carrier name (see below), not to the raw
  column. (`MNG` was added after the first production run; the list is the single source for the
  dashboard, the workbook formula and the Methodology page.)
- Return SLA is **20 days** from the ship-back date, early warning at **15 days**
  (`ReturnSlaReportBuilder.SlaDays` / `.WarningDays`, which every row carries to the dashboard so the
  labels cannot drift from the arithmetic). Return template A has no ship-date column, so
  `Talep Tarihi` is used as a proxy.
- `pastWarning` **is** gated on the return still being open, unlike the original — see
  [Return SLA report](#return-sla-report).

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

- **There is no carrier column.** `Shipping method` is the delivery type ("Standard delivery") on
  every line, `Tracking number` names no company, and `Shipping company` is typed by the seller — so
  one carrier arrives as `YURTİÇİ`, `Yurtiçi`, `yurtici` and `YURTICI KARGO` and splits into four
  rows, each with its own share and its own integrated/manual badge. `CarrierNames` folds them onto
  one name: the host of `Tracking URL` decides where there is one (it is copied from the carrier's
  own site), otherwise the name is folded — Turkish i family, accents, case, punctuation — and
  matched against a catalogue of aliases. **No fuzzy matching, ever**, for the reason spelled out on
  `SellerGroupMap`: a name the catalogue does not know keeps its own group and merges only with its
  own spellings, and the carrier table lists those spellings on the name's tooltip, so no merge is
  invisible. `DHL` and `DHL e-Commerce` are deliberately one catalogue entry — the export separates
  them, operations reconciles them as one carrier. Lines with no shipping company are left out of
  *Order lines by carrier* and counted in a note underneath it, so its shares are taken over the
  lines that name a carrier and add up to 100%. The workbook writes the canonical name into
  `Data!R` and its `Carrier Group` formula reads that column, so the two outputs cannot group
  carriers differently.

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
value, and carrier/category/brand/city travel as indexes into dictionaries on the payload rather than
as strings on every row. Index 0 in each dictionary is the "unknown" slot. The carrier dictionary
carries more than a label — `{ n: canonical name, i: integrated, v: [raw spellings] }` — because the
merge and the keyword rule both belong to the builder, not to the browser.

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
by column letter (`Data!K`, `Data!I`, …), and columns K, L, O, P, Q are live formulas. A new column
goes on the **end** for that reason — `R` (`Carrier (Normalized)`) was added there rather than next
to `Shipping Company`, which is where it belongs visually and where it would have repointed every
formula on the other three sheets.

The dashboard's long tables (lead time, carrier volume, cancellation detail) are rendered by
`RPA.renderDataTable`, which adds a sortable header and a per-column filter row on top of the same
column contract `RPA.renderTable` uses. Its sort and filters are kept per wrapper id and survive a
date or seller change — but not a new upload, which clears them. What a section exports is always
what its table currently shows, filters included.
