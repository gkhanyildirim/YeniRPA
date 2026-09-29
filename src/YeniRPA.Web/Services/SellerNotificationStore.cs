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

    /// <summary>
    /// Until the first save the document does not exist and the two stock templates are returned; after
    /// that the saved list is authoritative, so an operator who deletes a stock template keeps it gone.
    /// </summary>
    public SellerNotificationTemplateFile Load()
    {
        var data = _collection.FindById(DocumentId)?.Data;
        if (data is null) return new SellerNotificationTemplateFile(Defaults);

        return data with
        {
            Templates = [.. (data.Templates ?? []).Select(t => t with { Kind = SellerNotificationKinds.Normalize(t.Kind) })]
        };
    }

    public void Save(SellerNotificationTemplateFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var normalized = (file.Templates ?? [])
            .Select(t => t with { Kind = SellerNotificationKinds.Normalize(t.Kind) })
            .ToList();
        _collection.Upsert(new Document { Id = DocumentId, Data = new SellerNotificationTemplateFile(normalized) });
    }

    static readonly SellerNotificationTemplate[] Defaults =
    [
        new("default-return", "Return notification", "",
            "Değerli İş Ortağımız,\n" +
            "Sipariş için müşteri tarafından iade talebi oluşturulmuştur. İadenin kargo takip linkini sipariş içerisinden görebilirsiniz.\n" +
            "• İadeyi onaylamanız durumunda,   \"Geri Ödeme\" veya \"İptal\"   butonu kullanılarak iade işleminin tamamlanmasını,\n" +
            "• İadeyi reddetmeniz durumunda ise, red gerekçesini destekleyen ürün görselleri ve açıklayıcı bilgi ile birlikte tarafımıza sistem üzerinden iletmenizi rica ederiz.\n" +
            "İade ürün tarafınıza ulaştıktan sonra 48 saatlik süre içerisinde aksiyon alınmaması halinde, platform tarafından değerlendirme yapılarak aksiyon alınabileceğini hatırlatmak isteriz.\n" +
            "İyi çalışmalar dileriz.",
            SellerNotificationKinds.Return),
        new("default-undelivered", "Undelivered return", "",
            "Merhaba\n" +
            "Sipariş müşteriye teslim edilmeden iade olarak geri dönmüştür. Ücret iadesini tamamlamanızı rica ederiz.\n" +
            "MediaMarkt Pazaryeri Ekibi",
            SellerNotificationKinds.Undelivered)
    ];
}
