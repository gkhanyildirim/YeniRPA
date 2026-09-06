using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Services;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Moves this install's LiteDB-backed settings — the seller/WhatsApp group mapping, Title Cleaner's
/// rule sets, category rules and reference lists, and the offer/VAT warning templates — to and from a
/// single JSON file, so a new machine does not start from nothing.
///
/// <para><c>Export</c> is a plain <c>GET</c> rather than an AJAX call: a browser navigating straight to
/// it (or an <c>&lt;a download&gt;</c> link) is the simplest possible "download a file" contract, and
/// there is nothing here that needs a request body. <c>Import</c> posts the file and reports what it
/// did as <c>{ success, message, data }</c> — new endpoints in this app follow that shape; see
/// CLAUDE.md. <c>Export</c> itself carries no such envelope because its response is the file, not
/// JSON describing one.</para>
/// </summary>
[ApiController]
[Route("Settings")]
[SupportedOSPlatform("windows")]
public sealed class SettingsController : ControllerBase
{
    const string JsonContentType = "application/json";

    readonly DatabaseBackupService _backup;

    public SettingsController(DatabaseBackupService backup) => _backup = backup;

    [HttpGet("ExportDatabase")]
    public IActionResult ExportDatabase()
    {
        var bytes = _backup.Export();
        var fileName = $"YeniRPA_Backup_{DateTime.Now:yyyyMMdd}.json";
        return File(bytes, JsonContentType, fileName);
    }

    [HttpPost("ImportDatabase")]
    public IActionResult ImportDatabase(IFormFile? file)
    {
        if (file is not { Length: > 0 })
        {
            return BadRequest(new
            {
                success = false,
                message = "Please choose a backup file (.json).",
                data = (object?)null
            });
        }

        try
        {
            using var stream = file.OpenReadStream();
            var result = _backup.Import(stream);

            return Ok(new
            {
                success = true,
                message = $"Imported {result.Sections.Count} section(s): {string.Join(", ", result.Sections)}.",
                data = result
            });
        }
        catch (InvalidOperationException ex)
        {
            // Handled locally rather than left to ReportExceptionFilter: that filter's { error }
            // shape predates this endpoint, and every existing one keeps it — only endpoints written
            // after the { success, message, data } rule use it, this one included.
            return BadRequest(new
            {
                success = false,
                message = ex.Message,
                data = (object?)null
            });
        }
    }
}
