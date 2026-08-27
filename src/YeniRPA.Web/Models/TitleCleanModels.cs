namespace YeniRPA.Web.Models;

/// <summary>
/// How one attribute column's value is looked for in the product title.
///
/// <para>The three kinds differ in what they can <em>detect</em>, not just in how they match:</para>
/// <list type="bullet">
///   <item><see cref="Text"/> searches for the row's own cell value and nothing else, so it can only
///   report found / not found. A brand column of this kind cannot notice that the title names a
///   <em>different</em> brand — there is no catalogue to notice it against. It also has nothing to
///   correct towards: the match it finds is by construction spelled the way the cell already is, so
///   <see cref="TitleAttributeRule.Correct"/> can never fire on it.</item>
///   <item><see cref="Alias"/> searches for every spelling in the rule's catalogue, so a title
///   carrying a different known value is reported as a conflict.</item>
///   <item><see cref="Measure"/> searches for "number + unit" in the attribute's unit family, so a
///   title saying 16 GB against a cell saying 32 GB is reported as a conflict.</item>
/// </list>
/// </summary>
public enum TitleAttributeKind
{
    Text,
    Measure,
    Alias,
}

/// <summary>
/// One unit a <see cref="TitleAttributeKind.Measure"/> attribute may be written in.
/// </summary>
/// <param name="Canonical">How the unit is written back out. Whether a space separates it from the
/// number is derived from this: a unit starting with a letter gets one ("16 GB"), a punctuation unit
/// does not ("16\"").</param>
/// <param name="Spellings">Accepted spellings, folded before use. Longer spellings are tried first,
/// so "inch" cannot be shadowed by "in".</param>
/// <param name="Factor">Size of this unit in the attribute's base unit, or 0 when the units are not
/// convertible. Where two units both carry a factor, quantities are compared in the base — which is
/// what stops a cell reading "1024 GB" from being reported as a conflict against a title reading
/// "1TB". With no factor the unit must match exactly.</param>
public sealed record MeasureUnit(string Canonical, IReadOnlyList<string> Spellings, double Factor = 0);

/// <summary>One attribute column and what the cleaner may do with it.</summary>
/// <param name="Column">Header text of the column in the uploaded file.</param>
/// <param name="Remove">Whether a confirmed match is cut out of the title. This is the whole of the
/// removal policy: <b>anything without this flag survives untouched</b>. A GPU column left
/// unflagged is why "RTXPRO2000" stays in the reference title — the cleaner does not need to
/// recognise it, only to be told nothing about it.</param>
/// <param name="Correct">Whether a cell whose value agrees with the title but is written differently
/// ("16" against a title's "16GB") is rewritten into canonical form. Has no effect on a
/// <see cref="TitleAttributeKind.Text"/> attribute, which searches for the cell's own value and so
/// never finds a spelling to correct towards — the editor locks the box on those rows.</param>
/// <param name="FillFromTitle">Whether an empty cell may be filled from the title. Off by default:
/// it writes in the opposite direction from everything else here, so it is enabled per attribute
/// rather than assumed.</param>
public sealed record TitleAttributeRule(
    string Column,
    TitleAttributeKind Kind = TitleAttributeKind.Text,
    bool Remove = true,
    bool Correct = true,
    bool FillFromTitle = false,
    IReadOnlyList<MeasureUnit>? Units = null,
    IReadOnlyList<IReadOnlyList<string>>? Aliases = null)
{
    public IReadOnlyList<MeasureUnit> UnitList => Units ?? [];

    /// <summary>Alias groups; the first spelling in each group is the canonical one.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AliasGroups => Aliases ?? [];
}

/// <summary>
/// One category's naming standard: which column holds the title, and which attribute columns
/// participate.
/// </summary>
/// <param name="Attributes"><b>Order is load-bearing.</b> Where two attributes claim the same stretch
/// of title, the earlier one wins, so the longer and more specific value belongs first — "Dizüstü İş
/// İstasyonu" ahead of "İş İstasyonu".</param>
/// <param name="DecimalSeparator">What a corrected fractional value is written with. Turkish
/// catalogues are usually kept on ",", the reference titles on "." — it is one setting rather than a
/// guess per row, so a file cannot come back carrying both.</param>
public sealed record TitleRuleSet(
    string Name,
    string TitleColumn,
    IReadOnlyList<TitleAttributeRule> Attributes,
    string DecimalSeparator = ".")
{
    public IReadOnlyList<TitleAttributeRule> AttributeList => Attributes ?? [];
}

/// <summary>The file <c>TitleRuleStore</c> owns: every saved rule set, one per category.</summary>
public sealed record TitleRuleFile(
    int Version,
    string? UpdatedUtc,
    IReadOnlyList<TitleRuleSet> Sets);

// ---------------------------------------------------------------------
// The editor's shape
// ---------------------------------------------------------------------
//
// Units and alias groups are lists of lists, and both a spreadsheet cell and a table input hold one
// string. Rather than teach the browser to encode them — a second implementation of a format, in a
// second language, free to drift from the first — the rule set crosses the wire already flattened
// and comes back the same way. The encoding lives in TitleRuleStore alone, is what the Excel
// round trip already uses, and is covered by its tests.

/// <param name="Units">"GB=gb|gigabayt@1 ; TB=tb|terabayt@1024" — ";" between units, "|" between
/// spellings, what precedes "=" is canonical and what follows "@" is the size in the base unit.</param>
/// <param name="Aliases">"W11P|Windows 11 Pro ; W11H|Windows 11 Home" — ";" between groups, "|"
/// between spellings, the first in each group is canonical.</param>
public sealed record TitleAttributeForm(
    string Column,
    string Kind = "Text",
    bool Remove = true,
    bool Correct = true,
    bool FillFromTitle = false,
    string Units = "",
    string Aliases = "");

public sealed record TitleRuleSetForm(
    string Name,
    string TitleColumn,
    IReadOnlyList<TitleAttributeForm> Attributes,
    string DecimalSeparator = ".")
{
    public IReadOnlyList<TitleAttributeForm> AttributeList => Attributes ?? [];
}

public sealed record TitleRuleFileForm(
    int Version,
    string? UpdatedUtc,
    IReadOnlyList<TitleRuleSetForm> Sets);

/// <summary>What <c>POST /suggest</c> answers with: a draft in the editor's own shape, plus what the
/// scan saw in each column so the editor can explain its proposal.</summary>
public sealed record TitleSuggestionResponse(
    TitleRuleSetForm RuleSet,
    IReadOnlyList<TitleColumnHintDto> Columns,
    IReadOnlyList<string> Notes);

public sealed record TitleColumnHintDto(
    string Column,
    string Kind,
    bool Remove,
    int Filled,
    int Distinct,
    int Matched,
    IReadOnlyList<string> Samples,
    string? Note);

// ---------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------

/// <summary>What happened to one attribute on one row.</summary>
public enum TitleAttributeStatus
{
    /// <summary>The cell is empty and nothing was written into it.</summary>
    Empty,

    /// <summary>The cell has a value the title never mentions. Not an error — plenty of true
    /// attributes are simply left out of a title.</summary>
    NotInTitle,

    /// <summary>Found in the title, already written canonically. Removed if the rule says so.</summary>
    Ok,

    /// <summary>Found in the title and the cell agreed, but was written differently; the cell has
    /// been rewritten. Removed if the rule says so.</summary>
    Corrected,

    /// <summary>The title and the cell name different values. <b>Nothing is removed and nothing is
    /// rewritten</b> — which of the two is right is not something this tool can know.</summary>
    Conflict,

    /// <summary>The cell carries a bare number and the title offers more than one unit it could
    /// belong to, so the unit cannot be settled. Nothing is removed.</summary>
    Ambiguous,

    /// <summary>The cell was empty and was filled from the title (<see
    /// cref="TitleAttributeRule.FillFromTitle"/>).</summary>
    Filled,
}

/// <summary>
/// Why a row needs a human. <see cref="TitleAttributeStatus.Ambiguous"/> has three separate causes
/// and they need different fixes, so the status alone is not enough to act on — and two of them are
/// indistinguishable from the rest of the result, because both can leave
/// <see cref="TitleAttributeResult.TitleSaid"/> equal to the cell. Recorded here rather than
/// recovered by parsing the Turkish message, which would break the moment the wording changed.
/// </summary>
public enum TitleAttributeReason
{
    None,

    /// <summary>The title names one value, the cell another.</summary>
    Disagreement,

    /// <summary>The cell's value appears more than once in the title — one of them belongs to
    /// something else, typically a graphics card's own memory beside the system RAM.</summary>
    ValueRepeated,

    /// <summary>The cell holds a bare number and the title offers it more than one unit.</summary>
    UnitUnsettled,

    /// <summary>A text or catalogue cell holding nothing but a number, which has no identity to
    /// delete a title by.</summary>
    BareNumber,
}

/// <param name="TitleSaid">What the title carried, when that differs from the cell — the other half
/// of a conflict message.</param>
public sealed record TitleAttributeResult(
    string Column,
    TitleAttributeStatus Status,
    string OriginalValue,
    string Value,
    string? TitleSaid = null,
    string? Message = null,
    TitleAttributeReason Reason = TitleAttributeReason.None);

/// <summary>One row's outcome. <paramref name="CleanTitle"/> is what the title becomes; the original
/// is kept beside it so the change is always reversible.</summary>
public sealed record TitleCleanRow(
    int RowNumber,
    string OriginalTitle,
    string CleanTitle,
    IReadOnlyList<TitleAttributeResult> Attributes,
    IReadOnlyList<string> Errors)
{
    public bool HasConflict => Attributes.Any(a =>
        a.Status is TitleAttributeStatus.Conflict or TitleAttributeStatus.Ambiguous);

    public bool Changed => !string.Equals(OriginalTitle.Trim(), CleanTitle, StringComparison.Ordinal);
}

/// <summary>What a suggested fix would do to the rule set.</summary>
public enum TitleFixKind
{
    /// <summary>The cell and the title spell one thing two ways: fold the title's spelling into the
    /// cell value's alias group.</summary>
    MergeAlias,

    /// <summary>The value appears twice: give the longer phrase around the other occurrence to a
    /// column that may not remove it, so it is claimed and protected.</summary>
    ProtectPhrase,

    /// <summary>A bare number in the cell: treat the title's full phrase as that value's spelling.</summary>
    AdoptPhrase,
}

/// <summary>
/// One scenario out of the review list, with the rule change that would resolve it.
///
/// <para>Grouped, not per row. On a real export eighteen review rows were three scenarios — the same
/// "Gaming Laptop ≠ Oyun Bilgisayarı" on 78 of them — so a fix offered per row would ask the operator
/// the same question 78 times.</para>
/// </summary>
/// <param name="Id">Derived from the scenario itself, not from its position in the list: the apply
/// request recomputes the suggestions, so an id has to mean the same thing on both runs.</param>
/// <param name="Value">What gets written into the rule set. Editable on the card — the phrase is a
/// proposal, and correcting it there beats hunting for the row in the rule table.</param>
/// <param name="TargetColumn">Which column's rule the change lands on. Usually the column that
/// reported the problem; for <see cref="TitleFixKind.ProtectPhrase"/> it is the column that owns the
/// protected phrase instead, which may need choosing.</param>
/// <param name="Warning">Set when applying this has an effect beyond the reviewed rows — changing a
/// column's type, say.</param>
/// <param name="CellValue">The attribute value the scenario was about — the canonical spelling a
/// merged group keeps at its head. Carried as a field rather than recovered from
/// <paramref name="Problem"/>, which is display prose and would tie the rule edit to its wording.</param>
public sealed record TitleFix(
    string Id,
    TitleFixKind Kind,
    string Column,
    string TargetColumn,
    string Problem,
    string Action,
    string Value,
    string CellValue,
    int Rows,
    string SampleBefore,
    string SampleAfter,
    bool NeedsColumnChoice = false,
    string? Warning = null);

/// <summary>How one attribute fared across the whole file — the table the team tunes rules against.</summary>
public sealed record TitleAttributeSummary(
    string Column,
    TitleAttributeKind Kind,
    bool Remove,
    int Ok,
    int Corrected,
    int Conflict,
    int NotInTitle,
    int Empty);

/// <summary>
/// What the preview hands the browser. <paramref name="Rows"/> is the whole file's count while
/// <paramref name="Preview"/> and <paramref name="Conflicting"/> are capped — a truncated table has
/// to say it is truncated rather than quietly under-reporting how much needs review.
/// </summary>
public sealed record TitleCleanData(
    TitleRuleSet RuleSet,
    int Rows,
    int Changed,
    int Untouched,
    int ConflictRows,
    int CorrectedValues,
    int FilledValues,
    IReadOnlyList<TitleAttributeSummary> Attributes,
    IReadOnlyList<TitleCleanRow> Preview,
    IReadOnlyList<TitleCleanRow> Conflicting,
    int PreviewLimit,
    IReadOnlyList<string> Notes,
    IReadOnlyList<TitleFix> Fixes);
