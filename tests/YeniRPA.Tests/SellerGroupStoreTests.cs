using LiteDB;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;

namespace YeniRPA.Tests;

/// <summary>
/// The one document in this app that no export can rebuild: the seller → WhatsApp group mapping, and
/// the message templates of the two modules that share it.
///
/// <para>Two failure modes are pinned here, both silent and both destructive. One module saving its
/// own settings must not wipe the other's half of the record — the mapping panel does not post the
/// incident templates and vice versa. And a document written before a field existed must still load,
/// because LiteDB binds this record through its constructor and supplies null for anything the stored
/// document does not carry.</para>
/// </summary>
public class SellerGroupStoreTests
{
    /// <summary>
    /// A real LiteDB, in memory. The store's contract is what LiteDB does with a positional record, so
    /// a hand-written fake would pin nothing that matters here.
    /// </summary>
    sealed class MemoryContext : ILiteDbContext, IDisposable
    {
        readonly LiteDatabase _database = new(new MemoryStream());

        public string DatabasePath => "(in memory)";
        public ILiteCollection<T> GetCollection<T>(string name) => _database.GetCollection<T>(name);
        public ILiteCollection<BsonDocument> Raw(string name) => _database.GetCollection(name);
        public void Dispose() => _database.Dispose();
    }

    static SellerGroupEntry[] Mapping() =>
    [
        new("11835", "Prodesk", "MediaMarkt - Prodesk"),
        new("11616", "Fressi Home", "MediaMarkt - Fressi"),
    ];

    // -----------------------------------------------------------------
    // Neither module may wipe the other's fields
    // -----------------------------------------------------------------

    /// <summary>
    /// The regression this whole split exists for. Saving the mapping table used to build a fresh
    /// <c>SellerGroupFile</c> from the request, which carries no incident fields — so every time the
    /// operator pressed "Save mapping", the Incident Warnings templates were deleted.
    /// </summary>
    [Fact]
    public void SavingTheMappingKeepsTheIncidentSettings()
    {
        using var context = new MemoryContext();
        var store = new SellerGroupStore(context);

        store.SaveIncidentSettings("incident envelope", "incident line", 5);
        store.SaveMapping(Mapping(), "late order envelope", "late order line");

        var file = store.Load();

        Assert.Equal("incident envelope", file.IncidentMessageTemplate);
        Assert.Equal("incident line", file.IncidentLineTemplate);
        Assert.Equal(5, file.IncidentThresholdDays);
        Assert.Equal(2, file.Entries.Count);
        Assert.Equal("late order envelope", file.MessageTemplate);
    }

    [Fact]
    public void SavingTheIncidentSettingsKeepsTheMappingAndLateOrderTemplates()
    {
        using var context = new MemoryContext();
        var store = new SellerGroupStore(context);

        store.SaveMapping(Mapping(), "late order envelope", "late order line");
        store.SaveIncidentSettings("incident envelope", "incident line", 5);

        var file = store.Load();

        Assert.Equal(2, file.Entries.Count);
        Assert.Equal("late order envelope", file.MessageTemplate);
        Assert.Equal("late order line", file.OrderLineTemplate);
        Assert.Equal("incident envelope", file.IncidentMessageTemplate);
    }

    /// <summary>Whole-document Save is the restore path and legitimately owns every field.</summary>
    [Fact]
    public void AWholeDocumentSaveReplacesEverything()
    {
        using var context = new MemoryContext();
        var store = new SellerGroupStore(context);

        store.SaveMapping(Mapping(), "late order envelope", null);
        store.SaveIncidentSettings("incident envelope", null, 9);

        store.Save(new SellerGroupFile(0, null, null, null, []));

        var file = store.Load();

        Assert.Empty(file.Entries);
        Assert.Null(file.MessageTemplate);
        Assert.Null(file.IncidentMessageTemplate);
        Assert.Null(file.IncidentThresholdDays);
    }

    // -----------------------------------------------------------------
    // Documents written before the incident fields existed
    // -----------------------------------------------------------------

    /// <summary>
    /// LiteDB binds <see cref="SellerGroupFile"/> through its primary constructor, matching stored keys
    /// to parameter names, and hands <c>null</c> to anything the document does not carry. That is why
    /// every field added to that record has to be nullable and go on the end: a non-nullable value type
    /// could not take the null, and <c>Load()</c> would throw on every existing installation.
    /// </summary>
    [Fact]
    public void ADocumentSavedBeforeTheIncidentFieldsExistedStillLoads()
    {
        using var context = new MemoryContext();

        // Exactly the shape the pre-Incident-Warnings store wrote: five fields, no incident keys.
        context.Raw("sellerGroups").Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Data"] = new BsonDocument
            {
                ["Version"] = 1,
                ["UpdatedUtc"] = "2026-09-01 10:00:00Z",
                ["MessageTemplate"] = "late order envelope",
                ["OrderLineTemplate"] = "• {orderNumber}",
                ["Entries"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["SellerId"] = "11835",
                        ["SellerName"] = "Prodesk",
                        ["GroupName"] = "MediaMarkt - Prodesk",
                    },
                },
            },
        });

        var file = new SellerGroupStore(context).Load();

        Assert.Equal("late order envelope", file.MessageTemplate);
        Assert.Equal("Prodesk", Assert.Single(file.Entries).SellerName);

        // The new fields read as "never set", which the controller turns into the shipped defaults.
        Assert.Null(file.IncidentMessageTemplate);
        Assert.Null(file.IncidentLineTemplate);
        Assert.Null(file.IncidentThresholdDays);
    }

    /// <summary>Adding the incident settings to such a document must not disturb what was already there.</summary>
    [Fact]
    public void TheIncidentSettingsCanBeAddedToAPreExistingDocument()
    {
        using var context = new MemoryContext();

        context.Raw("sellerGroups").Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Data"] = new BsonDocument
            {
                ["Version"] = 1,
                ["UpdatedUtc"] = "2026-09-01 10:00:00Z",
                ["MessageTemplate"] = "late order envelope",
                ["OrderLineTemplate"] = "• {orderNumber}",
                ["Entries"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["SellerId"] = "11835",
                        ["SellerName"] = "Prodesk",
                        ["GroupName"] = "MediaMarkt - Prodesk",
                    },
                },
            },
        });

        var store = new SellerGroupStore(context);
        store.SaveIncidentSettings("incident envelope", "• {orderNumber}", 3);

        var file = store.Load();

        Assert.Equal("late order envelope", file.MessageTemplate);
        Assert.Single(file.Entries);
        Assert.Equal(3, file.IncidentThresholdDays);
    }

    // -----------------------------------------------------------------

    [Fact]
    public void AnEmptyDatabaseLoadsAnEmptyFileRatherThanThrowing()
    {
        using var context = new MemoryContext();
        var file = new SellerGroupStore(context).Load();

        Assert.Empty(file.Entries);
        Assert.Null(file.IncidentThresholdDays);
    }

    [Fact]
    public void EverySaveStampsTheVersionAndTheUpdatedTimestamp()
    {
        using var context = new MemoryContext();
        var store = new SellerGroupStore(context);

        store.SaveIncidentSettings(null, null, 2);

        var file = store.Load();
        Assert.Equal(1, file.Version);
        Assert.NotNull(file.UpdatedUtc);
    }
}
