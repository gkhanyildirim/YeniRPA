namespace YeniRPA.Web.Models;

/// <summary>
/// The stage wording for one loading scene. The scene itself is identical everywhere — only what it
/// claims to be doing changes, because a report reads a file and computes metrics while an
/// automation module narrows a list down to the rows it will act on.
/// </summary>
/// <param name="Caption">Standing line above the stage text.</param>
/// <param name="Stages">Stage labels, in order. The client walks them as its progress estimate
/// climbs; the last one holds until the response lands.</param>
public sealed record TowerLoader(string Caption, IReadOnlyList<string> Stages)
{
    public static TowerLoader Report() => new("Rapor hazırlanıyor",
    [
        "Dosya okunuyor",
        "Sütunlar eşleştiriliyor",
        "Satırlar ayrıştırılıyor",
        "Metrikler hesaplanıyor",
        "Grafikler çiziliyor"
    ]);

    public static TowerLoader Automation() => new("Liste hazırlanıyor",
    [
        "Dosya okunuyor",
        "Kayıtlar süzülüyor",
        "Kurallar uygulanıyor",
        "Liste derleniyor"
    ]);
}
