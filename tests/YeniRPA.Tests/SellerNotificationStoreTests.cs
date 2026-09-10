using LiteDB;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// Unlike <see cref="SellerGroupStoreTests"/>, this store has only one write path — the whole
/// template list is always posted and saved together — so there is no partial-field-clobbering
/// regression to pin here. What matters is the plain round trip and that an empty database behaves
/// like every other settings store: an empty list, not a throw.
/// </summary>
public class SellerNotificationStoreTests
{
    /// <summary>A real LiteDB, in memory — the store's contract is what LiteDB does with a
    /// positional record, so a hand-written fake would pin nothing that matters here.</summary>
    sealed class MemoryContext : ILiteDbContext, IDisposable
    {
        readonly LiteDatabase _database = new(new MemoryStream());

        public string DatabasePath => "(in memory)";
        public ILiteCollection<T> GetCollection<T>(string name) => _database.GetCollection<T>(name);
        public void Dispose() => _database.Dispose();
    }

    [Fact]
    public void AnEmptyDatabaseLoadsAnEmptyFileRatherThanThrowing()
    {
        using var context = new MemoryContext();
        var file = new SellerNotificationStore(context).Load();

        Assert.Empty(file.Templates);
    }

    [Fact]
    public void SavedTemplatesRoundTrip()
    {
        using var context = new MemoryContext();
        var store = new SellerNotificationStore(context);

        store.Save(new SellerNotificationTemplateFile([
            new SellerNotificationTemplate("t1", "Return notice", "Return information", "Please review the return."),
            new SellerNotificationTemplate("t2", "Undelivered", "Undelivered parcel", "The parcel was not delivered."),
        ]));

        var file = store.Load();

        Assert.Equal(2, file.Templates.Count);
        Assert.Equal("Return notice", file.Templates[0].Name);
        Assert.Equal("Undelivered parcel", file.Templates[1].Topic);
    }

    /// <summary>A save is a full replace, the same contract every other single-document store has.</summary>
    [Fact]
    public void ASecondSaveReplacesTheFirstEntirely()
    {
        using var context = new MemoryContext();
        var store = new SellerNotificationStore(context);

        store.Save(new SellerNotificationTemplateFile([
            new SellerNotificationTemplate("t1", "First", "Topic one", "Message one"),
        ]));
        store.Save(new SellerNotificationTemplateFile([
            new SellerNotificationTemplate("t2", "Second", "Topic two", "Message two"),
        ]));

        var file = store.Load();

        Assert.Single(file.Templates);
        Assert.Equal("Second", file.Templates[0].Name);
    }
}
