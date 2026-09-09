using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The VAT/Offer Warnings prepare step used to fail outright when its preferred output folder
/// could not be created — which happens for real when the saved folder is a different machine's
/// absolute path (the LiteDB file was copied/synced onto a machine where
/// <c>C:\Users\&lt;other-name&gt;\...</c> belongs to nobody). <see cref="OutputFolderCreator"/> now
/// recovers by falling back to this machine's own default folder, then a sibling of that, then temp.
/// </summary>
public class OutputFolderCreatorTests
{
    [Fact]
    public void APreferredPathThatCannotBeCreatedFallsBackToThisMachinesDefaultFolder()
    {
        var preferredRoot = MakeTempRoot();
        var fallbackRoot = MakeTempRoot();

        try
        {
            var runFolderName = "2026-09-09-1432";
            var blockedPath = Path.Combine(preferredRoot, runFolderName);

            // A file already sitting where the run folder needs to go is the same shape of failure
            // as an access-denied path belonging to a different account: CreateDirectory refuses
            // either one.
            File.WriteAllText(blockedPath, "");

            var created = OutputFolderCreator.Create(blockedPath, runFolderName, fallbackRoot);

            Assert.Equal(Path.Combine(fallbackRoot, runFolderName), created);
            Assert.True(Directory.Exists(created));
        }
        finally
        {
            Directory.Delete(preferredRoot, recursive: true);
            Directory.Delete(fallbackRoot, recursive: true);
        }
    }

    [Fact]
    public void AFallbackFolderThatIsAlsoBlockedFallsBackToASiblingOfIt()
    {
        var preferredRoot = MakeTempRoot();
        var fallbackRoot = MakeTempRoot();

        try
        {
            var runFolderName = "2026-09-09-1432";
            File.WriteAllText(Path.Combine(preferredRoot, runFolderName), "");
            File.WriteAllText(Path.Combine(fallbackRoot, runFolderName), "");

            var created = OutputFolderCreator.Create(
                Path.Combine(preferredRoot, runFolderName), runFolderName, fallbackRoot);

            Assert.NotEqual(Path.Combine(fallbackRoot, runFolderName), created);
            Assert.StartsWith(fallbackRoot, created);
            Assert.True(Directory.Exists(created));
        }
        finally
        {
            Directory.Delete(preferredRoot, recursive: true);
            Directory.Delete(fallbackRoot, recursive: true);
        }
    }

    [Fact]
    public void AnAlreadyWritablePreferredPathIsUsedAsIsWithoutTouchingTheFallback()
    {
        var preferredRoot = MakeTempRoot();

        try
        {
            var runFolderName = "2026-09-09-1432";
            var preferredPath = Path.Combine(preferredRoot, runFolderName);

            var created = OutputFolderCreator.Create(
                preferredPath, runFolderName, Path.Combine(preferredRoot, "unused-fallback"));

            Assert.Equal(preferredPath, created);
            Assert.False(Directory.Exists(Path.Combine(preferredRoot, "unused-fallback")));
        }
        finally
        {
            Directory.Delete(preferredRoot, recursive: true);
        }
    }

    static string MakeTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "YeniRPA-OutputFolderCreatorTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
