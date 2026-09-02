# Marketplace Reporting

ASP.NET Core MVC app hosting the Mirakl marketplace reports and automation modules on a single page,
with tab navigation between them and results rendered in place.

| Module | Input | Output |
|---|---|---|
| **Order Report** (Late Shipment & Cancellation) | `orders.xlsx` | In-page dashboard (20 KPIs, 8 charts, 5 tables, 2 ranked lists), filterable by date range and seller + 4-sheet Excel workbook |
| **Return SLA Report** | `orders` export + 1–2 return tracking templates (`.xlsx` or `.csv`) | In-page dashboard (6 KPIs, 4 tables) |
| **Create Return** | The two return templates + the returns and orders exports — or a ready `.xlsx` with the order ID in column A and the tracking number in column B | Reviewable list (funnel, ready rows, what was dropped), then files a return on Mirakl per row with a live run log |
| **Product Status** | A seller list (`.xlsx` or `.csv`, names in the first column) or pasted seller names | Reads each seller's catalogue breakdown off the Mirakl Catalog Manager — four sellers at a time — and returns one seller × status table, sortable in place and exportable to Excel. Read-only: nothing is written to the marketplace |
| **Late Order Warnings** | `orders` export (`.xlsx` or `.csv`) + the seller → WhatsApp group mapping | Overdue orders by seller, a funnel, the rows set aside for review, and one composed warning message per WhatsApp group (copy to clipboard or export to Excel) |
| **Seller Offer Warnings** | The Mirakl `offers` export + the seller address list (`Onboarding Check List.xlsx`, sheet `Data`) | One `.xlsx` per seller listing their offers with a lead time to ship of 1–2 days, then one warning mail per seller carrying their own file, previewed in full and sent through Outlook with a live run log |
| **Seller VAT Warnings** | The "offers with no VAT rate" export (needs a `State Reasons` column) + the same seller address list | One `.xlsx` per seller listing the products whose *only* state reason is `VAT_RATE_MISSING`, mailed the same way |
| **Title Cleaner** | A product export (`.xlsx` or `.csv`) with a title column and attribute columns | Each title stripped of what that row's own attributes name, the cells that disagreed with their title, and the cells completed from it — previewed in full, then a 3-sheet workbook |
| **Data & Methodology** | — | Reference page: source column per metric, calculation rules, known export traps, limits |

The reports are read-only: they never leave the machine and nothing is stored. **Create Return is
not** — it drives a real browser against the Mirakl back office and writes to the marketplace. See
[Create Return](#create-return-automation). **Late Order Warnings is not either**, and goes further:
it posts messages to external parties in WhatsApp groups, and a sent message cannot be recalled. See
[Late Order Warnings](#late-order-warnings). **Seller Offer Warnings and Seller VAT Warnings** are the
same class of thing again, with one extra hazard: every mail carries a commercially sensitive
attachment, so the file that goes out matters as much as the address. See
[Seller Offer Warnings](#seller-offer-warnings).

## Late Order Warnings

Finds orders that are **overdue right now** — `Shipping date` empty, status one the seller can still
act on, `Shipping deadline` passed — groups them by seller and posts **one message per WhatsApp
group**. Usually that is one message per seller. When a company trades under two Mirakl ids and the
mapping points both at the same group, their overdue orders are merged into a single message, each
account's orders under its own heading — two messages in one chat is not what anyone wants, and
dropping one of the two accounts would leave those orders unchased.

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

Mails each seller a warning about the offers they ship too fast, attaching **that seller's own list**.
Two uploads go in — the Mirakl `offers` export and the seller address list — and the app writes one
`.xlsx` per seller, resolves each address, renders every mail and sends the ones the operator ticks.

**Seller VAT Warnings is the same module with a different filter**: same two uploads, same address
matching, same batch, same guards. Only what it selects out of the export and what it puts in the
attachment differ. Its filter is the `State Reasons` column, and it keeps a row only when that cell
reduces to `VAT_RATE_MISSING` **on its own** — an offer that is also inactive, out of stock or priced
at zero has a bigger problem than its VAT rate, and asking its seller to fix the VAT rate is the
wrong message. The column is required for the same reason `Lead time to ship` is: an export that
renamed it would match nothing, and falling back to no filter means mailing every offer in the file.
The two modules are deliberately separate code — see [The guards](#the-guards).

### What it selects

Only offers whose **`Lead time to ship` is 1 or 2 days**. On the current export that is 86 912 of
203 543 rows, belonging to 287 of 444 sellers. Zero is excluded on purpose: it is what the export
writes for offers the seller does not ship at all. A blank cell — a third of the file — is not a
promise anyone made, so it is not one anyone is warned about.

The attachment has **two columns: `Product SKU` and `Termin (Gün)`**. Nothing else from the 26-column
export reaches the record the workbook is written from, so no price, stock, discount or category can
be written into a file that leaves the building. Rows are sorted by lead time so a seller can work
down the one-day offers first.

### 287 sellers against a 250-mail run

More sellers qualify than one run may mail, and the cap is a **refusal, not a truncation** — sending
the first 250 silently would leave the operator believing all of them went out. Two levers:
**Minimum offers**, which drops sellers with only a handful of short-lead offers before anything is
written for them, and the per-card selection, which splits a run into two passes. The prepare says so
in a warning as soon as it knows, while the threshold box is still on screen, rather than letting the
send refuse after 287 cards have been read.

### Why the export is not read like every other upload

At 27.5 MB compressed — 195 MB of worksheet XML, 203 543 rows × 26 columns — this file is two orders
of magnitude larger than anything else the app reads. `TabularFile`'s ClosedXML path materialises the
whole worksheet DOM before the first row is looked at: 5.3 million cell objects, gigabytes of working
set, for a file four columns are read out of.

So `OfferExportReader` streams it instead — shared strings once into a flat array, then the sheet
walked with `OpenXmlReader`, one row yielded and dropped at a time. It indexes cells on their **cell
reference**, not on arrival order, because Excel writes no element at all for an empty cell: row 2 of
the real export jumps from `L2` to `N2`, and appending would shift every column after the gap by one
and read the `EAN` column as the lead time.

### Where the addresses come from

The **Onboarding Check List** workbook, on the sheet named `Data` — its first sheet is a funnel
summary with no address column at all, which is why the sheet has to be named and why a wrong name is
refused with the real sheet list rather than silently falling back to sheet one.

`SellerMailDirectory` indexes it by seller id and by folded name, and **matches on nothing else**. A
key that appears twice with two different addresses is poisoned rather than resolved — picking one
would be picking whose inbox an offer list lands in. Cells holding `#N/A` or `#REF!` are counted and
skipped; they are broken lookups, not addresses. A seller often has several users and they all belong
on **one** mail's To line, not on one mail each.

Sellers the list does not cover appear in their own table with an editable address box, and what is
typed there is saved and wins over the uploaded list from then on. That is the whole answer to a
seller who does not match: state the address once, rather than widening the match until
`Yazıcı Bende` lands on `Yazıcı Ticaret`.

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

A mail cannot be recalled, and its attachment is a complete list of one seller's offers, so:

1. **The pairing is the server's, and the server keeps its own copy.** `prepare` computes which
   address and which file belong to which seller and puts it in `OfferBatchStore`; `send` reads it
   back from there. Nothing the browser posts can change either. A send quoting a batch the server no
   longer holds is refused — build the mails again and read the list.
2. **Recipients and attachment are re-derived from that batch**, never taken from the request. What
   the browser sent is compared to it and a difference is refused by name — it never wins.
3. **The path is resolved inside the run's own folder and nowhere else.** A file name is a *name*:
   anything containing a separator, a drive letter or an invalid character is refused, so a seller
   name of `..\..\auth` cannot mail anyone a file off your disk. Each run writes into a fresh
   timestamped folder, so last month's files can never be picked up by this month's send.
4. **Nothing is matched approximately, and nothing approximate may ever be added.** Not "starts
   with", not "closest file name", not "the only workbook containing the seller's name". An
   85 %-similar match attaches one seller's offer list to a different seller's mail — a competitor
   data leak delivered by our own automation, and it would look exactly like a working system until
   someone complained. `OfferMailBuilderTests.ResolutionIsByNameAloneAndNeverGuessesANeighbour`
   exists to fail if anyone tries.
5. **Two sellers whose names reduce to one file name are both refused**, not just the second: the
   second write overwrites the first, so mailing either one hands a seller the other's list.

Plus: one seller per mail (the same seller twice in a run is refused; the same *address* across
different sellers is fine and expected — each of those mails carries a different attachment), the
file confirmed on disk again immediately before Outlook is called, dry run the default (it `Save`s a
real draft with the real attachment instead of sending), at most 250 mails per run (refused, not
truncated) and 2–5 s randomised between them.

**Why the two modules are separate code.** `OfferSplitBuilder`/`VatSplitBuilder`,
`OfferMailStore`/`VatMailStore` and `OfferBatchStore`/`VatBatchStore` are near-copies rather than a
shared generic. They agree today; a change made for one export's shape must fail that module's own
test rather than quietly alter whose offers land in whose inbox in the other. The one thing they do
share is `OfferMailBuilder.ResolveAttachment` — it states an invariant that must *not* be allowed to
drift apart — and `SellerMailStore`'s address-cell rules, which carry no seller in them.

**Keep the output folder out of `wwwroot`.** Anything under there is served to the browser and copied
into the build output on every build — 287 sellers' offer lists in both places. The default is
`%LOCALAPPDATA%\YeniRPA\OfferLeadTimes` (VAT Warnings uses `…\VatOffers`).

The Order Report dashboard has two layers. *Key metrics* down to *Late shipment & cancellation —
top 5 sellers* is the original port and is covered by the guarantees below. Below that sit the
extended sections — delivery quality, cancellation/rejection/refund breakdown, lead-time (SLA)
analysis, category performance and data quality. They read **optional** columns, so an export
without them still renders; each affected section shows an empty state naming the missing column,
and a banner above *Key metrics* lists them all.

**Period-over-period comparison.** The hero strip and every *Key metrics* tile carry the change
against the window immediately before the active date filter, of the same length, measured on
`Date created`; a filter that is exactly one calendar month is compared against the previous
*calendar month* instead, so a 31-day August is not measured against a window reaching back into
June. Rates state the difference in percentage points (`▲ 1.7 pp`) and counts and durations a
percent change (`▲ 31.9%`) — calling a 5.4% → 7.1% cancellation rate a 31.5% rise would read as a
collapse it is not. The seller filter applies to the baseline too. **Nothing is stored between
runs**, so the baseline exists only where the uploaded export itself reaches back that far; where it
does not, no change is shown and the note under the hero offers the last whole month in the file as
a range that would produce one.

## Title Cleaner

Product titles have to follow a per-category naming standard. The cleaner reads **each row's own
attribute cells** and cuts those values out of that row's title, leaving the model and anything no
column claims:

```
Başlık : Dell Pro Max 16 MC16250_3 Ultra 7 265H 32GB 1TBSSD RTXPRO2000 16" FullHD+ W11P Dizüstü İş İstasyonu
Sonuç  : Pro Max 16 MC16250_3 RTXPRO2000
```

**Removal is a whitelist.** A value goes only when a rule names its column, the row's cell carries
it, and the title is confirmed to spell the same thing. `RTXPRO2000` survives because no rule claims
it — the cleaner does not need to recognise it, only to be told nothing about it. That is the only
safe default for something that deletes catalogue data.

### Why "16" is the whole problem

That title carries `16` three times — in the model name (`Pro Max 16`), inside the model code
(`MC16250_3`) and as the screen size (`16"`). Only the last may go; a plain replace of the attribute
value leaves `Pro Max MC250_3`, a model that does not exist, written back to the marketplace. Two
rules together settle it, and neither works alone:

- **A measured value is only ever recognised together with its unit** — or, where the unit is
  missing, glued to another value that is. A bare number standing on its own is never a candidate.
  Loosen this and model names start disappearing.
- **A span may begin or end inside a word only where another accepted span picks up exactly where it
  leaves off.** This is what lets `1TBSSD` — a disk capacity and a disk type with no separator
  between them — come apart into two matches while `MC16250_3` stays whole. A boundary rule strict
  enough to reject the one would reject the other, so the two cases cannot be told apart one span at
  a time: `TitleCleanBuilder` collects candidates first, then resolves overlaps and validates
  boundaries against the neighbours, to a fixed point.

### Three ways a title writes a value the cell does not

A second real export — 48 laptops from a different seller — broke the module in three places at once,
and the operator saw one symptom: the processor was never removed. Each has its own answer, and none
of them is a similarity score.

**The space nobody typed.** The marketplace's attribute column reads `Ryzen™ 5` and `Core™ 5`; the
seller's titles read `Ryzen5 220` and `Core5 120U`. Nothing separates those but a space one side chose
not to write, and without it the processor column matched *nothing at all* on the whole file. A gap in
one may now be missing in the other — **only at a change between letter and digit**, which is a word
boundary whether it is written or not. `Pro Max` therefore still does not answer `ProMax`: two letters
running together are two words the writer joined, not one word they split, and allowing that would
turn every value into a prefix search. `AttributeMatcher.IndexOfLoose` does it, the partial search
uses the same rule so `Intel Core Ultra 5` can find `Ultra5`, and the boundary check afterwards is
unchanged.

**The unit nobody wrote.** The same titles write `512SSD` — the capacity, no unit at all, glued onto
the disk type. The number is readable as 512 GB only because the confirmed disk type continues
straight from it, which is the same evidence `1TBSSD` always relied on, minus the unit. So a
`Measure` scan now also proposes the cell's *own* quantity where the title wrote it bare, and
`TitleCleanBuilder.BareSupported` throws it away unless another confirmed span is glued to it with no
separator. `Pro Max 16` is refused — its `16` has spaces either side and nothing to lean on — and it
is refused *even on a row whose screen really is 16 inches*, which is the case that makes the rule
worth having.

**The words no cell contains.** `Ultra5 125H`: the model code is in no column of the file. The older
export happened to carry it (`İşlemci (tr_TR) = 8745HX`); this one carries `Intel Core Ultra 5`
instead, and no rule built out of that file can remove what the file does not contain. See
[Reference lists](#reference-lists).

**The word the field ran out of room for.** A marketplace caps its title, and a seller who writes up
to the cap loses the last word mid-letter: five of the forty-eight rows end `Dizüstü Bi` or
`Dizüstü Bil`. Every other step here compares whole words, and half a word is not one. A value may
now answer a title that stops inside its **final** word — every earlier word present in full, the
match running to the exact end of the title, and the cut landing strictly inside that last word.

Those three conditions are the whole of it, and the first attempt had only two. Allowing the cut
anywhere in the value let a product-description column match an entire title with its opening
sentence, claim every character, and evict every other rule on the row; a whole white-goods file came
out uncleaned, and `TitleCleanerRealFileTests` is what caught it. Truncation means the field ran out
on the last word, and saying exactly that is what keeps this from being a prefix search.

A truncated span is offered **alongside** whatever else matched rather than only when nothing did. A
catalogue routinely holds both `Dizüstü Bilgisayar` and `Dizüstü`; a title cut to `… Dizüstü Bi`
matches the short spelling outright, so gating truncation on "nothing matched" left the `Bi` behind
on every such row. The longer span is the better reading of the same text, and the ordinary
arbitration is already there to prefer it.

### What a second seller's file changed

A hundred laptops from a different seller, same template, different habits. Four things only that
file could have taught the module:

**A cell can hold two measurements.** A machine with two disks says so in one column: `1 TB + 1 TB`,
`2 TB + 128 GB`. Reading only the front of it made the second `1TBSSD` in the title look like a repeat
nobody had asked for, and the row went out uncleaned. A `Measure` cell now reads **every** number and
unit in it, and the repeat guard is not loosened by that but sharpened: how many occurrences a row is
entitled to is read off the cell rather than assumed to be one. `RTX 5070 8GB 8GB` against a cell
saying `8 GB` once is still ambiguous, still untouched.

The disk *type* column cannot follow suit — it says `SSD` however many disks there are — so a second
occurrence earns its place a different way: **a value written hard against another confirmed value is
anchored to it.** The `SSD` in `1TBSSD` is not a free-floating repeat, it is half of a compound whose
other half the row confirmed. Two such compounds are two disks; two loose `8GB` are not.

**Which character separates a family from its model is arbitrary.** The catalogue writes
`AMD Ryzen 7 7735HS` and `Intel Core i5-14450HX`; the titles write `Ryzen7-7735HS`, `i5 14450HX` and
`Ryzen5 220`. A hyphen is now a gap wherever a space is, both ways round — the same tolerance for
typography the whitespace rule already was, and it stops at the characters that carry meaning.

**A catalogue entry has to earn its place.** `Intel Core Ultra 5 225` is a real processor and the
published list has it before `Intel Core Ultra 5 225U`. Against a title reading `Ultra5 225U` it
matched nothing but the `Ultra5` the cell alone would have found — and then stopped the search, so
the entry that would have taken the model code never got a turn. An entry is only accepted when what
it matched actually carries the words it adds.

**Not every title column holds a title.** One row's reads
`2 YIL LENOVO TÜRKİYE GARANTİLİ - ADINIZA FATURALI - HIZLI KARGO`. Its brand cell says LENOVO and the
line does contain LENOVO, so the cleaner faithfully cut it out and wrote the rest back. The test is
arithmetic and knows nothing about brands: **of the cells a row filled, the title matches one.** Such
a row is handed over untouched and goes to review. A genuine title matches half its columns or better,
and a rule set of fewer than four filled cells is too small to draw the conclusion from at all.

### A word the seller typed twice

`Lenovo Ideapad Ideapad Slim3` — the series written back to back. Collapsing that removes text **no
column claimed**, which is the one thing this module otherwise never does, so it is a rule-set setting
(`Tekrarı Sil`) that is off by default rather than something the engine assumes.

Two things keep it narrow. **A repeat carrying a digit is never touched:** `RTX 5070 8GB 8GB` is a
graphics card's own memory beside the system RAM on the rows where the two are the same size, the
case this module already has a verdict and a paragraph of its own for. And it is **read off the
original title, not the cleaned one** — cutting the middle out of `Ocak Siyah Ocak` leaves `Ocak
Ocak`, which is not a repetition anybody typed.

`FoldedTitle` exists because neither fold already in the app can be used here. `SellerGroupMap.FoldName`
collapses whitespace and `CarrierNames.Fold` turns punctuation into spaces — both right for comparing
two whole names, both fatal for a module that cuts spans out of a string rather than comparing them.
It takes their *rules* (the Turkish i-family, the accent map) and keeps a 1:1 map back to the original
character positions.

**There is no fuzzy matching and none may ever be added** — the same rule as `CarrierNames` and
`SellerGroupMap`, for a sharper version of the same reason. Those would misroute a message; this one
silently deletes the wrong characters out of a product title and writes the result back, with the
original gone. `TitleCleanerTests.AValueIsNeverFoundInsideALongerWord` exists to fail if anyone
widens it.

### What happens when the title and the cell disagree

| Verdict | When | The title | The cell |
|---|---|---|---|
| `OK` | Found, already canonical | Cut | — |
| `DÜZELTİLDİ` | Same value, different spelling (`16` ↔ `16GB`, `15,6"` ↔ `15.6"`) | Cut | Rewritten canonically |
| `ÇAKIŞMA` | Title says one value, cell says another | **Untouched** | **Untouched** |
| `BELİRSİZ` | Cell holds a bare number the title offers two units for | **Untouched** | **Untouched** |
| `BAŞLIKTA YOK` | Cell has a value the title never mentions | — | — |
| `ÖZELLİK BOŞ` | Empty cell (`FillFromTitle` is off by default) | — | — |

`FillFromTitle` has no checkbox in the rule editor. Every attribute column a marketplace export
carries is already filled, so the box only ever sat there unticked; the browser carries the stored
value through untouched. The one way to turn it on is the `Başlıktan Doldur` column of an imported
rule-set workbook.

A disagreement is reported, never acted on: which side is right is not something this tool can know,
and one attribute disagreeing does not stop the other seven on that row being cleaned. What each kind
can *detect* differs — `Measure` and `Alias` scan the title independently of the cell, so they can see
it naming a different value; `Text` only ever searches for the cell's own value, so it reports found
or not found and never a conflict.

Two units that both carry a `Factor` are compared in the base unit, so a cell reading `1024 GB` is not
reported as a conflict against a title reading `1TB`. Without a factor the unit must match exactly —
no conversion is ever invented.

### Reference lists

An attribute cell says what a product is; it does not always say it in full. A **reference list** is a
column of full canonical values out of a workbook the operator uploads — the published Intel/AMD
catalogue, 5384 rows of `Intel Core Ultra 5 125H` — attached to a rule by name and stored apart from
the rule sets, in `reference-lists.json`, because a catalogue that long has no business inside a file
meant to stay hand-readable.

**It does not widen the whitelist.** An entry is only ever looked at when the row's **own cell value
sits inside it** as a run of whole words, so the row is always the one asserting what the product is;
the list only says how that value is written out in full. A catalogue of five thousand processors
therefore cannot introduce a processor into a title — against a cell reading `AMD Ryzen 3`, the entry
`Intel Core Ultra 5 125H` is never searched for.

What gets searched is the entry **from the cell's value onwards**, matched partially. Titles drop the
manufacturer — `Intel Core Ultra 5 125H` is written `Ultra5 125H` — but they do not drop the model
code, and `AddPartial`'s existing rule enforces exactly that: every word the run leaves out has to be
absent from the title. `Intel Core` may go because the title genuinely does not say it; `125H` may not
because it does.

Three details are load-bearing, and two of them were bought the hard way:

- **Every word an entry adds must be a word the title writes**, checked before any search is run.
  Checking only the last one let `AMD Ryzen 5 PRO 220` through against a title reading `Ryzen5 220` —
  it has more words than `AMD Ryzen 5 220` so it was tried first, `PRO` is nowhere in that title, and
  the partial search fell back to the one word both sides shared: the bare `220`. A reference entry
  matching nothing but an unqualified number is the exact deletion this module refuses everywhere
  else, and it silently cost the whole file its processor removal.
- **The cell's last word may end part-way into the entry's**, on a separator or a letter/digit change.
  Intel's own catalogue writes `Intel Core i5-13420H` against a cell reading `Intel Core i5`, so
  whole words throughout would rule out every Intel entry written that way. `Ryzen 5` still does not
  reach into `Ryzen 50`.
- **A reference match never rewrites the cell.** The list says what the *title* says; what the cell
  ought to say is a much larger claim, and nothing here makes it.

A rule naming a list that is not loaded is **refused at compile time** rather than ignored — the rule
exists because the column cannot be cleaned without it, and running on quietly would leave every title
in the file carrying the value the operator asked to have removed.

That refusal has a reach worth writing down. `CompiledRuleSet` carries the lists it was built with,
because the fix suggester re-compiles an edited copy of the set to preview each card — and a compile
with no lists to give refuses any rule naming one, the refusal is caught as "this fix does not work",
and **every** card on the file disappears rather than just the ones touching that column. Silent, and
indistinguishable from the suggester having nothing to say.

Narrowing five thousand entries happens once per **distinct cell value**, not once per row: a file
names ten or so processors across thousands of products, and `CompiledAttribute.ConsistentWith` caches
on that.

**When the list comes up short, the leftover report says so by name.** A title reading `Ryzen3-30`
against a cell reading `AMD Ryzen 3` used to be reported as "nothing claims this word", which is
wrong and sends the operator looking for a column to add. The column is already right; what is
missing is the rest of the entry. `TitleLeftoverCause.ReferenceMissing` says that instead, names the
column, and prints the value that would close it — `AMD Ryzen 3 30`.

It stops there, and deliberately. On the export this came from, looking that value up shows there is
no such processor: `AMD Ryzen 3 7330U` exists and `AMD Ryzen 3 30` does not, so the title is wrong and
the right outcome is the one the tool already produces — leave it, report it. Whether a model exists
is not something a cleaner can know, so the report names the decision rather than making it.

### Rule sets

One per category, stored in `%LOCALAPPDATA%\YeniRPA\TitleCleaner\title-rules.json` by `TitleRuleStore`
— atomic write, one `.bak` generation, and a file that will not parse is an error rather than a silent
fresh start. Same treatment, and the same reason, as `SellerGroupStore`: rule sets are what the
category team decided, they are not derived from any export, and nothing can rebuild them.

**Attribute order is load-bearing.** Where two attributes could claim one stretch of title the longer
match wins and ties fall to whichever comes first, so `Dizüstü İş İstasyonu` has to be evaluated
before `İş İstasyonu`.

`TitleRuleSuggester` proposes a starting set by reading the uploaded file — it is what makes "every
category gets its own rule set" affordable. The proposal is a **draft** for the editor and is never
applied on its own. Two things about it are deliberate:

- **It measures `Remove` rather than guessing it**, by running the real engine over the sample. A GPU
  column whose values never appear in the titles arrives switched off.
- **It probes every column at once, not one at a time.** Boundary validity depends on which *other*
  attributes claimed the characters next to a span, so `1TBSSD` only comes apart while both the
  capacity rule and the disk-type rule are present. Measured alone, both halves of every glued token
  match nothing — and glued tokens are exactly how these titles are written.

**Not every column that matches should be given a rule.** A laptop export carries
`Kutu İçeriği (tr_TR) = Bilgisayar` on every row, and that word does appear in the titles — so the
proposal offers it, switched on. Accepting it cuts `Bilgisayar` out of `Dizüstü Bilgisayar` and leaves
the product type stranded. The column describes what is in the box, not what the product is, and no
statistic can tell those apart; the rule editor is where a person decides. `ClaimedWords` at least
stops the suggester then proposing a product-type card built on a word that column has taken.

A column of bare numbers is never proposed for removal unless its own name says what the unit is
(`RAM`, `Ekran Boyutu`, `Kapasite`). `16` is a screen size, a model name and a fragment of a model
code at once, and this guard covers every kind except `Measure` — a column of `16`/`12` is short and
closed enough to be read as a catalogue, and a catalogue of bare numbers searches titles just as
literally as free text does.

### What a real export changed

The module was built against the reference title above and then run over a 100-product laptop export
(298 columns). Four things only that file could have taught it, all of them now covered by tests:

- **The row under the header is not a product.** A marketplace import template carries the technical
  field codes there — `TITLE__TR_TR`, `BRAND`, `PROD_FEAT_16858`. Read as data it seeds every alias
  catalogue with a field code and makes a column that is genuinely empty look like it holds one
  value, which is how a 40-column file proposed 300 rules. `IsFieldCodeRow` keeps it out of the
  cleaning and out of the statistics but **leaves it in the output**, because the marketplace's own
  importer needs it back. The proposal went from 298 rules to 47.
- **Titles repeat a measurement constantly, and a repeat is not a typo.** `RTX 5070 8GB 8GB 512GB
  SSD` is a graphics card's own memory beside the system RAM. Removing every match — which is what
  it used to do — deleted the card's memory out of the title, and only on the rows where the two
  sizes happened to be equal, so it was invisible on the rows either side. More than one match is
  now reported and nothing is removed; giving the other column its own rule resolves it, and the
  message says so. `Greedy` also hands out one span per attribute before any second one, so two
  rules wanting the same repeated value get one apiece instead of the first taking both.
- **A measured column gets only the units it actually uses**, not its whole family. A cache column
  is always written in MB; handed GB/TB/MB it treated every `8GB` in a title as its own and reported
  a conflict against its 40 MB on **78 rows out of 100**. The mirror case — a disk column whose
  sample is all GB not recognising a later `1TB` — announces itself under *Başlıkta yok* with the
  value still in the title. A false conflict announces nothing.
- **A unit the catalogue has never heard of is still a unit.** `Ekran Yenileme Hızı` holds `165 Hz`
  and was being read as a catalogue of values, because `GHz` and `MHz` were known and plain `Hz` was
  not — see [Any category, no code change](#any-category-no-code-change).
- **A bare number is refused per row, not per column.** A `Text` or `Alias` cell holding nothing but
  a number is asking to delete an unqualified number from a title, which is the deletion the
  `Measure` rules exist to refuse; it is refused there too. Doing it per column instead — the first
  attempt — let one numeric value among a hundred processor models switch removal off for the other
  ninety-nine.

Review rows on that export went from 92 of 100 to 18, and the 18 are real: four rows where the RAM
and the graphics memory are the same size, and fourteen where the processor cell and the title spell
the model differently.

### Any category, no code change

Nothing in the engine knows what a laptop is. `TitleCleanBuilder`, `AttributeMatcher`, `TitleFold`
and `TitleRuleStore` deal in columns, values and spans; the naming standard lives entirely in the
rule set, which is data. A washing-machine file cleans the same way a laptop file does, and
`TitleRuleSuggesterTests.AWhiteGoodsFileWorksWithNoCodeThatKnowsAboutWhiteGoods` is there to prove
it stays that way.

The one place category knowledge used to leak in was the **suggester's unit catalogue**. It was a
gate: a column whose unit was not in the list could not be a measured attribute at all. That is
untenable for a marketplace — `dB`, `bar`, `devir`, `kWh`, `MP`, `ay` would each be a code change,
a standing tax on every new category, and the miss is silent (the column quietly becomes a catalogue
of values instead). So the catalogue is now an **enrichment, not a gate**:

| The column writes | What it gets |
|---|---|
| A unit the catalogue knows (`512 GB`, `15,6 inç`) | The family's observed units, with their spelling variants and conversion factors — `inç`/`inch`/`"` as one thing, `GB`↔`TB` comparable |
| Anything else, used consistently (`165 Hz`, `52 dB`, `1400 devir`) | That token, spelled the way the column spells it |
| Bare numbers (`16`, `8`) | The unit read off the **titles** — what follows that number on the same row |

Two guards keep the open-ended path safe, and both already existed. A measured value is only ever
matched **with its unit**, so no bare number becomes a candidate; and a span may not cut into a word,
so a unit that happens to read like a common word (`ay` inside `ayarlı`) is rejected on the boundary
check rather than on a list of words this class would otherwise have to maintain.

An undeclared unit is proposed with **`Düzelt` off**: the canonical spelling of a unit nobody
declared is not knowable, and "correcting" a processor model of `8745HX` into `8745 HX` would damage
the cell. The value still matches and still leaves the title; only the rewrite stops, and the editor
is told why.

Reading the unit off the titles replaced a list of column-name hints (`ram`, `bellek`, `ekran`,
`disk` → GB/inch). That list only ever knew about laptops and could not be extended to a
marketplace's category list; the titles are where the unit is actually written, whatever the column
is called.

### Suggested fixes

The review table reports problems; it does not resolve them. Working through it meant reading a row,
working out which rule was wrong, finding that rule among forty and editing the right cell — then
doing it again for the next row saying exactly the same thing.

`TitleFixSuggester` groups the review rows into **scenarios** and proposes one rule change each. On
the laptop export the eighteen review rows were three scenarios (11 rows, 4 rows, 3 rows), and
applying all three took the review count to **zero** in one request. Three kinds are offered:

| The row says | The fix |
|---|---|
| Title and cell spell one thing two ways | Fold the title's spelling into the cell value's alias group |
| The value appears twice | Give the longer phrase around the other occurrence to the column that owns it, and bar that column from removing anything |
| The cell holds a bare number | Adopt the title's full phrase as that value's spelling |

Four things about it are deliberate:

- **Only changes that generalise are suggested.** A fix is written into the rule set and therefore
  acts on the whole file, so "the cell on this row is wrong" — a data error in one product — is never
  offered. Those stay in the review list and go out with the workbook.
- **A fix that would leave the rule set exactly as it was is not a fix.** `AddSpelling` refuses to
  move a spelling that already belongs to a different value — "FreeDOS" is not another way of writing
  "Windows 11 Pro", and the catalogue carrying both as their own values is what says so. Offering the
  merge anyway produced a card that could not be got rid of: applying it changed nothing, the row
  came back unfixed, and the card was drawn again. `Build` now compares the edited rule set against
  the one it started from and drops the card, so such a row — one product whose cell disagrees with
  its own twin — stays in the review list with nothing offered.
- **The card's before/after is produced by the real engine**, by cleaning a sample row under the
  changed rule set. What the operator is shown is what they get.
- **The cell's own spelling stays at the head of a merged group.** It is what a cell gets rewritten
  to, so putting the title's spelling first would overwrite the catalogue with title text.
- **Nothing is saved.** Applying updates the editor and re-runs the preview; the rule set is written
  when the operator presses Save, like everywhere else here.

**A proposed phrase is anchored on the value's whole alias group, not on the cell's own text.** The
marketplace's RuleSet defines `Notebook OR Laptop OR Dizüstü Bilgisayar OR …` as one value, and a
seller writes titles ending `Taşınabilir Bilgisayar` — a spelling that group has never heard of.
Anchored on the cell alone the word `Notebook` is nowhere in that title and no card is offered;
anchored on the group, `Bilgisayar` is, and the phrase around it is the spelling worth adopting.

That is also the guard that keeps this from being a correlation. On the same export `Gümüş` and
`Aspire Lite` appear on exactly the same rows, and anything that proposed a phrase because it *turns
up alongside* a value would offer to make a model name the spelling of a colour. A value with one
spelling gets one spelling's worth of anchor words, and a colour shares none of them with a model name.

**A title the field cut short still gets a card.** The proposers all want a whole word: one wants a
title word that equals a word of the cell's value, the other wants a word one letter off. A
hundred-character cut leaves neither. On the second seller's file all eleven `İş İstasyonu` rows were
cut before `İstasyonu` finished — nothing anchored, no card, and the leftover report could only say
"nothing claims this" about `Taşınabilir`, while `İş` at two letters never reached the report at all.
The operator saw the residue with no way to learn what to do about it.

So a third proposer reconstructs the phrase: the title's own unclaimed words, with the word the cut
broke completed from the cell. Two conditions, and the second is what keeps it sane — the last word
must be a *proper* prefix of the cell's last word, and the words before it must spell the rest of the
value in order. Without the second, a title ending `… Taşınabilir İş` reads its `İş` as a cut-off
`İstasyonu` and proposes `Taşınabilir İstasyonu`, eating a word. It also reads across the scenario's
rows rather than only the first: a cut lands wherever it lands, and the row that can answer is rarely
the first one.

**Worked example — the product type, which every category asks about.** A laptop file's titles end
`Dizüstü Bilgisayar`, `Taşınabilir Bilgisayar`, `Dizüstü` and `Dizüstü Bi`, and the cell says
`Notebook`. That is four spellings of one value, and none of them has to be typed twice: the work is
**per category, not per file**.

1. Upload the marketplace's RuleSet. It already defines
   `Ürün Tipi = Laptop OR Notebook OR Dizüstü Bilgisayar OR Dönüştürebilir Dizüstü Bilgisayar OR …`
   for that category, and `SuggestCategoryTypes` offers the whole group as one card. Those spellings
   join **the group this row's cell already belongs to**, whatever it is headed by: a column carrying
   `Notebook` from an earlier file must not end up with a second group headed `Laptop`, or the cell
   would resolve to one value and the title's `Dizüstü Bilgisayar` to another and every row would
   read as a conflict.
2. The spellings the marketplace has never heard of — `Taşınabilir Bilgisayar` is this seller's own —
   arrive as one `AdoptPhrase` card each, anchored on the group rather than on the cell.
3. The cut-off ones (`Dizüstü Bi`) need nothing: see
   [the word the field ran out of room for](#three-ways-a-title-writes-a-value-the-cell-does-not).
4. To have the **cell** rewritten to `Laptop`, put `Laptop` first in that group's `Değerler` box —
   the first spelling is the canonical one — and leave `Düzelt` on.

**A protector never hands its phrase back to the column that reported the problem.** That card gives a
phrase to some *other* column and switches that column's removal off; pointed at the reporting column
it turns off the very removal the operator is trying to get working, and leaves behind a catalogue
value no cell resolves to. One real rule set acquired a bare `Ultra9` under its processor column that
way — after which every row read as a conflict, because the cell said `Intel Core Ultra 9` and the
title's `Ultra9` now named a different value. The picker no longer offers that column and the apply
endpoint refuses it, so the browser behaving is not what stands between the two.

**A phrase is read off the title, not out of a concatenation of it.** The search that finds where a
value sits used to join two adjacent words to look for it, which is right for a value written across
a gap — and wrong when the value sits whole inside the second word. `32GB 1TBSSD+2TBSSD` answered
`SSD` by claiming both words, and the phrase came back carrying the RAM the row had already removed.
The pair is now only tried where neither word holds the value on its own. `ClaimedWords` also folds a
value's spaces out (`32 GB` → `32gb`), or a column that had plainly removed something looked as
though it had claimed nothing.

**A digit is never a typo.** `Ryzen7` and `Ryzen 3` are one character apart and are two different
processors; adopting one as a spelling of the other would clean every Ryzen 7 title as a Ryzen 3 from
then on. `OneLetterApart` spends its one difference only on letters — which is what people actually
mistype, `Emaya` for `Emaye`.

The apply endpoint **recomputes the suggestions server-side** and takes only ids from the browser, so
a page cannot hand over a rule edit of its own. Fix ids are derived from the scenario rather than its
position in a list, which is what lets them mean the same thing on that second pass.

Two smaller things the engine needed for this. The bare-number guard now tests **the text that would
be cut** rather than what the cell holds — deleting an unqualified number from a title is the
dangerous act, and a cell reading `465` whose catalogue maps it onto the title's `Ultra 5 465` is
asking to delete a phrase, which is safe. And `TitleAttributeResult` carries a `Reason`, because
`Ambiguous` has three causes needing three different fixes and two of them are otherwise
indistinguishable — recovering the cause by parsing the Turkish message would break on the next
rewording.

### The output

One sheet, and the uploaded file's own column layout — **the title column holds the cleaned title**
and the attribute cells their corrected values, so it goes straight back to the marketplace. Nothing
is appended and no second sheet is written.

It used to carry three sheets and twelve extra columns: the old title, the row's verdict, an error
list, and a verdict column per rule, beside an `Orijinal` copy and a `Kural Seti` record. All of it
was something the category team had to strip before the file could be uploaded, and the same
information is on screen after a run — the review table, the per-column table, and their own export
buttons. **The consequence is real and was accepted deliberately:** a cleaned title cannot be
reconstructed from the result and this file no longer carries a copy of the input, so the uploaded
file is the only way back and has to be kept.

The output is not a byte-for-byte copy of a marketplace template either: validation dropdowns,
reference sheets and cell formatting are not reproduced. What is preserved is the column layout and
every row, including the technical code row a template carries under its header.

Writing the clean title into the title column rather than beside it is not cosmetic. The marketplace
reads that column: this sheet used to leave the old title there and put the clean one in an appended
column, which meant uploading it corrected every attribute and changed no title at all. On a
298-column export nobody could see the difference either, because the appended column sat past
everything anyone scrolls to.

Re-uploading the output and cleaning it again changes nothing further
(`TitleCleanWorkbookTests.ReUploadingTheOutputChangesNothingFurther`). A second pass that kept eating
characters would corrupt a catalogue one re-run at a time, and every individual run would look like it
had worked.

The Excel download **re-derives everything server-side from the uploaded file** rather than taking
rows back from the browser — the same rule as Seller Offer Warnings. The engine is deterministic, so
the download is what the preview showed.

Units and alias groups reach the browser **already flattened** into one string each
(`GB=gb|gigabayt@1 ; TB=tb|terabayt@1024`). The encoding lives in `TitleRuleStore` and nowhere else:
a second implementation of it in JavaScript would be free to drift from the one the Excel round trip
uses, and the drift would surface as a rule that quietly stopped matching.

### One shared fix this module needed

`TabularFile.ParseCsvLine` treated a `"` anywhere in a field as opening a quoted field. A screen size
of `16"` therefore switched quoting on mid-cell and swallowed the rest of the line into it, silently
emptying every column after it. A quote now only opens a quoted field at the **start** of one, which
is what RFC 4180 says and the only behaviour under which the affected rows were not already corrupt.
Covered by `TabularFileTests.AnInchMarkMidFieldDoesNotSwallowTheRestOfTheLine`.

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
tracking code, carrier canonicalisation, and — at greater length than any of them — which characters
Title Cleaner is allowed to delete out of a product title. That last group is where the risk in this
repo is concentrated: every other result can be checked by looking at it, while a title that lost
four characters too many looks perfectly reasonable and is noticed weeks later with the original
gone. They run on CSV built in memory and go through the same
readers the app does. The test packages are a **build-time** dependency only — the app itself still
ships with no external runtime dependencies.

`TitleCleanerRealFileTests` is the exception to "built in memory": it runs the engine end to end over
the sample exports served from `wwwroot`, which the build already copies into the test output. The
unit tests next door pin one rule each against a title written to exercise it; these pin the whole
thing against files nobody wrote for a test — 300 columns, a technical field-code row, titles typed by
five different sellers — and every rule in this module came from such a file in the first place. Two
of its checks are invariants rather than expected strings: nothing an attribute reported as removed is
still in the cleaned title, and a second pass takes nothing further out.

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
- **The Title Cleaner rule editor is the one screen in this app that edits rather than reports**, and
  it is the one place a `<table>` was the wrong container. A rule is a form with ten fields, one of
  which holds forty alias groups; as eleven columns behind a 720px minimum the boxes were too narrow
  to read what was in them and the whole thing scrolled sideways. It is now a `.tc-rules` list: a
  summary line you can scan forty of — name, type, four flag letters, the match badge — with the
  editing underneath it at full width. The two catalogues are auto-growing textareas holding **one
  entry per line**; `;` still separates them in a workbook cell, and `TitleRuleStore` remains the only
  implementation of that format (`EncodeAliasLines` is the same encoder with a different join, and
  the parser takes either separator). Every colour is borrowed: `--accent` for a lit flag, the app's
  own `.badge` for the match count, and the dashed disabled treatment for a field the row's type does
  not use. Nothing is hidden when it is locked — what was typed into a box survives a change of mind
  about the type.

```
src/YeniRPA.Web/
├── Services/
│   ├── OrderReportBuilder.cs        Build() -> xlsx, BuildData() -> dashboard JSON
│   ├── CarrierNames.cs              Free-text shipping company -> canonical carrier
│   ├── ReturnSlaReportBuilder.cs    BuildData() -> dashboard JSON
│   ├── ReturnListBuilder.cs         4 exports -> the Create Return input list
│   ├── SellerMailStore.cs           How an address cell is split, joined and checked
│   ├── SellerMailDirectory.cs       The uploaded seller -> e-mail list; id, then folded name
│   ├── OfferExportReader.cs         Streaming xlsx reader for the 200k-row offer export
│   ├── OfferSplitBuilder.cs         Offers with a 1-2 day lead time, grouped by seller
│   ├── OfferSellerWorkbook.cs       One seller -> Product SKU + lead time, one sheet
│   ├── OfferMailBuilder.cs          One seller -> subject, body, and the containment rule
│   ├── OfferMailStore.cs            offer-warnings.json: templates, folder, typed addresses
│   ├── OfferBatchStore.cs           The prepared seller -> address -> file pairing, in memory
│   ├── VatSplitBuilder.cs           The VAT twin: products with no VAT rate, by seller
│   ├── VatSellerWorkbook.cs         │
│   ├── VatMailBuilder.cs            │ near-copies of the four above, deliberately not shared
│   ├── VatMailStore.cs              │
│   ├── VatBatchStore.cs             ┘
│   ├── TabularFile.cs               xlsx/csv -> table, plus the order-number key, the
│   │                                day-first template dates and the tracking-code rule
│   ├── TitleCleaner/
│   │   ├── TitleFold.cs             Position-preserving fold: spans map back to the original
│   │   ├── AttributeMatcher.cs      Every place a title expresses one attribute's kind of value
│   │   ├── TitleCleanBuilder.cs     Overlap resolution, boundary validation, removal, verdicts
│   │   ├── TitleRuleStore.cs        title-rules.json + the Excel and editor round trips
│   │   ├── TitleRuleSuggester.cs    A file -> a draft rule set, with Remove measured not guessed
│   │   ├── TitleFixSuggester.cs     The review list -> a handful of scenarios and their rule fixes
│   │   └── TitleCleanWorkbook.cs    One sheet: the upload's layout, cleaned in place
│   └── Automation/
│       ├── AutomationJobBus.cs      Single-run lock + SSE progress fan-out
│       ├── MiraklBrowser.cs         Playwright browser + encrypted saved login
│       ├── CreateReturnRunner.cs    The Create Return flow, one order at a time
│       ├── OutlookMailSender.cs     Outlook COM on one dedicated STA thread
│       └── OfferMailRunner.cs       The seller warning batch, one mail at a time —
│                                    shared by both warning modules, module name a parameter
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
    ├── js/offer-warnings.js         Seller Offer Warnings: two uploads, preview, send
    ├── js/vat-warnings.js           Seller VAT Warnings: the same, with the VAT filter
    └── js/title-cleaner.js          Title Cleaner: rule editor, preview, download
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
| `GET` \| `PUT` | `/api/offer-warnings/settings` | JSON `{ subjectTemplate, bodyTemplate, outputFolder, minOfferCount, ccAddresses, includeSignature, overrides }` | The templates, the folder, the threshold and the hand-entered addresses |
| `POST` | `/api/offer-warnings/prepare` | `offers`, `directory`, `sheetName`, `subjectTemplate`, `bodyTemplate`, `minOfferCount` | Writes one workbook per seller; returns `batchId`, one rendered mail per seller, the sellers with no address, a funnel and warnings |
| `POST` | `/api/offer-warnings/mails/excel` | JSON `{ mails, cc }` | `.xlsx` (one row per mail, body wrapped) |
| `POST` | `/api/offer-warnings/send` | JSON `{ batchId, mails, dryRun }` | `{ count, dryRun }`; the run continues in the background |
| `GET` | `/api/offer-warnings/status` | — | `{ outlookAvailable, outputFolder, batchId, batchSellers, maxMailsPerRun, isRunning, runningModule }` |
| `POST` | `/api/offer-warnings/check-outlook` | — | `{ available, error }` — starts Outlook if it is not running |
| | `/api/vat-warnings/…` | | The same six routes, same shapes — see [The guards](#the-guards) for why they are not one controller |
| `POST` | `/api/title-cleaner/suggest` | `file`, `name` | A draft rule set plus what the scan saw in each column — **saves nothing** |
| `GET` \| `PUT` | `/api/title-cleaner/rules` | JSON `{ sets }` | The saved rule sets, in the editor's flattened shape |
| `DELETE` | `/api/title-cleaner/rules/{name}` | — | The sets that remain. Confirmed in the browser first — a rule set cannot be regenerated |
| `POST` | `/api/title-cleaner/rules/excel` | JSON `{ sets }` | `baslik-kural-setleri.xlsx`, re-importable |
| `POST` | `/api/title-cleaner/rules/import` | `file` | The sets in that file — **does not save** |
| `POST` | `/api/title-cleaner/preview` | `file`, `ruleSet` \| `ruleSetName` | Dashboard JSON: KPIs, per-column verdicts, the rows needing review, the suggested fixes |
| `POST` | `/api/title-cleaner/fixes/apply` | `file`, `ruleSet`, `fixIds`, `targetColumns` | The rule set with those fixes applied — **does not save** |
| `POST` | `/api/title-cleaner/excel` | `file`, `ruleSet` \| `ruleSetName` | 3-sheet `.xlsx` (cleaned, original, rule set) |

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
