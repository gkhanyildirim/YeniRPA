using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using YeniRPA.Web.Models;
using YeniRPA.Web.Services;
using YeniRPA.Web.Services.TitleCleaner;

namespace YeniRPA.Web.Controllers;

/// <summary>
/// Title Cleaner. Strips the values an attribute column names out of the product title, and reports
/// where the title and the column disagree.
///
/// <para><b>Nothing here writes to the uploaded file.</b> The preview computes a result and hands it
/// back for review; the download recomputes it and returns a new workbook that keeps an untouched
/// copy of the input on its second sheet. Same shape as the mapping imports elsewhere in this app,
/// and for the same reason: a cleaner rewrites data that is not recoverable afterwards.</para>
///
/// <para>The Excel download <b>re-derives the result server-side from the uploaded file</b> rather
/// than taking rows back from the browser — the same rule as Seller Offer Warnings. The engine is
/// deterministic, so the download is exactly what the preview showed, and nothing a page could have
/// edited in between decides what gets written.</para>
/// </summary>
[ApiController]
[Route("api/title-cleaner")]
public sealed class TitleCleanerController(
    TitleRuleStore store,
    CategoryRuleStore categories,
    TitleReferenceStore references) : ControllerBase
{
    // [FromForm] is not optional on the string parameters below. Under [ApiController] a simple type
    // binds from the route or query string by default — only IFormFile is taken from the multipart
    // body — so an un-attributed `ruleSet` arrives null on every upload and the run is refused for
    // having no rule set, with the browser having plainly sent one.

    /// <summary>Reads a file and proposes a starting rule set for it. Saves nothing.</summary>
    [HttpPost("suggest")]
    public async Task<IActionResult> Suggest(
        IFormFile? file, [FromForm] string? name, CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the product file (.xlsx or .csv)." });

        var table = await ReadTableAsync(file, cancellationToken);
        var suggestion = TitleRuleSuggester.Suggest(table, name);

        return Ok(new TitleSuggestionResponse(
            TitleRuleStore.ToForm(suggestion.RuleSet),
            suggestion.Columns.Select(c => new TitleColumnHintDto(
                c.Column, c.Kind.ToString(), c.Remove, c.Filled, c.Distinct, c.Matched, c.Samples, c.Note)).ToList(),
            suggestion.Notes));
    }

    // ---------------------------------------------------------------------
    // Rule sets
    // ---------------------------------------------------------------------

    [HttpGet("rules")]
    public IActionResult GetRules() => Ok(TitleRuleStore.ToForm(store.Load()));

    // ---------------------------------------------------------------------
    // The marketplace's RuleSet
    // ---------------------------------------------------------------------

    [HttpGet("category-rules")]
    public IActionResult GetCategoryRules() => Ok(categories.Status());

    /// <summary>
    /// Takes the marketplace's RuleSet workbook and keeps what this module can act on: which product
    /// types each category accepts, and under which spellings.
    ///
    /// <para>Parsed here rather than on every preview — the workbook only changes when the
    /// marketplace publishes a new edition, and the file name is the only place its version is
    /// recorded, so it is kept.</para>
    /// </summary>
    [HttpPost("category-rules")]
    public async Task<IActionResult> PutCategoryRules(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the RuleSet workbook (.xlsx)." });

        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);
        var rules = CategoryRuleStore.ReadWorkbook(stream, file.FileName);

        categories.Save(new CategoryRuleFile(1, null, file.FileName, rules));
        return Ok(categories.Status());
    }

    // ---------------------------------------------------------------------
    // Reference lists
    // ---------------------------------------------------------------------

    [HttpGet("reference-lists")]
    public IActionResult GetReferenceLists() => Ok(references.Status());

    /// <summary>
    /// Takes a catalogue workbook and keeps one of its columns as a named list of values.
    ///
    /// <para>Parsed here rather than on every preview, the same as the RuleSet above: a five thousand
    /// row catalogue only changes when a new edition is published, and re-reading it per run would be
    /// work repeated for an answer that does not move.</para>
    ///
    /// <para>The column is asked for rather than guessed. A catalogue workbook carries provenance
    /// notes, family names and source URLs beside the values, and picking the wrong column would load
    /// a list of Wikipedia links as though they were processor names.</para>
    /// </summary>
    [HttpPost("reference-lists")]
    public async Task<IActionResult> PutReferenceList(
        IFormFile? file,
        [FromForm] string? name,
        [FromForm] string? column,
        CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Referans listesi çalışma kitabını (.xlsx) yükleyin." });

        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "Listeye bir ad verin." });

        if (string.IsNullOrWhiteSpace(column))
            return BadRequest(new { error = "Değerlerin hangi kolonda olduğunu yazın." });

        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);
        var values = TitleReferenceStore.ReadWorkbook(stream, column);

        references.Put(new TitleReferenceList(
            name.Trim(), $"{file.FileName} · {column.Trim()}", values));

        return Ok(references.Status());
    }

    /// <summary>
    /// Deletes one list. Unlike a rule set this is derived data and uploading the workbook again
    /// rebuilds it, so nothing is lost — but a rule still naming it will refuse to compile until the
    /// operator clears that box, which is the loud failure rather than the silent one.
    /// </summary>
    [HttpDelete("reference-lists/{name}")]
    public IActionResult DeleteReferenceList(string name)
    {
        var before = references.Status().Count;
        var after = references.Remove(name).ListList.Count;

        if (after == before)
            return BadRequest(new { error = $"'{name}' adında bir referans listesi yok." });

        return Ok(references.Status());
    }

    /// <summary>
    /// The unit families the rule editor offers as ready-made choices, each already encoded in the
    /// cell format.
    ///
    /// <para>Encoded here rather than in the browser for the reason written out on
    /// <see cref="TitleAttributeForm"/>: one implementation of the format, not two. The page writes
    /// what this returns straight into the Birimler box.</para>
    /// </summary>
    [HttpGet("unit-presets")]
    public IActionResult GetUnitPresets() => Ok(
        TitleRuleSuggester.Families
            // Line form, because the browser writes this straight into the editor's box and that box
            // now holds one unit per line.
            .Select(f => new MeasureFamilyDto(f.Label, TitleRuleStore.EncodeUnitLines(f.Units)))
            .ToList());

    [HttpPut("rules")]
    public IActionResult PutRules([FromBody] JsonElement body)
    {
        var file = TitleRuleStore.FromForm(TitleRuleStore.ParseFileForm(body.GetRawText()));
        var lists = references.Load().ListList;

        foreach (var set in file.Sets)
        {
            if (string.IsNullOrWhiteSpace(set.Name))
                return BadRequest(new { error = "Her kural setinin bir adı olmalı." });

            // Compiled now rather than at run time: a set that cannot run is refused while the
            // operator is still looking at it, not on their next upload. The reference lists have to
            // come along, or a rule naming a list that IS loaded would be refused on the way in.
            CompiledRuleSet.Compile(set, lists);
        }

        var duplicate = file.Sets
            .GroupBy(s => FoldedTitle.Fold(s.Name), StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            return BadRequest(new { error = $"'{duplicate.First().Name}' adında birden fazla kural seti var." });

        store.Save(file);
        return Ok(TitleRuleStore.ToForm(store.Load()));
    }

    /// <summary>
    /// Deletes one saved rule set by name and returns what is left.
    ///
    /// <para>Rule sets are the only data in this module that cannot be regenerated — they are what
    /// the category team decided, not something derived from an export. <see cref="TitleRuleStore"/>
    /// keeps a <c>.bak</c> generation of the file, and an exported workbook restores them, but
    /// neither is automatic: the browser confirms before calling this.</para>
    /// </summary>
    [HttpDelete("rules/{name}")]
    public IActionResult DeleteRule(string name)
    {
        var file = store.Load();
        var wanted = FoldedTitle.Fold(name);

        var remaining = file.Sets
            .Where(s => !string.Equals(FoldedTitle.Fold(s.Name), wanted, StringComparison.Ordinal))
            .ToList();

        if (remaining.Count == file.Sets.Count)
            return BadRequest(new { error = $"'{name}' adında kayıtlı bir kural seti yok." });

        store.Save(file with { Sets = remaining });
        return Ok(TitleRuleStore.ToForm(store.Load()));
    }

    [HttpPost("rules/excel")]
    public IActionResult RulesExcel([FromBody] JsonElement body)
    {
        var file = TitleRuleStore.FromForm(TitleRuleStore.ParseFileForm(body.GetRawText()));

        return File(
            TitleRuleStore.BuildWorkbook(file.Sets),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "baslik-kural-setleri.xlsx");
    }

    /// <summary>Reads a rule-set workbook and hands the result back for review. <b>Does not save</b>
    /// — the same shape as the other mapping imports in this app.</summary>
    [HttpPost("rules/import")]
    public async Task<IActionResult> ImportRules(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the rule set file (.xlsx or .csv)." });

        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);
        var sets = TitleRuleStore.ReadWorkbook(stream, file.FileName);
        var lists = references.Load().ListList;

        foreach (var set in sets)
            CompiledRuleSet.Compile(set, lists);

        return Ok(TitleRuleStore.ToForm(new TitleRuleFile(1, null, sets)));
    }

    // ---------------------------------------------------------------------
    // Running
    // ---------------------------------------------------------------------

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(
        IFormFile? file,
        [FromForm] string? ruleSet,
        [FromForm] string? ruleSetName,
        CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the product file (.xlsx or .csv)." });

        var rules = Resolve(ruleSet, ruleSetName);
        var table = await ReadTableAsync(file, cancellationToken);

        return Ok(TitleCleanBuilder.BuildData(
            rules, table, null, categories.Load().RuleList, CategoryRuleStore.FileCategory(table)));
    }

    /// <summary>
    /// Applies the chosen suggested fixes and hands back the updated rule set. <b>Does not save</b> —
    /// the browser puts it in the editor and the operator presses Save, the same as everywhere else
    /// in this app.
    ///
    /// <para>The suggestions are <b>recomputed here</b> from the uploaded file; only the ids come
    /// from the browser. What a fix writes into the rule set is decided server-side, so a page cannot
    /// hand over an edit of its own — the same rule as Seller Offer Warnings. Fix ids are derived
    /// from the scenario rather than its position, which is what makes them mean the same thing on
    /// this second pass.</para>
    /// </summary>
    [HttpPost("fixes/apply")]
    public async Task<IActionResult> ApplyFixes(
        IFormFile? file,
        [FromForm] string? ruleSet,
        [FromForm] string? ruleSetName,
        [FromForm] string? fixIds,
        [FromForm] string? targetColumns,
        CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the product file (.xlsx or .csv)." });

        var chosen = (fixIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (chosen.Count == 0)
            return BadRequest(new { error = "Uygulanacak bir düzeltme seçilmedi." });

        var rules = Resolve(ruleSet, ruleSetName);
        var table = await ReadTableAsync(file, cancellationToken);

        // Recomputed with the same inputs the preview had, RuleSet included. A card is matched by an
        // id derived from its scenario, so leaving the category rules out here would simply lose
        // every category-type card the operator had just ticked.
        var cleaned = TitleCleanBuilder.Clean(rules, table);
        var suggested = TitleFixSuggester.Suggest(rules, cleaned)
            .Concat(TitleFixSuggester.SuggestCategoryTypes(
                rules, cleaned, categories.Load().RuleList, CategoryRuleStore.FileCategory(table)))
            .Concat(TitleFixSuggester.SuggestSettings(
                rules, cleaned, TitleLeftoverReport.Build(rules, cleaned)))
            .ToList();

        // Where the operator had to choose the owning column, or corrected the proposed phrase, that
        // is the one thing the browser legitimately supplies — as a value against a known id, never
        // as an edit to make.
        var overrides = ParseOverrides(targetColumns);
        var resolved = suggested
            .Select(f => overrides.TryGetValue(f.Id, out var choice)
                ? f with { TargetColumn = Target(f, choice.Column), Value = choice.Value ?? f.Value }
                : f)
            .ToList();

        var updated = TitleFixSuggester.Apply(rules.Source, resolved, chosen);

        return Ok(TitleRuleStore.ToForm(updated));
    }

    /// <summary>
    /// The column a chosen fix acts on: what the browser picked, unless that is the column which
    /// reported the problem.
    ///
    /// <para>A protector hands a phrase to some <em>other</em> column and switches that column's
    /// removal off. Pointed back at the reporting column it turns off the very removal the operator
    /// is trying to get working, and leaves behind a catalogue value no cell resolves to — one real
    /// rule set ended up with a bare "Ultra9" under its processor column that way, conflicting on
    /// every row. The picker no longer offers it; this is the half that does not depend on the
    /// browser behaving.</para>
    /// </summary>
    static string Target(TitleFix fix, string? chosen) =>
        string.IsNullOrWhiteSpace(chosen) ||
        string.Equals(chosen, fix.Column, StringComparison.Ordinal)
            ? fix.TargetColumn
            : chosen;

    /// <summary>Reads the per-fix choices as <c>id=column=value</c> lines.</summary>
    static Dictionary<string, (string? Column, string? Value)> ParseOverrides(string? raw)
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        using var document = JsonDocument.Parse(raw);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = entry.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            result[id] = (
                entry.TryGetProperty("column", out var c) ? c.GetString() : null,
                entry.TryGetProperty("value", out var v) ? v.GetString() : null);
        }

        return result;
    }

    [HttpPost("excel")]
    public async Task<IActionResult> Excel(
        IFormFile? file,
        [FromForm] string? ruleSet,
        [FromForm] string? ruleSetName,
        CancellationToken cancellationToken)
    {
        if (file is not { Length: > 0 })
            return BadRequest(new { error = "Please upload the product file (.xlsx or .csv)." });

        var rules = Resolve(ruleSet, ruleSetName);
        var table = await ReadTableAsync(file, cancellationToken);
        var rows = TitleCleanBuilder.Clean(rules, table);

        return File(
            TitleCleanWorkbook.Build(table, rules, rows),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            TitleCleanWorkbook.FileName(rules.Source.Name));
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// The rule set a run uses: the one posted with the upload if there is one, otherwise the saved
    /// set of that name. The posted set wins because the editor's unsaved edits are what the
    /// operator is looking at when they press the button.
    /// </summary>
    CompiledRuleSet Resolve(string? posted, string? name)
    {
        // Every run goes through here — preview, the Excel download and the fix application — so the
        // reference lists are read in one place rather than remembered at three call sites.
        var lists = references.Load().ListList;

        if (!string.IsNullOrWhiteSpace(posted))
            return CompiledRuleSet.Compile(TitleRuleStore.ParseRuleSetForm(posted), lists);

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Bu çalıştırma için bir kural seti seçilmedi.");

        var saved = store.Find(name)
            ?? throw new InvalidOperationException($"'{name}' adında kayıtlı bir kural seti yok.");

        return CompiledRuleSet.Compile(saved, lists);
    }

    static async Task<List<List<string>>> ReadTableAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var stream = await CopyToSeekableStreamAsync(file, cancellationToken);
        return TabularFile.Read(stream, file.FileName);
    }

    /// <summary>ClosedXML needs a seekable stream; the raw request body is not one.</summary>
    static async Task<MemoryStream> CopyToSeekableStreamAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;
        return stream;
    }
}
