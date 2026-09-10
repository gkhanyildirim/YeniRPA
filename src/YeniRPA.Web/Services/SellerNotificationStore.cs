using LiteDB;
using YeniRPA.Web.Models;

namespace YeniRPA.Web.Services;

/// <summary>The instance surface <see cref="SellerNotificationStore"/> exposes through DI.</summary>
public interface ISellerNotificationStore
{
    /// <summary>Where the data lives — the shared LiteDB file. Kept on the interface because the
    /// template editor shows it, the way the rule editor shows <c>ITitleRuleStore.FilePath</c>.</summary>
    string FilePath { get; }

    SellerNotificationTemplateFile Load();
    void Save(SellerNotificationTemplateFile file);
}

/// <summary>
/// Owns every saved Seller Notification template, in the <c>sellerNotificationTemplates</c>
/// collection of the shared LiteDB database (<see cref="ILiteDbContext"/>).
///
/// <para>Modelled on <see cref="TitleCleaner.TitleRuleStore"/>: one document holding the whole list,
/// because nothing here is ever read or saved one template at a time — the editor always posts the
/// full set back. Not encrypted, also like <c>TitleRuleStore</c>: template wording is not a
/// credential.</para>
/// </summary>
public sealed class SellerNotificationStore : ISellerNotificationStore
{
    const int DocumentId = 1;

    public sealed class Document
    {
        public int Id { get; set; }
        public SellerNotificationTemplateFile Data { get; set; } = null!;
    }

    readonly ILiteCollection<Document> _collection;

    public SellerNotificationStore(ILiteDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        FilePath = context.DatabasePath;
        _collection = context.GetCollection<Document>("sellerNotificationTemplates");
        _collection.EnsureIndex(x => x.Id, unique: true);
    }

    public string FilePath { get; }

    public SellerNotificationTemplateFile Load() =>
        _collection.FindById(DocumentId)?.Data ?? new SellerNotificationTemplateFile([]);

    public void Save(SellerNotificationTemplateFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var stamped = file with { Templates = file.Templates ?? [] };
        _collection.Upsert(new Document { Id = DocumentId, Data = stamped });
    }
}
