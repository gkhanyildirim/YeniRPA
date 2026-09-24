using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Infrastructure;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;
using YeniRPA.Web.Services.TitleCleaner;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
{
    // Report builders signal bad input by throwing InvalidOperationException with a message that is
    // meant for the operator ("Required column 'Shipping deadline' was not found..."). Surface those
    // as 400 { error } instead of a 500 page.
    options.Filters.Add<ReportExceptionFilter>();

    // Health dashboard: one SystemLog row per POST action (report generated, settings saved,
    // database imported). Registered after ReportExceptionFilter but reads context.Exception before
    // that filter ever runs — exception filters only fire once the action-filter pipeline this sits
    // in has already returned, so nothing here interferes with the 400 it writes.
    options.Filters.Add<SystemLogActionFilter>();
})
.AddJsonOptions(options =>
{
    // Title Cleaner's rule sets travel as JSON in both directions and are meant to be readable —
    // "Measure" and "ÇAKIŞMA" say what they are where a bare 2 does not, and a rule set saved from
    // the browser has to mean the same thing as one typed into title-rules.json by hand. These are
    // the only enums this app puts on the wire.
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// A single orders export can be ~13 MB and the return report uploads three files at once, so the
// 30 MB Kestrel default is not enough headroom.
const long MaxUploadBytes = 300L * 1024 * 1024;

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxUploadBytes;
});

// The shared LiteDB database backing the settings stores below — one .db file under %LOCALAPPDATA%
// instead of one hand-rolled atomic-write JSON file each. ConnectionType.Shared inside LiteDbContext
// is what makes it safe for a second local instance to open the same file.
builder.Services.AddSingleton<ILiteDbContext, LiteDbContext>();

// The health dashboard's log. Registered before AutomationJobBus, which writes to it.
builder.Services.AddSingleton<ISystemLogStore, SystemLogStore>();

// Automation modules. Singletons because the state they own outlives the request that started it:
// there is one run slot for the whole app (AutomationJobBus), and one browser per target site — the
// browsers are deliberately not shared, because each site needs a different way of keeping its login
// and MiraklBrowser's would silently fail to persist WhatsApp's.
//
// Data protection encrypts the saved Mirakl session cookies at rest — they grant full operator access
// to the marketplace, so they never touch disk in the clear.
builder.Services.AddDataProtection();
builder.Services.AddSingleton<AutomationJobBus>();
builder.Services.AddSingleton<MiraklBrowser>();
builder.Services.AddSingleton<CreateReturnRunner>();
builder.Services.AddSingleton<MarkAsReceivedRunner>();
builder.Services.AddSingleton<SellerNotificationRunner>();

// Product Status reads rather than writes, so its own singleton is the result table: the scrape takes
// minutes and the progress stream carries only log lines, so the table has to outlive the run for the
// page to be able to ask for it — including after a reload.
builder.Services.AddSingleton<ProductStatusStore>();
builder.Services.AddSingleton<ProductStatusRunner>();

// Kargo Takip (17Track delivery check): also read-only, and also its own browser — 17track.net needs
// no login, so Track17Browser carries none of MiraklBrowser's session/StorageState machinery and runs
// headless. Track17BatchStore holds what "prepare" filtered out of the upload until "start" reads it
// back; Track17Store holds the finished run's delivered rows, the same way ProductStatusStore does.
builder.Services.AddSingleton<Track17BatchStore>();
builder.Services.AddSingleton<Track17Store>();
builder.Services.AddSingleton<Track17Browser>();
builder.Services.AddSingleton<Track17Runner>();

// Cargo Seller Report: matches a cargo-invoice export against the Marketplace return/exchange and MM
// Pazaryeri cargo data exports by tracking code. Read-only, no browser or login involved, so it sits
// next to Track17 rather than the Outlook modules below. CargoSellerReportStore holds the last
// generated report the same way Track17BatchStore/OfferBatchStore do — in memory only, deliberately
// left out of JsonToLiteDbMigrator, because the report is worthless after a restart anyway.
builder.Services.AddSingleton<CargoSellerReportStore>();

// POS Reconciliation: backfills Bulut Tahsilat's blank order numbers from Craftgate by Provizyon
// No / authCode, then pivots by POS Banka x Taksit. Same in-memory, single-batch shape as
// CargoSellerReportStore, for the same reason.
builder.Services.AddSingleton<PosReconciliationStore>();

// The two WhatsApp warning modules — Late Order Warnings and Incident Warnings. The store owns the
// seller → WhatsApp group mapping (shared by both) and each module's message templates; group names
// are not credentials, so unlike the Mirakl session it is not encrypted. WhatsAppBrowser keeps its
// login in a persistent Chrome profile instead of a storage-state file — the class doc explains why
// copying MiraklBrowser's approach would fail silently.
//
// One runner, not two: it only types a body into a named group and knows nothing about deadlines or
// incidents, so the module it is running for arrives as an argument to TryStart.
builder.Services.AddSingleton<ISellerGroupStore, SellerGroupStore>();
builder.Services.AddSingleton<WhatsAppBrowser>();
builder.Services.AddSingleton<WhatsAppMessageRunner>();

// The two Outlook warning modules — Seller Offer Warnings and Seller VAT Warnings. They share the
// sender and the runner and differ only in what they split out of the export.
//
// OutlookMailSender is a singleton because it owns a long-lived STA thread — Outlook's object model
// is apartment-threaded and driving it from ASP.NET's MTA request threads fails intermittently, so
// every COM call is marshalled onto that one thread. A per-request instance would spawn a thread and
// a COM connection per call.
//
// Each module owns two singletons: the settings file (templates plus the addresses entered by hand
// for sellers the uploaded list does not cover) and the prepared batch. The batch is a singleton
// because it is the server's copy of which address and which file belong to which seller, and the
// send endpoint reads it instead of trusting the browser.
//
// Guarded rather than registered unconditionally: these types are Windows-only, and the guard is what
// tells the platform analyser so instead of us suppressing it. Everything here sits inside it because
// neither module can work without Outlook anyway.
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<OutlookMailSender>();
    builder.Services.AddSingleton<OfferMailRunner>();

    builder.Services.AddSingleton<IOfferMailStore, OfferMailStore>();
    builder.Services.AddSingleton<OfferBatchStore>();

    builder.Services.AddSingleton<IVatMailStore, VatMailStore>();
    builder.Services.AddSingleton<VatBatchStore>();

    // Custom Mail: a free-form mail to whichever sellers the operator ticks after resolving them
    // against an uploaded address directory. No per-seller template or attachment to remember — the
    // subject/body/CC/BCC are typed fresh each campaign — but the hand-entered addresses for sellers
    // the directory does not cover are real operator input worth keeping, hence its own settings store
    // alongside the batch store that holds the prepare→send recipient pairing.
    builder.Services.AddSingleton<ICustomMailStore, CustomMailStore>();
    builder.Services.AddSingleton<CustomMailBatchStore>();

    // Bundles all six LiteDB-backed stores' data into one backup file. Registered here, not above
    // the guard, because it depends on IOfferMailStore/IVatMailStore, which only exist on Windows.
    builder.Services.AddSingleton<DatabaseBackupService>();
}

// Title Cleaner. The store owns the per-category naming standards; like the two mapping stores it
// holds no credentials, so it is not encrypted. A singleton because its load and save must not
// interleave across the several controller actions that reach it — the rule sets are hand-built and
// exist nowhere else, so a torn write has nothing to be rebuilt from.
builder.Services.AddSingleton<ITitleRuleStore, TitleRuleStore>();

// Seller Notification. The store owns every saved topic/message template — like the rule sets
// above, it holds no credentials, so it is not encrypted. A singleton for the same reason: its
// load and save must not interleave across the controller's GET/PUT/start actions.
builder.Services.AddSingleton<ISellerNotificationStore, SellerNotificationStore>();

// The marketplace's RuleSet, parsed once at upload. A singleton for the same reason, though this one
// is derived data: it can always be rebuilt by uploading the workbook again.
builder.Services.AddSingleton<ICategoryRuleStore, CategoryRuleStore>();

// The value catalogues a rule may consult for spellings longer than its own cells carry — a processor
// list against a column reading "Intel Core Ultra 5" and titles reading "Ultra5 125H". Derived data
// like the RuleSet above, and a singleton for the same reason.
builder.Services.AddSingleton<ITitleReferenceStore, TitleReferenceStore>();

// Carries any pre-LiteDB JSON settings file into the shared database, once. See the class doc for why
// OfferBatchStore/VatBatchStore/ProductStatusStore are deliberately not part of this.
builder.Services.AddSingleton<JsonToLiteDbMigrator>();

var app = builder.Build();

app.Services.GetRequiredService<JsonToLiteDbMigrator>().Run();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
