using System.Text.Json.Serialization;

namespace YeniRPA.Web.Models;

// ---------------------------------------------------------------------------
// POS Reconciliation (Bulut Tahsilat x Craftgate x Mirakl)
//
// Bulut Tahsilat's own "Sipariş Numarası" column comes back empty for every Akbank row (and a
// handful of rows on other banks). The two exports share a code that Bulut Tahsilat calls
// "Provizyon No" and Craftgate calls "authCode" — so a blank order number is backfilled by looking
// that code up in Craftgate and taking its "externalId". Once every row that can be backfilled has
// been, the enriched Bulut Tahsilat data is pivoted: rows = "POS Banka", columns = "Taksit",
// values = sum("İşlem Tutarı") and sum("Toplam Komisyon Tutarı").
//
// Mirakl is a third platform the operator pulls from for this workflow, but its role here is not
// defined yet — its upload is accepted for the screen's shape but not read. Consumed by
// wwwroot/js/pos-reconciliation.js.
// ---------------------------------------------------------------------------

/// <summary>One Bulut Tahsilat row after the Craftgate backfill has been attempted.</summary>
public sealed record PosReconciliationRow(
    [property: JsonPropertyName("islemKodu")] string IslemKodu,
    [property: JsonPropertyName("posBanka")] string PosBanka,
    [property: JsonPropertyName("kartBanka")] string KartBanka,
    [property: JsonPropertyName("islemTutari")] decimal IslemTutari,
    [property: JsonPropertyName("toplamKomisyonTutari")] decimal ToplamKomisyonTutari,
    [property: JsonPropertyName("provizyonNo")] string ProvizyonNo,
    [property: JsonPropertyName("taksit")] int Taksit,
    [property: JsonPropertyName("siparisNumarasi")] string SiparisNumarasi,
    [property: JsonPropertyName("siparisKaynagi")] string SiparisKaynagi);

/// <summary>A row whose blank "Sipariş Numarası" could not be backfilled — either "Provizyon No" had
/// no match at all in Craftgate's "authCode", or it matched more than one distinct "externalId".</summary>
public sealed record PosReconciliationUnmatchedRow(
    [property: JsonPropertyName("provizyonNo")] string ProvizyonNo,
    [property: JsonPropertyName("posBanka")] string PosBanka,
    [property: JsonPropertyName("kartBanka")] string KartBanka,
    [property: JsonPropertyName("islemTutari")] decimal IslemTutari,
    [property: JsonPropertyName("taksit")] int Taksit,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>One (bank, installment-count) cell's two summed values.</summary>
public sealed record PosReconciliationPivotCell(
    [property: JsonPropertyName("islemTutari")] decimal IslemTutari,
    [property: JsonPropertyName("toplamKomisyonTutari")] decimal ToplamKomisyonTutari);

/// <summary>One pivot row (a bank, or the closing "Genel Toplam" row). <paramref name="Cells"/> lines
/// up with <see cref="PosReconciliationPivot.Labels"/> by position — the last label and the last cell
/// are always the "Genel Toplam" column.</summary>
public sealed record PosReconciliationPivotRow(
    [property: JsonPropertyName("posBanka")] string PosBanka,
    [property: JsonPropertyName("cells")] IReadOnlyList<PosReconciliationPivotCell> Cells);

/// <summary>Rows = "POS Banka", columns = "Taksit" (installment count) plus a closing "Genel Toplam"
/// column, values = sum("İşlem Tutarı") and sum("Toplam Komisyon Tutarı") — built over every merged
/// row regardless of whether its order number could be backfilled.</summary>
public sealed record PosReconciliationPivot(
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("rows")] IReadOnlyList<PosReconciliationPivotRow> Rows);

/// <summary>The counts and totals shown on screen right after <c>generate</c>.</summary>
public sealed record PosReconciliationSummary(
    [property: JsonPropertyName("totalRecords")] int TotalRecords,
    [property: JsonPropertyName("originalOrderNumbers")] int OriginalOrderNumbers,
    [property: JsonPropertyName("backfilledViaCraftgate")] int BackfilledViaCraftgate,
    [property: JsonPropertyName("unmatched")] int Unmatched,
    [property: JsonPropertyName("conflicted")] int Conflicted,
    [property: JsonPropertyName("totalIslemTutari")] decimal TotalIslemTutari,
    [property: JsonPropertyName("totalKomisyonTutari")] decimal TotalKomisyonTutari);

/// <summary>
/// One finished run, held by <see cref="Services.PosReconciliationStore"/> between <c>generate</c>
/// (which builds it and returns the parts small enough to put on screen straight away — summary,
/// pivot, unmatched rows) and <c>download</c> (which reads the full row-level detail back out to
/// build the workbook).
/// </summary>
public sealed record PosReconciliationBatch(
    [property: JsonPropertyName("generatedAt")] DateTime GeneratedAt,
    [property: JsonPropertyName("rows")] IReadOnlyList<PosReconciliationRow> Rows,
    [property: JsonPropertyName("unmatched")] IReadOnlyList<PosReconciliationUnmatchedRow> Unmatched,
    [property: JsonPropertyName("pivot")] PosReconciliationPivot Pivot,
    [property: JsonPropertyName("summary")] PosReconciliationSummary Summary);
