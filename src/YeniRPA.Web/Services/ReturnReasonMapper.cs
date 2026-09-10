namespace YeniRPA.Web.Services;

/// <summary>
/// Maps "Talep Nedeni" values from return template A to the return-reason option Mirakl's
/// create-return picker shows. Return template B carries no reason column, and any value not in
/// the table below (including an empty one) falls back to <see cref="Other"/>.
/// </summary>
public static class ReturnReasonMapper
{
    public const string Other = "Other reason";

    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ürünü beğenmedim"] = "Don't like the product",
        ["Ürün arızalı çıktı"] = "Defective item",
        ["Vazgeçtim"] = "Changed my mind",
        ["Yanlış ürün seçtim"] = "Changed my mind",
        ["Diğer"] = Other,
        ["Ürün belirtilen özelliklere sahip değil"] = "Delivered product different from ordered product",
        ["Daha iyi bir fiyat mevcut"] = "Changed my mind",
        ["Sipariş geç teslim edildi"] = Other,
        ["Yanlış ürün geldi"] = "Delivered product different from ordered product",
        ["Ürün hasarlı teslim edildi"] = "Defective on arrival",
        ["Fazla ürün gönderildi"] = Other,
        ["Kullanılmış/açılmış ürün teslim edildi"] = "Defective on arrival",
        ["Ürün görselden farklı geldi"] = "Delivered product different from ordered product",
        ["Ürün parçası/aksesuarı eksik teslim edildi"] = "Missing item",
    };

    public static string Resolve(string? rawReason)
    {
        var trimmed = rawReason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Other;

        return Map.TryGetValue(trimmed, out var mapped) ? mapped : Other;
    }
}
