using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Infrastructure;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.Automation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
{
    // Report builders signal bad input by throwing InvalidOperationException with a message that is
    // meant for the operator ("Required column 'Shipping deadline' was not found..."). Surface those
    // as 400 { error } instead of a 500 page.
    options.Filters.Add<ReportExceptionFilter>();
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

// Late Order Warnings. The store owns the seller → WhatsApp group mapping and the message templates;
// group names are not credentials, so unlike the Mirakl session it is not encrypted. WhatsAppBrowser
// keeps its login in a persistent Chrome profile instead of a storage-state file — the class doc
// explains why copying MiraklBrowser's approach would fail silently.
builder.Services.AddSingleton<SellerGroupStore>();
builder.Services.AddSingleton<WhatsAppBrowser>();
builder.Services.AddSingleton<LateOrderWhatsAppRunner>();

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
