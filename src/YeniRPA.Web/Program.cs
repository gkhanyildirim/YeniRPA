using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Infrastructure;
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

// Automation modules. All three are singletons: there is one browser, one saved Mirakl session and
// one run slot for the whole app, and the run outlives the request that started it.
//
// Data protection encrypts the saved session cookies at rest — they grant full operator access to
// the marketplace, so they never touch disk in the clear.
builder.Services.AddDataProtection();
builder.Services.AddSingleton<AutomationJobBus>();
builder.Services.AddSingleton<MiraklBrowser>();
builder.Services.AddSingleton<CreateReturnRunner>();

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
