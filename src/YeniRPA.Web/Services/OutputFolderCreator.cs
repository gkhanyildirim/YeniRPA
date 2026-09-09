namespace YeniRPA.Web.Services;

/// <summary>
/// Creates the per-run output folder for VAT/Offer Warnings, recovering from a folder that cannot
/// be created instead of failing the whole prepare step over it.
///
/// <para>The app is run from several different Windows machines, and the saved "output folder"
/// setting is a plain absolute path — <c>C:\Users\&lt;name&gt;\AppData\Local\YeniRPA\VatOffers</c>
/// with a specific machine's account baked in. If the LiteDB file that holds it is ever copied or
/// synced onto a different machine (or a different Windows account on the same one), that path
/// belongs to nobody there: the current user typically cannot even create a folder under another
/// account's profile, so <c>Directory.CreateDirectory</c> fails with access denied. Rather than
/// surface that as a hard failure, this falls back to the current machine's own default folder,
/// then a uniquely-named sibling of that (in case a single folder in that tree is the problem — e.g.
/// left behind with bad ACLs by a run under a different account), then the machine's temp directory
/// as a last resort. Only if none of those can be created does this throw.</para>
/// </summary>
internal static class OutputFolderCreator
{
    /// <param name="preferredFolder">The saved/requested folder for this run — may belong to a
    /// different machine or account than the one running now.</param>
    /// <param name="runFolderName">The per-run subfolder name (a timestamp), reused for every
    /// fallback so the run's files always land in a folder named for when it ran.</param>
    /// <param name="fallbackRoot">This machine's own default folder — <c>IVatMailStore
    /// .DefaultOutputFolder</c> / <c>IOfferMailStore.DefaultOutputFolder</c> — always resolved from
    /// the current process's own environment, so it belongs to whoever is running it now.</param>
    public static string Create(string preferredFolder, string runFolderName, string fallbackRoot)
    {
        var candidates = new[]
        {
            preferredFolder,
            Path.Combine(fallbackRoot, runFolderName),
            Path.Combine(fallbackRoot, runFolderName + "-" + Guid.NewGuid().ToString("N")[..8]),
            Path.Combine(Path.GetTempPath(), "YeniRPA", Path.GetFileName(fallbackRoot), runFolderName),
        };

        Exception? last = null;
        foreach (var candidate in candidates.Distinct())
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"No writable output folder could be created under '{preferredFolder}' or '{fallbackRoot}': " +
            $"{last!.Message}", last);
    }
}
