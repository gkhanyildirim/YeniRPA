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

/// <summary>
/// A named group of units that measure the same thing — what the editor offers as one ready-made
/// choice, and what the suggester recognises a column's values against.
/// </summary>
/// <param name="Label">What the operator picks from.</param>
/// <param name="Units">Declaration order is load-bearing: the suggester keeps it so canonical
/// spellings and factors stay as written here.</param>
public sealed record MeasureFamily(string Label, IReadOnlyList<MeasureUnit> Units);

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
/// <param name="AllowPartial">
/// Whether part of the cell's value may answer for the whole of it — a cell reading "CETINTAS EVII"
/// against a title that says only "Çetintaş", or "Temperli Cam" against one that says only "Cam".
///
/// <para>Off by default and enabled per attribute, and the reason is worth writing down because the
/// first attempt at this had it always on. A catalogue holding "Windows 11 Pro" then matched the
/// word <em>Pro</em> in "Dell Pro Max 16", and cut it out of a title that had nothing to do with an
/// operating system. The words a value is made of are ordinary words; that some of them are missing
/// from the title is not evidence that the rest of them mean the value. Only the operator knows
/// which columns are written this way — a brand or a material, not a product type.</para>
/// </param>
/// <param name="AllowSuffix">
/// Whether a match may run to the end of a word carrying a Turkish inflection — the title's
/// "Ankastre Ocaklar" answering a cell reading "Ankastre ocak". The whole word goes; no "lar" is
/// left behind.
///
/// <para>Off by default and enabled per attribute, because it is only safe where the values are
/// words. A column of model codes must never have it: "GLO 022SARS" ends in letters that a suffix
/// rule has no business reading, and a wrong extension here deletes part of a model name.</para>
/// </param>
/// <param name="ReferenceList">
/// Name of a <see cref="TitleReferenceList"/> this column may consult, or null. The list supplies
/// <em>longer</em> spellings than the cell carries — a processor catalogue's "Intel Core Ultra 5 125H"
/// against a cell reading "Intel Core Ultra 5" — so a title can be cleaned of the model code the cell
/// never mentions.
///
/// <para>Removal stays a whitelist: an entry is only ever used where the row's own cell value is
/// contained in it. See <c>AttributeMatcher.AddReference</c>.</para>
/// </param>
public sealed record TitleAttributeRule(
    string Column,
    TitleAttributeKind Kind = TitleAttributeKind.Text,
    bool Remove = true,
    bool Correct = true,
    bool FillFromTitle = false,
    bool AllowSuffix = false,
    bool AllowPartial = false,
    IReadOnlyList<MeasureUnit>? Units = null,
    IReadOnlyList<IReadOnlyList<string>>? Aliases = null,
    string? ReferenceList = null)
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
/// <param name="CollapseRepeats">
/// Whether a word written twice in a row — "Lenovo Ideapad Ideapad Slim3" — loses its second copy.
///
/// <para><b>Off by default, and it is the one setting that bends this module's central promise.</b>
/// Everything else here removes only what a column's own cell claims; this removes text nobody
/// claimed, on the strength of the repetition alone. That is a judgement about the seller's typing
/// rather than about the catalogue, so it is an explicit choice per rule set rather than something
/// the engine assumes.</para>
///
/// <para>Only repeats with no digit in them are ever collapsed. "RTX 5070 8GB 8GB" is a graphics
/// card's own memory beside the system RAM — the case this module already goes out of its way to
/// protect — and a rule that could not tell it from "Ideapad Ideapad" would not be worth having.</para>
/// </param>
public sealed record TitleRuleSet(
    string Name,
    string TitleColumn,
    IReadOnlyList<TitleAttributeRule> Attributes,
    string DecimalSeparator = ".",
    bool CollapseRepeats = false)
{
    public IReadOnlyList<TitleAttributeRule> AttributeList => Attributes ?? [];
}

/// <summary>The file <c>TitleRuleStore</c> owns: every saved rule set, one per category.</summary>
public sealed record TitleRuleFile(
    int Version,
    string? UpdatedUtc,
    IReadOnlyList<TitleRuleSet> Sets);

// ---------------------------------------------------------------------
// Reference lists
// ---------------------------------------------------------------------
//
// An attribute cell says what a product is; it does not always say it in full. A laptop export's
// processor column reads "Intel Core Ultra 5" while its titles read "Ultra5 125H", and no rule built
// out of that file can remove the model code, because no cell in it contains one.
//
// A reference list closes that gap without teaching the engine anything about processors: it is a
// column of full canonical values out of a workbook the operator uploads, and it is consulted only
// through the row's own cell. Nothing about it is specific to a category — a GPU catalogue, a panel
// list or a fabric list is the same shape and takes the same path.

/// <summary>
/// A named list of full canonical values, uploaded from a workbook column.
/// </summary>
/// <param name="Name">What a rule refers to it by. Unique within the file, folded for comparison.</param>
/// <param name="SourceName">The workbook and column it came from — the only record of which edition
/// is loaded, the same reason <see cref="CategoryRuleFile.SourceName"/> exists.</param>
/// <param name="Values">Entries as the source spelled them, de-duplicated, in the source's order.</param>
public sealed record TitleReferenceList(
    string Name,
    string? SourceName,
    IReadOnlyList<string> Values)
{
    public IReadOnlyList<string> ValueList => Values ?? [];
}

/// <summary>The file <c>TitleReferenceStore</c> owns. Kept apart from <c>title-rules.json</c> because
/// a catalogue runs to thousands of lines and that file is meant to stay hand-readable.</summary>
public sealed record TitleReferenceFile(
    int Version,
    string? UpdatedUtc,
    IReadOnlyList<TitleReferenceList> Lists)
{
    public IReadOnlyList<TitleReferenceList> ListList => Lists ?? [];
}

/// <summary>What the editor shows about one loaded reference list.</summary>
public sealed record TitleReferenceStatus(string Name, string? SourceName, int Values);

// ---------------------------------------------------------------------
// The marketplace's own category rules
// ---------------------------------------------------------------------
//
// The RuleSet workbook the marketplace publishes carries, among much else, one line per rule reading
// "Ürün Tipi = A OR B OR C" against a category. Read that way it is a ready-made catalogue: for each
// category, every product type it accepts and every spelling it accepts it under. That is the same
// shape as a TitleAttributeRule's alias groups, which is what makes it usable here at all.

/// <summary>
/// One rule out of the RuleSet workbook: the product types it accepts, and the category they belong
/// to.
/// </summary>
/// <param name="Category">The marketplace's own code, e.g. <c>HOBS</c>.</param>
/// <param name="CategoryTr">What the operator sees on a product file, e.g. <c>OCAKLAR</c>. Falls
/// back to <paramref name="Category"/> where the sheet has no Turkish label.</param>
/// <param name="Types">Spellings as the sheet wrote them, in its order. The first is canonical —
/// it is what a cell gets rewritten to once the group is adopted.</param>
public sealed record CategoryTypeRule(
    string Category,
    string CategoryTr,
    IReadOnlyList<string> Types);

/// <summary>The file <c>CategoryRuleStore</c> owns: the RuleSet workbook, parsed once at upload.</summary>
/// <param name="SourceName">The workbook this came out of. The marketplace versions it in the file
/// name ("RuleSet 35 1.xlsx"), so it is the only record of which edition is loaded.</param>
public sealed record CategoryRuleFile(
    int Version,
    string? UpdatedUtc,
    string? SourceName,
    IReadOnlyList<CategoryTypeRule> Rules)
{
    public IReadOnlyList<CategoryTypeRule> RuleList => Rules ?? [];
}

/// <summary>What the editor shows about the loaded RuleSet.</summary>
public sealed record CategoryRuleStatus(
    string? SourceName,
    string? UpdatedUtc,
    int Rules,
    int Categories,
    string Path);

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
    bool AllowSuffix = false,
    bool AllowPartial = false,
    string Units = "",
    string Aliases = "",
    string ReferenceList = "");

/// <summary>One ready-made unit set the editor offers, on its way to the browser.</summary>
/// <param name="Units">Already encoded into the cell format by <c>TitleRuleStore</c>. The browser
/// writes this into the Birimler box verbatim — it never builds the string itself, for the reason
/// on <see cref="TitleAttributeForm"/>.</param>
public sealed record MeasureFamilyDto(string Label, string Units);

public sealed record TitleRuleSetForm(
    string Name,
    string TitleColumn,
    IReadOnlyList<TitleAttributeForm> Attributes,
    string DecimalSeparator = ".",
    bool CollapseRepeats = false)
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
/// What <see cref="TitleFixSuggester"/> may be able to do something about.
///
/// <para>Mostly this is why a row needs a human: <see cref="TitleAttributeStatus.Ambiguous"/> has
/// three separate causes and they need different fixes, so the status alone is not enough to act on
/// — and two of them are indistinguishable from the rest of the result, because both can leave
/// <see cref="TitleAttributeResult.TitleSaid"/> equal to the cell. Recorded here rather than
/// recovered by parsing the Turkish message, which would break the moment the wording changed.</para>
///
/// <para><see cref="SpellingUnknown"/> is the exception: it marks an opportunity rather than a
/// problem, on a row that reported nothing wrong at all.</para>
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

    /// <summary>
    /// The cell's value is not in the title under any spelling the rule knows — "İndüksiyonlu ocak"
    /// against a title reading "İndüksiyon Ocak".
    ///
    /// <para>Not an error, and not reported as one. A cell whose value the title never mentions is
    /// ordinary, so this rides along on every <see cref="TitleAttributeStatus.NotInTitle"/> result
    /// purely so <see cref="TitleFixSuggester"/> gets a look at it; whether there is anything worth
    /// offering is decided there, not here.</para>
    /// </summary>
    SpellingUnknown,
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
/// <param name="TitleSuspect">
/// Set where the title does not look like a product name at all — a row whose many filled cells the
/// title matches exactly one of. A real export carries such rows ("2 YIL LENOVO TÜRKİYE GARANTİLİ -
/// HIZLI KARGO" in a title column), and the one thing they do match is usually the brand, so
/// cleaning them writes a mangled sentence back to the marketplace.
///
/// <para>Carried as its own flag rather than as a verdict on some attribute, because it is a
/// judgement about the <em>title</em> and no single column is at fault.</para>
/// </param>
public sealed record TitleCleanRow(
    int RowNumber,
    string OriginalTitle,
    string CleanTitle,
    IReadOnlyList<TitleAttributeResult> Attributes,
    IReadOnlyList<string> Errors,
    bool TitleSuspect = false)
{
    public bool HasConflict => TitleSuspect || Attributes.Any(a =>
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

    /// <summary>The title names a product type the marketplace's own RuleSet defines: take that
    /// rule's whole group — canonical plus every spelling it accepts — into the column's catalogue.</summary>
    AdoptCategoryType,

    /// <summary>
    /// Turn on whatever settings stand between a column and words it already carries — Çıkar, Ek,
    /// Kısmi, in whatever combination the leftover report found.
    ///
    /// <para>All of them at once, and on purpose. A column can be held back by two switches at the
    /// same time — its removal is off <em>and</em> its value only partly appears — and lifting one of
    /// those changes nothing, so a card offering one alone gets filtered out for having no effect.
    /// The operator would then never be offered either. One card, one column, every switch that
    /// column needs.</para>
    /// </summary>
    EnableMatching,
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
/// <param name="Preselected">Whether the card arrives ticked. Off for a proposal the operator should
/// look at before taking — a product type the RuleSet files under a <em>different</em> category from
/// the one the uploaded file declares. Distinct from <paramref name="NeedsColumnChoice"/>, which also
/// leaves a card unticked but does so because it is incomplete rather than because it is doubtful.</param>
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
    string? Warning = null,
    bool Preselected = true);

/// <summary>Why a word is still standing in the cleaned titles.</summary>
public enum TitleLeftoverCause
{
    /// <summary>No column's cell carries it — a model code, a marketing word, a size nobody mapped.
    /// Ordinary, and the bulk of what a clean run leaves behind.</summary>
    Unclaimed,

    /// <summary>A column carries it and the rule found it, but that rule says do not remove.</summary>
    RemoveOff,

    /// <summary>A column carries it under an inflection its rule is not allowed to follow.</summary>
    NeedsSuffix,

    /// <summary>A column carries it as part of a longer value whose other words the title omits.</summary>
    NeedsPartial,

    /// <summary>A column carries it and none of the above explains the miss — the title spells it some
    /// way the catalogue has no entry for.</summary>
    Unmatched,

    /// <summary>
    /// A column's value is the <em>start</em> of this word — "AMD Ryzen 3" against a title's
    /// "Ryzen3-30" — and what follows it is not in that column's reference list.
    ///
    /// <para>Told apart from <see cref="Unclaimed"/> because the advice is completely different.
    /// "Nothing claims this" sends the operator looking for a column to add; here the column is
    /// already right and the question is whether the catalogue is short an entry or the title is
    /// simply wrong. Only a person can answer that, so the report says which decision it is rather
    /// than making one.</para>
    /// </summary>
    ReferenceMissing,
}

/// <summary>
/// One word still standing in the cleaned titles, and what accounts for it.
///
/// <para>This is the answer to the only question an operator actually asks of a cleaned file: why is
/// <em>that</em> still there. Without it the question can only be answered by reading a rule table
/// against a title by eye, which is how a column with its removal switched off looks exactly like a
/// column that failed to match.</para>
/// </summary>
/// <param name="Column">The column whose own cell carries this word, where there is one.</param>
public sealed record TitleLeftover(
    string Word,
    int Rows,
    string? Column,
    TitleLeftoverCause Cause,
    string Reason,
    string Sample);

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
    IReadOnlyList<TitleFix> Fixes,
    IReadOnlyList<TitleLeftover> Leftovers);
