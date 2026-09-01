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

// Product Status reads rather than writes, so its own singleton is the result table: the scrape takes
// minutes and the progress stream carries only log lines, so the table has to outlive the run for the
// page to be able to ask for it — including after a reload.
builder.Services.AddSingleton<ProductStatusStore>();
builder.Services.AddSingleton<ProductStatusRunner>();

// Late Order Warnings. The store owns the seller → WhatsApp group mapping and the message templates;
// group names are not credentials, so unlike the Mirakl session it is not encrypted. WhatsAppBrowser
// keeps its login in a persistent Chrome profile instead of a storage-state file — the class doc
// explains why copying MiraklBrowser's approach would fail silently.
builder.Services.AddSingleton<SellerGroupStore>();
builder.Services.AddSingleton<WhatsAppBrowser>();
builder.Services.AddSingleton<LateOrderWhatsAppRunner>();

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

    builder.Services.AddSingleton<OfferMailStore>();
    builder.Services.AddSingleton<OfferBatchStore>();

    builder.Services.AddSingleton<VatMailStore>();
    builder.Services.AddSingleton<VatBatchStore>();
}

// Title Cleaner. The store owns the per-category naming standards; like the two mapping stores it
// holds no credentials, so it is not encrypted. A singleton because its load and save must not
// interleave across the several controller actions that reach it — the rule sets are hand-built and
// exist nowhere else, so a torn write has nothing to be rebuilt from.
builder.Services.AddSingleton<TitleRuleStore>();

// The marketplace's RuleSet, parsed once at upload. A singleton for the same reason, though this one
// is derived data: it can always be rebuilt by uploading the workbook again.
builder.Services.AddSingleton<CategoryRuleStore>();

// The value catalogues a rule may consult for spellings longer than its own cells carry — a processor
// list against a column reading "Intel Core Ultra 5" and titles reading "Ultra5 125H". Derived data
// like the RuleSet above, and a singleton for the same reason.
builder.Services.AddSingleton<TitleReferenceStore>();

var app = builder.Build();

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
