using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Controllers;

public sealed class HomeController : Controller
{
    /// <summary>
    /// The single page hosting all three modules. The model only feeds the Methodology tab, which
    /// documents the rules by reading them from the builders instead of restating them.
    /// </summary>
    public IActionResult Index() => View(new MethodologyViewModel());

    [Route("Home/Error")]
    public IActionResult Error() => View();
}
