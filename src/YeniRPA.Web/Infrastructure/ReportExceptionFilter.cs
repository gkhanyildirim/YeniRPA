using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace YeniRPA.Web.Infrastructure;

/// <summary>
/// The report builders validate uploaded workbooks by throwing <see cref="InvalidOperationException"/>
/// with an operator-facing message (missing column, empty sheet, ...). Those messages are the real
/// diagnostic the user needs, so they are returned verbatim as <c>400 { "error": "..." }</c>.
/// Anything else stays an unhandled 500.
/// </summary>
public sealed class ReportExceptionFilter : IExceptionFilter
{
    readonly ILogger<ReportExceptionFilter> _logger;

    public ReportExceptionFilter(ILogger<ReportExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not InvalidOperationException ex)
            return;

        _logger.LogWarning(ex, "Report generation rejected the uploaded input.");

        context.Result = new BadRequestObjectResult(new { error = ex.Message });
        context.ExceptionHandled = true;
    }
}
