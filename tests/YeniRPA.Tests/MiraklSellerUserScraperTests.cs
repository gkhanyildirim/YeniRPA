using YeniRPA.Web.Services.Automation;

namespace YeniRPA.Tests;

/// <summary>
/// Reading a seller's Users tab out of the Mirakl operator back office.
///
/// <para>The fixtures are hand-written rather than dumps of a real page: they hold the structure the
/// parser actually depends on — a row carrying an address and a status word — and none of a real
/// seller's data. The parser is deliberately ignorant of Mirakl's class names, so a fixture that
/// copied them would be testing something the code does not read.</para>
/// </summary>
public class MiraklSellerUserScraperTests
{
    const string UsersPage = """
        <html><head><title>MediaMarktSaturn Marketplace - Users</title></head>
        <body>
          <header><span class="operator-account">operator@mediamarktsaturn.com</span></header>
          <table>
            <thead><tr><th>Username</th><th>Status</th><th>Roles</th><th>Last login</th></tr></thead>
            <tbody>
              <tr>
                <td><div class="name">Mehmet Y&uuml;ce</div><div class="mail">pazaryeri@guvengroup.com.tr</div></td>
                <td><span class="tag tag--success">Enabled</span></td>
                <td>All</td><td>8/20/2026 (GMT+2)</td>
              </tr>
              <tr>
                <td><div class="name">Ay&#351;e Demir</div><div class="mail">ayse.demir@guvengroup.com.tr</div></td>
                <td><span class="tag tag--muted">Disabled</span></td>
                <td>All</td><td>7/10/2026 (GMT+2)</td>
              </tr>
            </tbody>
          </table>
        </body></html>
        """;

    [Fact]
    public void BothUsersAreReadWithTheirNamesAndAddresses()
    {
        var users = MiraklSellerUserScraper.ParseUsers(UsersPage);

        Assert.Equal(2, users.Count);
        Assert.Equal("pazaryeri@guvengroup.com.tr", users[0].Email);
        Assert.Equal("ayse.demir@guvengroup.com.tr", users[1].Email);
    }

    /// <summary>HTML entities are what a Turkish name arrives as. "Ayşe" must not become "Ay?e".</summary>
    [Fact]
    public void NamesComeBackDecoded()
    {
        var users = MiraklSellerUserScraper.ParseUsers(UsersPage);

        Assert.Equal("Mehmet Yüce", users[0].Name);
        Assert.Equal("Ayşe Demir", users[1].Name);
    }

    /// <summary>
    /// <c>Contains("Enabled")</c> is true of "Disabled". Getting this wrong mails a commercially
    /// sensitive attachment to someone who has left the company.
    /// </summary>
    [Fact]
    public void DisabledIsNotReadAsEnabled()
    {
        var users = MiraklSellerUserScraper.ParseUsers(UsersPage);

        Assert.True(users[0].Enabled);
        Assert.False(users[1].Enabled);
    }

    /// <summary>
    /// The operator's own address is on every page of the back office. Requiring a status word in the
    /// same row is what keeps it out — otherwise every seller would be mailed to the operator.
    /// </summary>
    [Fact]
    public void AddressesOutsideTheUserTableAreIgnored()
    {
        var users = MiraklSellerUserScraper.ParseUsers(UsersPage);

        Assert.DoesNotContain(users, u => u.Email.Contains("operator@"));
    }

    [Fact]
    public void ASellerWithNoUsersYieldsNothing()
    {
        const string empty = """
            <table><tbody><tr><td colspan="4">No results</td></tr></tbody></table>
            """;

        Assert.Empty(MiraklSellerUserScraper.ParseUsers(empty));
    }

    [Fact]
    public void EmptyMarkupYieldsNothing()
    {
        Assert.Empty(MiraklSellerUserScraper.ParseUsers(""));
        Assert.Empty(MiraklSellerUserScraper.ParseUsers("   "));
    }

    // -----------------------------------------------------------------
    // The expired-session guard
    // -----------------------------------------------------------------

    /// <summary>
    /// The one that matters. An expired Mirakl session answers <b>HTTP 200</b> after redirecting to
    /// <c>/login</c>, so nothing about the status code looks wrong. Miss it and every seller parses as
    /// "no users", which — written back into the table — clears every address the operator has.
    /// </summary>
    [Fact]
    public void AnExpiredSessionIsRecognisedByTheFinalUrlDespiteA200()
    {
        Assert.True(MiraklSellerUserScraper.LooksLikeSignInPage(
            "https://mediamarktsaturn.mirakl.net/login",
            "<html><head><title>MediaMarktSaturn Marketplace - Sign in</title></head><body></body></html>"));
    }

    /// <summary>The backstop, for a sign-in page served in place without a redirect.</summary>
    [Fact]
    public void ASignInBodyIsRecognisedEvenOnTheRequestedUrl()
    {
        Assert.True(MiraklSellerUserScraper.LooksLikeSignInPage(
            "https://mediamarktsaturn.mirakl.net/mmp/operator/shop/11896/user?limit=100",
            "<title>MediaMarktSaturn Marketplace - Sign in</title>"));
    }

    [Fact]
    public void SsoRedirectsAreRecognised()
    {
        Assert.True(MiraklSellerUserScraper.LooksLikeSignInPage("https://accounts.google.com/o/oauth2/v2/auth", ""));
    }

    [Fact]
    public void ARealUsersPageIsNotMistakenForTheSignInPage()
    {
        Assert.False(MiraklSellerUserScraper.LooksLikeSignInPage(
            "https://mediamarktsaturn.mirakl.net/mmp/operator/shop/11896/user?limit=100",
            UsersPage));
    }
}
