/* =============================================================================
   Title Cleaner — rule set editor, preview, download.

   Every rule decision happens server-side; this file edits a rule set, posts it
   with the file and renders what came back. Two things about that are
   deliberate:

   1. Units and alias groups cross the wire ALREADY FLATTENED into one string
      each ("GB=gb|gigabayt@1 ; TB=tb@1024"). The encoding lives in
      TitleRuleStore and nowhere else — a second implementation of it here, in a
      second language, would be free to drift from the one the Excel round trip
      uses, and the drift would show up as a rule that silently stopped matching.

   2. The Excel download re-posts the file rather than sending these rows back.
      The engine is deterministic, so the download is what the preview showed,
      and nothing this page could have edited in between decides what is written.

   Nothing here saves anything on its own. The rule set is saved when the
   operator presses Save; the preview writes nothing at all.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const el = id => document.getElementById(id);

  /** RPA.sendJson is POST-only; the rule sets are read with GET and written with PUT. Same local
      helper, and the same reason, as late-orders.js has for its mapping. */
  async function sendJsonMethod(method, url, payload) {
    const init = { method: method };
    if (payload !== undefined && payload !== null) {
      init.headers = { 'Content-Type': 'application/json' };
      init.body = JSON.stringify(payload);
    }

    const response = await fetch(url, init);
    if (!response.ok) {
      const text = await response.text();
      let message = 'Request failed with status ' + response.status + '.';
      try {
        const parsed = JSON.parse(text);
        if (parsed && (parsed.error || parsed.title)) message = parsed.error || parsed.title;
      } catch (ignored) { /* not JSON — keep the status message */ }
      throw new Error(message);
    }

    return response.json();
  }

  /** The rule sets on the server, in the editor's flattened shape. */
  let SETS = [];

  /** The last preview, kept so the Excel button knows which rule set produced it. */
  let DATA = null;

  /**
   * What the last scan saw in each column, keyed by column name.
   *
   * The suggester decides "Çıkar" by running the real engine over the sample, and this is that
   * count. Without it on screen the proposal is unarguable — a column arrives switched off and the
   * operator has no way to tell whether the rule is wrong or the titles simply do not carry the
   * value. Cleared whenever the editor is filled from something other than a scan, so a stale
   * number never sits next to a rule it was not measured against.
   */
  let HINTS = {};

  /**
   * The ready-made unit sets, each already encoded by the server.
   *
   * Empty until the fetch lands, and it may never land — the list is a convenience and the Birimler
   * box works without it, so a failure here leaves the picker empty rather than stopping the editor.
   */
  let UNIT_PRESETS = [];

  /**
   * The loaded reference lists, as {name, sourceName, values}.
   *
   * Same treatment as UNIT_PRESETS and for a sharper reason: a rule naming a list this fetch failed
   * to deliver still runs, because the server compiles against its own copy. What an empty list costs
   * is the picker — the box falls back to showing whatever name the rule already carries, so a set
   * loaded into the editor can still be saved without silently losing its reference.
   */
  let REFERENCE_LISTS = [];

  /**
   * Whether the editor holds anything the server has not been told about.
   *
   * Importing a workbook and suggesting from a file both fill the table without saving — the same
   * review-then-save flow as the other mapping imports in this app. Nothing said so, and a rule set
   * that was only ever loaded into the table looks exactly like one that was saved: the preview
   * honours it, the page reload does not. Saying it out loud is cheaper than changing the flow.
   */
  let DIRTY = false;

  const KINDS = [
    ['Text', 'Metin'],
    ['Measure', 'Ölçü'],
    ['Alias', 'Değer Listesi']
  ];

  /** The same labels keyed by wire value, for tables that render a kind the server sent. */
  const KIND_LABEL = Object.fromEntries(KINDS);

  /**
   * The four per-rule permissions, as [class, the letter the summary line shows, full label].
   *
   * One list rather than four repetitions: the summary badges, the checkboxes in the body and the
   * read-back in collectRuleSet all walk it, so a fifth flag is added in one place and a mismatch
   * between the badge and the box it stands for cannot happen.
   */
  const FLAGS = [
    ['tc-remove', 'Ç', 'Çıkar'],
    ['tc-correct', 'D', 'Düzelt'],
    ['tc-suffix', 'E', 'Ek'],
    ['tc-partial', 'K', 'Kısmi']
  ];

  const FLAG_HINT = {
    'tc-remove': 'Bulunca başlıktan silinsin mi. Kapalıysa metin korunur.',
    'tc-correct': 'Hücredeki yazım standarda çevrilsin mi (16 → 16 GB). Tip = Metin olan satırlarda kullanılmaz.',
    'tc-suffix': 'Başlıktaki "Ocaklar" kelimesi "Ocak" değerini karşılasın mı — Türkçe çekim ekli yazımlar. Model kodu taşıyan kolonlarda kapalı bırakın.',
    'tc-partial': 'Değerin bir parçası tamamının yerine geçsin mi — hücrede "CETINTAS EVII" yazarken başlıkta yalnızca "Çetintaş" geçmesi. Yalnızca marka, malzeme gibi kolonlarda açın; ürün tipinde açmayın.'
  };

  const STATUS_LABEL = {
    Ok: 'OK',
    Corrected: 'DÜZELTİLDİ',
    Conflict: 'ÇAKIŞMA',
    Ambiguous: 'BELİRSİZ',
    NotInTitle: 'BAŞLIKTA YOK',
    Filled: 'DOLDURULDU',
    Empty: 'ÖZELLİK BOŞ'
  };

  // ---------------------------------------------------------------------------
  // The rule editor
  // ---------------------------------------------------------------------------

  /**
   * "4/5" — how many of the sampled rows this rule found in the title, out of the rows whose cell
   * was filled. It is the evidence behind the "Çıkar" box next to it: a low number means either the
   * rule is wrong or the titles genuinely do not carry that value, and those need different fixes.
   */
  function matchCell(column) {
    const hint = column ? HINTS[column] : null;
    if (!hint) return '<span class="tc-rule-match">&mdash;</span>';

    const short = hint.matched < Math.ceil(hint.filled * 0.35);
    return '<span class="tc-rule-match" title="' + hint.filled + ' dolu hücrenin ' + hint.matched +
      ' tanesi başlıkta bulundu">' +
      '<span class="badge' + (short ? ' amber' : ' green') + '">' +
      hint.matched + '/' + hint.filled + '</span></span>';
  }

  /**
   * Locks the boxes that this row's type does not use.
   *
   * Only one of Birimler/Değerler ever applies — a measured attribute has units and no catalogue, a
   * catalogue attribute the other way round, and a plain text one neither. Leaving both open, both
   * empty and both showing an example was the whole reason the table could not be read: the examples
   * looked exactly like real values, on every row, including the rows where the box does nothing.
   *
   * "Düzelt" is locked on a Text row for the same reason. A Text rule searches the title for the
   * cell's own value, so the match it finds is by construction spelled the way the cell already is
   * and the correction branch in TitleCleanBuilder.Judge can never fire. The box was clickable and
   * inert, which reads as a setting that does nothing rather than one that does not apply.
   *
   * A locked box keeps whatever was in it. Someone who switches type to look at something and
   * switches back does not lose their work, and collectRuleSet still reads it — a disabled checkbox
   * still reports its own checked state.
   */
  function applyKindLock(row) {
    const kind = row.querySelector('.tc-kind').value;

    const lock = (input, active, hint) => {
      input.disabled = !active;
      if (input.type !== 'checkbox' && input.tagName !== 'SELECT')
        input.placeholder = active ? hint : '';
      const field = input.closest('.tc-field');
      if (field) field.classList.toggle('is-locked', !active);
    };

    lock(row.querySelector('.tc-units'), kind === 'Measure', 'GB=gb|gigabayt@1\nTB=tb@1024');
    lock(row.querySelector('.tc-unit-preset'), kind === 'Measure');
    lock(row.querySelector('.tc-aliases'), kind === 'Alias', 'W11P|Windows 11 Pro\nW11H|Windows 11 Home');
    lock(row.querySelector('.tc-correct'), kind !== 'Text');

    // A measured value is matched as a number and its unit, so a word ending has nothing to say
    // about it.
    lock(row.querySelector('.tc-suffix'), kind !== 'Measure');
    lock(row.querySelector('.tc-partial'), kind !== 'Measure');

    // Same reason for the reference list: a catalogue of spellings has nowhere to attach to a rule
    // matched by number and unit, and AttributeMatcher.Compile drops one handed to a Measure rule.
    lock(row.querySelector('.tc-reference'), kind !== 'Measure');

    syncRuleHead(row);
  }

  /**
   * Redraws the parts of the summary line that mirror what is in the body — the type, the four
   * flags, and the column name.
   *
   * The summary is what the operator scans forty rules with, so it cannot go stale the moment
   * something below it is edited. A flag reads lit when it is on, faint when it is off and dashed
   * when this row's type does not use it, which is the same three states the boxes themselves have.
   */
  function syncRuleHead(row) {
    const name = row.querySelector('.tc-col').value.trim();
    const title = row.querySelector('.tc-rule-name');

    title.textContent = name || '(kolon adı yok)';
    title.classList.toggle('is-empty', !name);

    row.querySelector('.tc-rule-kind').textContent =
      KIND_LABEL[row.querySelector('.tc-kind').value] || '';

    FLAGS.forEach(([cls, letter, label]) => {
      const box = row.querySelector('.' + cls);
      const dot = row.querySelector('.tc-rule-flag[data-flag="' + cls + '"]');

      dot.textContent = letter;
      dot.classList.toggle('is-on', box.checked && !box.disabled);
      dot.classList.toggle('is-locked', box.disabled);
      dot.title = label + ': ' + (box.disabled ? 'bu tipte kullanılmıyor' : box.checked ? 'açık' : 'kapalı');
    });
  }

  /**
   * Fills one row's ready-made unit picker.
   *
   * The options are written here rather than into the row template because the presets arrive from
   * the server asynchronously and a rule set can be rendered before they land. Every path that puts
   * a row on screen calls this, and so does the fetch when it resolves.
   */
  function fillUnitPresets(row) {
    const select = row.querySelector('.tc-unit-preset');

    // The value IS the encoded cell text — the browser picks one, it never builds one.
    select.innerHTML = '<option value="">— Hazır set —</option>' +
      UNIT_PRESETS.map(p => '<option value="' + RPA.escapeHtml(p.units) + '">' +
        RPA.escapeHtml(p.label) + '</option>').join('');

    syncUnitPreset(row);
  }

  /**
   * Points the picker at whatever the Birimler box currently holds.
   *
   * Matched on the exact string, so trimming a family down to the one unit a column really uses —
   * which is what stops a cache column reporting a false conflict against every "8GB" in a title —
   * drops the picker back to its blank option. That is the honest reading: the cell is no longer a
   * ready-made set.
   */
  function syncUnitPreset(row) {
    const select = row.querySelector('.tc-unit-preset');
    const units = row.querySelector('.tc-units').value.trim();

    select.value = UNIT_PRESETS.some(p => p.units === units) ? units : '';
  }

  /**
   * Fills one row's reference-list picker.
   *
   * Written here rather than into the row template for the same reason as the unit presets: the lists
   * arrive asynchronously and a rule set can be rendered before they land. The row's own value is
   * kept as an option even when no such list is loaded, so rendering a set that names a list nobody
   * has uploaded yet does not quietly clear the box on the next save.
   */
  function fillReferenceLists(row) {
    const select = row.querySelector('.tc-reference');
    const current = select.dataset.value || '';

    const names = REFERENCE_LISTS.map(l => l.name);
    if (current && !names.some(n => n.toLocaleLowerCase('tr') === current.toLocaleLowerCase('tr')))
      names.push(current);

    select.innerHTML = '<option value="">— yok —</option>' +
      names.map(n => '<option value="' + RPA.escapeHtml(n) + '">' +
        RPA.escapeHtml(n) + '</option>').join('');

    select.value = current;
  }

  /** Puts a row on screen: the locks its type implies, its two pickers, and its summary line. */
  function dressRuleRow(row) {
    applyKindLock(row);
    fillUnitPresets(row);
    fillReferenceLists(row);
    autoGrow(row.querySelector('.tc-units'));
    autoGrow(row.querySelector('.tc-aliases'));
  }

  /**
   * Grows a box to fit what is in it, up to a ceiling.
   *
   * A catalogue runs to forty lines and a unit family to three; a fixed height is wrong for both.
   * The ceiling is there so one long column cannot push every other rule off the screen — past it
   * the box scrolls on its own.
   */
  function autoGrow(box) {
    if (!box) return;
    box.style.height = 'auto';
    box.style.height = Math.min(box.scrollHeight + 2, 320) + 'px';
  }

  let RULE_SEQ = 0;

  function ruleItemHtml(rule) {
    const r = rule || {};
    const id = 'tc-rule-' + (++RULE_SEQ);

    const options = KINDS.map(([value, label]) =>
      '<option value="' + value + '"' + (r.kind === value ? ' selected' : '') + '>' +
      RPA.escapeHtml(label) + '</option>').join('');

    const on = { 'tc-remove': r.remove !== false, 'tc-correct': r.correct !== false,
                 'tc-suffix': r.allowSuffix === true, 'tc-partial': r.allowPartial === true };

    const dots = FLAGS.map(([cls]) =>
      '<span class="tc-rule-flag" data-flag="' + cls + '"></span>').join('');

    const checks = FLAGS.map(([cls, , label]) =>
      '<label class="tc-check" title="' + RPA.escapeHtml(FLAG_HINT[cls]) + '">' +
      '<input type="checkbox" class="' + cls + '"' + (on[cls] ? ' checked' : '') + ' />' +
      '<span>' + label + '</span></label>').join('');

    return '' +
      '<div class="tc-rule" data-rule>' +
        // The summary is a button so it opens from the keyboard and announces its state; the row's
        // own actions live in the body, because a button cannot contain another one — and because
        // "Sil" under the cursor while scanning forty rules is an accident waiting to happen.
        '<button type="button" class="tc-rule-head" aria-expanded="false" aria-controls="' + id + '">' +
          '<span class="tc-rule-caret" aria-hidden="true"></span>' +
          '<span class="tc-rule-name"></span>' +
          '<span class="tc-rule-kind"></span>' +
          '<span class="tc-rule-flags" aria-hidden="true">' + dots + '</span>' +
          matchCell(r.column) +
        '</button>' +

        '<div class="tc-rule-body" id="' + id + '" hidden>' +
          '<div class="tc-fields">' +
            '<div class="tc-field">' +
              '<label>Kolon</label>' +
              '<input type="text" class="tc-col" value="' + RPA.escapeHtml(r.column || '') +
                '" placeholder="Excel başlığıyla birebir aynı" />' +
              // Never shown: every attribute column a marketplace export carries is already filled,
              // so the box only ever sat there unticked. The value rides along hidden so saving from
              // this editor does not drop what an imported workbook turned on.
              '<input type="hidden" class="tc-fill" value="' + (r.fillFromTitle === true) + '" />' +
            '</div>' +
            '<div class="tc-field">' +
              '<label>Tip</label>' +
              '<select class="tc-kind">' + options + '</select>' +
            '</div>' +
            '<div class="tc-field tc-field-wide">' +
              '<label>İzinler</label>' +
              '<div class="tc-checks">' + checks + '</div>' +
            '</div>' +

            '<div class="tc-field tc-field-full">' +
              '<label>Birimler</label>' +
              '<select class="tc-unit-preset" aria-label="Hazır birim seti"></select>' +
              '<textarea class="tc-units" rows="1" spellcheck="false">' +
                RPA.escapeHtml(r.units || '') + '</textarea>' +
              '<span class="tc-hint">Her satır bir birim. &quot;=&quot; öncesi standart yazım, ' +
                '&quot;|&quot; kabul edilen diğer yazımlar, &quot;@@&quot; taban birime göre katsayı ' +
                '(1 TB = 1024 GB).</span>' +
            '</div>' +

            '<div class="tc-field tc-field-full">' +
              '<label>Değerler</label>' +
              '<textarea class="tc-aliases" rows="1" spellcheck="false">' +
                RPA.escapeHtml(r.aliases || '') + '</textarea>' +
              '<span class="tc-hint">Her satır bir değer; satırın ilk yazımı standart olan. ' +
                '&quot;|&quot; aynı şeyin diğer yazımlarını ayırır.</span>' +
            '</div>' +

            '<div class="tc-field">' +
              '<label>Referans listesi</label>' +
              '<select class="tc-reference" data-value="' +
                RPA.escapeHtml(r.referenceList || '') + '"></select>' +
              '<span class="tc-hint">Hücrenin yazmadığı uzun yazımlar için katalog.</span>' +
            '</div>' +
          '</div>' +

          '<div class="tc-rule-foot">' +
            // Order decides which of two rules claims a stretch of title that both could match, so
            // it has to be changeable here. Until now the only way was to edit the workbook.
            '<div class="tc-move">' +
              '<button type="button" class="btn btn-ghost btn-sm tc-rule-up" title="Yukarı taşı — sıra, iki kuralın aynı metni istediği durumda hangisinin kazanacağını belirler">&uarr;</button>' +
              '<button type="button" class="btn btn-ghost btn-sm tc-rule-down" title="Aşağı taşı">&darr;</button>' +
            '</div>' +
            '<button type="button" class="btn btn-ghost btn-sm tc-rule-remove">Kuralı sil</button>' +
          '</div>' +
        '</div>' +
      '</div>';
  }

  /** Opens or closes one rule, keeping the button's state and the body's visibility together. */
  function toggleRule(rule, open) {
    const head = rule.querySelector('.tc-rule-head');
    const body = rule.querySelector('.tc-rule-body');
    const next = open === undefined ? head.getAttribute('aria-expanded') !== 'true' : open;

    head.setAttribute('aria-expanded', next ? 'true' : 'false');
    body.hidden = !next;

    // A textarea measures as zero while it is hidden, so its height is only right once it is shown.
    if (next) {
      autoGrow(rule.querySelector('.tc-units'));
      autoGrow(rule.querySelector('.tc-aliases'));
    }
  }

  /** @param dirty true when the set came from a scan or an import — filled in, but not saved. */
  function renderRuleSet(set, dirty) {
    const s = set || { name: '', titleColumn: '', decimalSeparator: '.', attributes: [] };

    el('tc-set-name').value = s.name || '';
    el('tc-title-column').value = s.titleColumn || '';
    el('tc-decimal').value = s.decimalSeparator === ',' ? ',' : '.';
    el('tc-collapse').checked = s.collapseRepeats === true;

    const list = (s.attributes || []);
    el('tc-rules-body').innerHTML = list.length
      ? list.map(ruleItemHtml).join('')
      : '<p class="tc-rules-empty">Henüz kural yok — bir dosya yükleyip ' +
        '&quot;Dosyadan kural öner&quot; deyin, ya da &quot;Satır ekle&quot; ile kendiniz başlayın.</p>';

    ruleRows().forEach(dressRuleRow);
    DIRTY = dirty === true;
    updateRuleCount();
  }

  function ruleRows() {
    return Array.from(el('tc-rules-body').querySelectorAll('[data-rule]'));
  }

  /** Reads the editor back out. A rule with no column name is dropped — "Satır ekle" leaves one
      behind whenever someone changes their mind. */
  function collectRuleSet() {
    const attributes = ruleRows().map(row => ({
      column: row.querySelector('.tc-col').value.trim(),
      kind: row.querySelector('.tc-kind').value,
      remove: row.querySelector('.tc-remove').checked,
      correct: row.querySelector('.tc-correct').checked,
      allowSuffix: row.querySelector('.tc-suffix').checked,
      allowPartial: row.querySelector('.tc-partial').checked,
      fillFromTitle: row.querySelector('.tc-fill').value === 'true',
      units: row.querySelector('.tc-units').value.trim(),
      aliases: row.querySelector('.tc-aliases').value.trim(),
      referenceList: row.querySelector('.tc-reference').value.trim()
    })).filter(a => a.column);

    return {
      name: el('tc-set-name').value.trim(),
      titleColumn: el('tc-title-column').value.trim(),
      decimalSeparator: el('tc-decimal').value,
      collapseRepeats: el('tc-collapse').checked,
      attributes: attributes
    };
  }

  function updateRuleCount() {
    const set = collectRuleSet();
    const removed = set.attributes.filter(a => a.remove).length;

    if (!set.attributes.length) {
      el('tc-rule-count').textContent =
        'Henüz kural yok — dosya yükleyip "Dosyadan kural öner" deyin';
      return;
    }

    // Static text and counts only, so innerHTML carries no value the operator typed.
    el('tc-rule-count').innerHTML =
      set.attributes.length + ' kolon · ' + removed + ' tanesi başlıktan çıkarılacak' +
      (DIRTY ? ' <span class="badge amber">kaydedilmedi</span>' : '');
  }

  /** An edit the server has not seen. Redraws the summary so the badge appears with the change. */
  function markDirty() {
    DIRTY = true;
    updateRuleCount();
  }

  function renderNotes(notes) {
    const box = el('tc-notes');
    if (!notes || !notes.length) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }

    box.hidden = false;
    box.innerHTML = notes.map(n => '<span class="badge amber">' + RPA.escapeHtml(n) + '</span>').join(' ');
  }

  // ---------------------------------------------------------------------------
  // Saved rule sets
  // ---------------------------------------------------------------------------

  function fillSetSelect(selected) {
    const select = el('tc-ruleset');
    const options = ['<option value="">— yeni kural seti —</option>'];

    SETS.forEach(s => {
      options.push('<option value="' + RPA.escapeHtml(s.name) + '"' +
        (s.name === selected ? ' selected' : '') + '>' + RPA.escapeHtml(s.name) + '</option>');
    });

    select.innerHTML = options.join('');
    if (selected) select.value = selected;
  }

  /** Only a set that is actually saved can be deleted; a draft in the editor has nothing to remove. */
  function syncDeleteButton() {
    el('tc-rule-delete').disabled = !el('tc-ruleset').value;
  }

  /**
   * Removes one saved rule set.
   *
   * Confirmed first, and deliberately so: rule sets are the only thing here that cannot be
   * regenerated from a file. The store keeps one backup generation and an exported workbook brings
   * them back, but neither happens by itself.
   */
  async function deleteSet() {
    const name = el('tc-ruleset').value;
    if (!name) return;

    if (!window.confirm(
      '"' + name + '" kural setini silmek istiyor musunuz?\n\n' +
      'Bu set yeniden üretilemez — yalnızca Excel yedeğinden geri yüklenebilir.')) {
      return;
    }

    const button = el('tc-rule-delete');
    RPA.clearError('tc-rule-alert');
    RPA.setBusy(button, true, 'Siliniyor…');
    try {
      const saved = await sendJsonMethod('DELETE', '/api/title-cleaner/rules/' + encodeURIComponent(name));
      SETS = saved.sets || [];

      HINTS = {};
      fillSetSelect('');
      renderRuleSet(null);
      renderNotes(['"' + name + '" silindi.']);
      syncDeleteButton();
    } catch (err) {
      RPA.showError('tc-rule-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  /**
   * Reads the ready-made unit sets and refills every picker on screen.
   *
   * The failure is swallowed on purpose. This list only saves typing — the Birimler box takes the
   * same text either way — so a request that fails must not put an error banner over a rule editor
   * that is working perfectly well.
   */
  async function loadUnitPresets() {
    try {
      UNIT_PRESETS = await sendJsonMethod('GET', '/api/title-cleaner/unit-presets') || [];
    } catch (ignored) {
      return;
    }

    ruleRows().forEach(fillUnitPresets);
  }

  function renderReferenceStatus() {
    const box = el('tc-reference-status');
    if (!box) return;

    box.textContent = REFERENCE_LISTS.length === 0
      ? 'yüklenmedi'
      : REFERENCE_LISTS
        .map(l => l.name + ' (' + RPA.fmtInt(l.values) + ')')
        .join(' · ');
  }

  /** Same quiet failure as the unit presets: the picker goes empty, the editor keeps working. */
  async function loadReferenceLists() {
    try {
      REFERENCE_LISTS = await sendJsonMethod('GET', '/api/title-cleaner/reference-lists') || [];
    } catch (ignored) {
      REFERENCE_LISTS = [];
    }

    renderReferenceStatus();
    ruleRows().forEach(fillReferenceLists);
  }

  /**
   * Both boxes have to be filled before the file picker opens.
   *
   * Checking afterwards was the same work in the wrong order: the operator browsed for a workbook,
   * chose it, and only then learned they needed to type something first. The boxes are also the kind
   * that read as already filled — their examples name the very values this list is usually built
   * from — so the message names the empty one and puts the cursor in it rather than describing both.
   */
  function openReferenceFile() {
    const name = el('tc-reference-name');
    const column = el('tc-reference-column');

    RPA.clearError('tc-rule-alert');

    const missing = !name.value.trim() ? name : !column.value.trim() ? column : null;

    if (missing) {
      RPA.showError('tc-rule-alert', missing === name
        ? 'Listeye bir ad verin — kural tablosundaki Referans kutusunda bu adla görünecek.'
        : 'Değerlerin hangi kolonda olduğunu yazın — çalışma kitabının başlık satırındaki adıyla.');
      missing.focus();
      return;
    }

    el('tc-reference-file').click();
  }

  async function importReferenceList(file) {
    const button = el('tc-reference-import');
    const name = el('tc-reference-name').value.trim();
    const column = el('tc-reference-column').value.trim();

    RPA.clearError('tc-rule-alert');
    RPA.setBusy(button, true, 'Okunuyor…');
    try {
      const form = new FormData();
      form.append('file', file);
      form.append('name', name);
      form.append('column', column);

      REFERENCE_LISTS = await RPA.postJson('/api/title-cleaner/reference-lists', form);
      renderReferenceStatus();
      ruleRows().forEach(fillReferenceLists);

      const loaded = REFERENCE_LISTS.find(l => l.name === name);
      renderNotes([
        'Referans listesi alındı: ' + name + ' · ' +
        RPA.fmtInt(loaded ? loaded.values : 0) + ' değer. ' +
        'Kural tablosunda ilgili kolonun Referans kutusundan seçin.'
      ]);
    } catch (err) {
      RPA.showError('tc-rule-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // The marketplace's RuleSet
  // ---------------------------------------------------------------------------

  function renderRuleSetStatus(status) {
    const box = el('tc-ruleset-status');

    if (!status || !status.rules) {
      box.textContent = 'yüklenmedi — kategori doğrulaması yapılmıyor';
      return;
    }

    box.textContent = (status.sourceName || 'yüklendi') + ' · ' +
      RPA.fmtInt(status.rules) + ' kural · ' +
      RPA.fmtInt(status.categories) + ' kategori' +
      (status.updatedUtc ? ' · ' + status.updatedUtc : '');
  }

  /** The status only decorates the editor, so a failure here says so quietly and changes nothing. */
  async function loadRuleSetStatus() {
    try {
      renderRuleSetStatus(await sendJsonMethod('GET', '/api/title-cleaner/category-rules'));
    } catch (ignored) {
      renderRuleSetStatus(null);
    }
  }

  async function importCategoryRules(file) {
    const button = el('tc-ruleset-import');
    RPA.clearError('tc-rule-alert');
    RPA.setBusy(button, true, 'Okunuyor…');
    try {
      const form = new FormData();
      form.append('file', file);

      const status = await RPA.postJson('/api/title-cleaner/category-rules', form);
      renderRuleSetStatus(status);
      renderNotes([
        'RuleSet alındı: ' + RPA.fmtInt(status.rules) + ' kural, ' +
        RPA.fmtInt(status.categories) + ' kategori. Önizlemeyi tekrar alın.'
      ]);
    } catch (err) {
      RPA.showError('tc-rule-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  async function loadSets(selected) {
    try {
      const file = await sendJsonMethod('GET', '/api/title-cleaner/rules');
      SETS = file.sets || [];
      fillSetSelect(selected);
      syncDeleteButton();
    } catch (err) {
      RPA.showError('tc-rule-alert', err.message);
    }
  }

  // ---------------------------------------------------------------------------
  // Results
  // ---------------------------------------------------------------------------

  const statusBadge = status => {
    const label = STATUS_LABEL[status] || status;
    if (status === 'Conflict' || status === 'Ambiguous') return '<span class="badge red">' + label + '</span>';
    if (status === 'Corrected' || status === 'Filled') return '<span class="badge green">' + label + '</span>';
    if (status === 'NotInTitle') return '<span class="badge amber">' + label + '</span>';
    return '<span class="badge">' + label + '</span>';
  };

  const rowBadge = row => {
    if (row.hasConflict) return '<span class="badge red">İncelenecek</span>';
    if (row.changed) return '<span class="badge green">Temizlendi</span>';
    return '<span class="badge">Dokunulmadı</span>';
  };

  const rowColumns = [
    { label: 'Satır', render: r => RPA.fmtInt(r.rowNumber), numeric: true },
    { label: 'Başlık', render: r => RPA.escapeHtml(r.originalTitle) },
    { label: 'Temiz Başlık', render: r => RPA.escapeHtml(r.cleanTitle) },
    { label: 'Durum', render: r => rowBadge(r), filter: 'select' },
    { label: 'Hatalar', render: r => RPA.escapeHtml((r.errors || []).join(' | ')) }
  ];

  const attrColumns = [
    { label: 'Kolon', render: a => RPA.escapeHtml(a.column) },
    { label: 'Tip', render: a => RPA.escapeHtml(KIND_LABEL[a.kind] || a.kind), filter: 'select' },
    { label: 'Çıkar', render: a => a.remove ? '<span class="badge green">Evet</span>' : '<span class="badge">Hayır</span>' },
    { label: 'OK', render: a => RPA.fmtInt(a.ok), numeric: true },
    { label: 'Düzeltildi', render: a => RPA.fmtInt(a.corrected), numeric: true },
    { label: 'Çakışma', render: a => RPA.fmtInt(a.conflict), numeric: true },
    { label: 'Başlıkta yok', render: a => RPA.fmtInt(a.notInTitle), numeric: true },
    { label: 'Boş', render: a => RPA.fmtInt(a.empty), numeric: true }
  ];

  /**
   * What is still standing in the cleaned titles.
   *
   * "Talep eden yok" is the ordinary case — a model code, a marketing word — so it is the quiet
   * badge. Everything else is a setting standing between a column and a word it already carries,
   * and those come first in the list precisely because they can be acted on.
   */
  const LEFTOVER_BADGE = {
    Unclaimed: '',
    RemoveOff: 'amber',
    NeedsSuffix: 'amber',
    NeedsPartial: 'amber',
    Unmatched: 'amber'
  };

  const leftoverColumns = [
    { label: 'Kelime', render: l => RPA.escapeHtml(l.word) },
    { label: 'Satır', render: l => RPA.fmtInt(l.rows), numeric: true },
    { label: 'Kolon', render: l => RPA.escapeHtml(l.column || '—'), filter: 'select' },
    {
      label: 'Neden',
      render: l => '<span class="badge ' + (LEFTOVER_BADGE[l.cause] || '') + '">' +
        RPA.escapeHtml(l.reason) + '</span>',
      filter: 'select'
    },
    { label: 'Örnek başlık', render: l => RPA.escapeHtml(l.sample) }
  ];

  // ---------------------------------------------------------------------------
  // Suggested fixes
  // ---------------------------------------------------------------------------

  /**
   * One scenario out of the review list, with the rule change that resolves it.
   *
   * The value box is editable on purpose: the phrase the server proposes is read out of a sample
   * title and can reach a word too far. Correcting it here beats finding the rule row by hand.
   */
  function fixCardHtml(fix) {
    // Never the column that reported the problem. A protector switches the chosen column's removal
    // off, so pointing it back here would disable the very rule the operator is trying to fix — the
    // server refuses it too, this only keeps it out of sight.
    const columns = collectRuleSet().attributes
      .map(a => a.column)
      .filter(c => c !== fix.column);

    const chooser = fix.needsColumnChoice
      ? '<label class="tc-fix-field">Hangi kolona ait?' +
        '<select class="tc-fix-column"><option value="">— seçin —</option>' +
        columns.map(c => '<option value="' + RPA.escapeHtml(c) + '">' + RPA.escapeHtml(c) + '</option>').join('') +
        '</select></label>'
      : '';

    return '<div class="tc-fix" data-id="' + RPA.escapeHtml(fix.id) + '">' +
      '<label class="tc-fix-pick">' +
        // Unticked when the card is incomplete (a column still to choose) or doubtful (the RuleSet
        // files this type under a different category than the file declares).
        '<input type="checkbox" class="tc-fix-on"' +
          (fix.needsColumnChoice || fix.preselected === false ? '' : ' checked') + ' />' +
        '<span class="badge amber">' + RPA.fmtInt(fix.rows) + ' satır</span>' +
      '</label>' +
      '<div class="tc-fix-body">' +
        '<div class="tc-fix-problem">' + RPA.escapeHtml(fix.problem) + '</div>' +
        '<div class="tc-fix-action">' + RPA.escapeHtml(fix.action) + '</div>' +
        '<div class="tc-fix-fields">' +
          chooser +
          '<label class="tc-fix-field">Yazılacak değer' +
            '<input type="text" class="tc-fix-value" value="' + RPA.escapeHtml(fix.value) + '" />' +
          '</label>' +
        '</div>' +
        (fix.sampleAfter && fix.sampleAfter !== fix.sampleBefore
          ? '<div class="tc-fix-diff">' +
              '<div><span class="tc-fix-tag">Önce</span> ' + RPA.escapeHtml(fix.sampleBefore) + '</div>' +
              '<div><span class="tc-fix-tag is-after">Sonra</span> ' + RPA.escapeHtml(fix.sampleAfter) + '</div>' +
            '</div>'
          : '') +
        (fix.warning ? '<div class="tc-fix-warn">' + RPA.escapeHtml(fix.warning) + '</div>' : '') +
      '</div>' +
      '</div>';
  }

  function renderFixes(fixes) {
    const list = fixes || [];
    const has = list.length > 0;

    el('tc-fixes-title').hidden = !has;
    el('tc-fixes-card').hidden = !has;

    if (!has) {
      el('tc-fixes-list').innerHTML = '';
      return;
    }

    el('tc-fixes-list').innerHTML = list.map(fixCardHtml).join('');
    updateFixCount();
  }

  function updateFixCount() {
    const cards = Array.from(document.querySelectorAll('#tc-fixes-list .tc-fix'));
    const picked = cards.filter(c => c.querySelector('.tc-fix-on').checked);
    const rows = picked.reduce((sum, c) => {
      const badge = c.querySelector('.badge');
      return sum + (parseInt(String(badge.textContent).replace(/\D/g, ''), 10) || 0);
    }, 0);

    el('tc-fixes-count').textContent = picked.length
      ? picked.length + ' düzeltme seçili · ' + RPA.fmtInt(rows) + ' satırı etkiler'
      : cards.length + ' öneri var, hiçbiri seçili değil';
  }

  /** What the operator changed on the cards: a column they chose, a phrase they corrected. */
  function collectFixChoices() {
    return Array.from(document.querySelectorAll('#tc-fixes-list .tc-fix')).map(card => {
      const column = card.querySelector('.tc-fix-column');
      return {
        id: card.dataset.id,
        column: column ? column.value : null,
        value: card.querySelector('.tc-fix-value').value.trim()
      };
    });
  }

  async function applyFixes() {
    const file = el('tc-file').files[0];
    if (!file) {
      RPA.showError('tc-alert', 'Ürün dosyası artık seçili değil — tekrar yükleyin.');
      return;
    }

    const cards = Array.from(document.querySelectorAll('#tc-fixes-list .tc-fix'));
    const picked = cards.filter(c => c.querySelector('.tc-fix-on').checked);

    if (!picked.length) {
      RPA.showError('tc-alert', 'Uygulanacak düzeltmeleri işaretleyin.');
      return;
    }

    const missing = picked.find(c => {
      const column = c.querySelector('.tc-fix-column');
      return column && !column.value;
    });

    if (missing) {
      RPA.showError('tc-alert', 'Seçtiğiniz bir düzeltme için önce kolon belirtmeniz gerekiyor.');
      return;
    }
    RPA.clearError('tc-alert');

    const form = new FormData();
    form.append('file', file);
    form.append('ruleSet', JSON.stringify(collectRuleSet()));
    form.append('fixIds', picked.map(c => c.dataset.id).join(','));
    form.append('targetColumns', JSON.stringify(collectFixChoices()));

    const button = el('tc-fixes-apply');
    RPA.setBusy(button, true, 'Uygulanıyor…');
    try {
      // The server decides what the rule set becomes; the browser only says which scenarios.
      HINTS = {};
      renderRuleSet(await RPA.postJson('/api/title-cleaner/fixes/apply', form), true);

      // Straight back into a preview, so the effect of the choice is on screen immediately.
      render(await RPA.postJson('/api/title-cleaner/preview', runForm(file)));
    } catch (err) {
      RPA.showError('tc-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------

  function render(data) {
    DATA = data;
    renderFixes(data.fixes);

    RPA.renderKpis('tc-kpis', [
      ['Satır', RPA.fmtInt(data.rows), ''],
      ['Başlığı temizlenen', RPA.fmtInt(data.changed), 'green'],
      ['Dokunulmayan', RPA.fmtInt(data.untouched), ''],
      ['İncelenecek satır', RPA.fmtInt(data.conflictRows), data.conflictRows ? 'red' : 'green'],
      ['Düzeltilen özellik', RPA.fmtInt(data.correctedValues), 'green']
    ]);

    const truncated = data.rows > data.previewLimit
      ? ' Tabloda ilk ' + RPA.fmtInt(data.previewLimit) + ' satır gösteriliyor — Excel çıktısı ' +
        'dosyanın tamamını içerir.'
      : '';

    el('tc-overview-note').textContent =
      '"' + (data.ruleSet ? data.ruleSet.name : '') + '" kural setiyle çalıştırıldı. ' +
      'Hiçbir şey kaydedilmedi: bu bir önizleme, dosyanız olduğu gibi duruyor.' + truncated;

    RPA.renderDataTable('tc-attr-wrap', data.attributes || [], attrColumns,
      'Kural setinde kolon yok.');

    RPA.renderDataTable('tc-conflict-wrap', data.conflicting || [], rowColumns,
      'Hiçbir satırda başlık ile özellik çelişmiyor.');

    RPA.renderDataTable('tc-leftovers-wrap', data.leftovers || [], leftoverColumns,
      'Başlıklarda kelime kalmadı.');

    RPA.renderDataTable('tc-rows-wrap', data.preview || [], rowColumns,
      'Gösterilecek satır yok.');

    RPA.stamp('tc-stamp');
    el('tc-results').hidden = false;
    RPA.revealResults('tc-results');
    RPA.syncExportButtons();
  }

  // ---------------------------------------------------------------------------
  // Actions
  // ---------------------------------------------------------------------------

  function chosenFile() {
    const file = el('tc-file').files[0];
    if (!file) {
      RPA.showError('tc-alert', 'Lütfen ürün dosyasını yükleyin.');
      return null;
    }
    RPA.clearError('tc-alert');
    return file;
  }

  /** The posted rule set wins over the saved one: unsaved edits are what the operator is looking at. */
  function runForm(file) {
    const form = new FormData();
    form.append('file', file);
    form.append('ruleSet', JSON.stringify(collectRuleSet()));
    return form;
  }

  async function suggest() {
    const file = chosenFile();
    if (!file) return;

    const button = el('tc-suggest');
    const form = new FormData();
    form.append('file', file);

    const name = el('tc-set-name').value.trim();
    if (name) form.append('name', name);

    RPA.setBusy(button, true, 'Okunuyor…');
    try {
      const suggestion = await RPA.postJson('/api/title-cleaner/suggest', form);

      HINTS = {};
      (suggestion.columns || []).forEach(c => { HINTS[c.column] = c; });

      renderRuleSet(suggestion.ruleSet, true);
      renderNotes(suggestion.notes);
      el('tc-ruleset').value = '';
    } catch (err) {
      RPA.showError('tc-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  async function preview() {
    const file = chosenFile();
    if (!file) return;

    const set = collectRuleSet();
    if (!set.titleColumn) {
      RPA.showError('tc-alert', 'Başlık kolonunun adını yazın (ya da "Dosyadan kural öner" deyin).');
      return;
    }
    if (!set.attributes.length) {
      RPA.showError('tc-alert', 'Kural setinde hiç kolon yok — "Dosyadan kural öner" ile başlayın.');
      return;
    }

    const button = el('tc-preview');
    RPA.setBusy(button, true, 'Temizleniyor…');
    RPA.showSkeleton('tc-skeleton', 'tc-results');
    RPA.resetDataTables();
    try {
      render(await RPA.postJson('/api/title-cleaner/preview', runForm(file)));
    } catch (err) {
      RPA.showError('tc-alert', err.message);
    } finally {
      RPA.hideSkeleton('tc-skeleton');
      RPA.setBusy(button, false);
    }
  }

  async function download() {
    const file = el('tc-file').files[0];
    if (!file) {
      RPA.showError('tc-alert', 'Ürün dosyası artık seçili değil — tekrar yükleyin.');
      return;
    }

    const button = el('tc-excel');
    RPA.setBusy(button, true, 'Hazırlanıyor…');
    try {
      await RPA.postDownload('/api/title-cleaner/excel', runForm(file), 'Temizlenmis Basliklar.xlsx');
    } catch (err) {
      RPA.showError('tc-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  /** Saves the edited set alongside the others, replacing the one of the same name. */
  async function saveRules() {
    const set = collectRuleSet();

    if (!set.name) {
      RPA.showError('tc-rule-alert', 'Kural setine bir ad verin.');
      return;
    }
    if (!set.titleColumn) {
      RPA.showError('tc-rule-alert', 'Başlık kolonunun adını yazın.');
      return;
    }
    RPA.clearError('tc-rule-alert');

    const others = SETS.filter(s => s.name.toLocaleLowerCase('tr') !== set.name.toLocaleLowerCase('tr'));
    const button = el('tc-rule-save');

    RPA.setBusy(button, true, 'Kaydediliyor…');
    try {
      const saved = await sendJsonMethod('PUT', '/api/title-cleaner/rules',
        { version: 1, sets: others.concat([set]) });

      SETS = saved.sets || [];
      fillSetSelect(set.name);
      syncDeleteButton();

      DIRTY = false;
      updateRuleCount();
    } catch (err) {
      RPA.showError('tc-rule-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  async function importRules(file) {
    const button = el('tc-rule-import');
    const form = new FormData();
    form.append('file', file);

    RPA.setBusy(button, true, 'Okunuyor…');
    try {
      const imported = await RPA.postJson('/api/title-cleaner/rules/import', form);
      const sets = imported.sets || [];

      if (!sets.length) {
        RPA.showError('tc-rule-alert', 'Dosyada kural seti bulunamadı.');
        return;
      }

      // All of them, not just the first. A team running many categories keeps every rule set in one
      // workbook, and importing them one at a time — which is what this used to do — means one round
      // trip per category to get them back in.
      //
      // Handed back for review and NOT saved, the same as the other imports in this app: they land
      // in the working list and the dropdown, and Save writes the lot.
      HINTS = {};
      SETS = sets;
      fillSetSelect(sets[0].name);
      renderRuleSet(sets[0], true);

      renderNotes([
        sets.length === 1
          ? '"' + sets[0].name + '" içe aktarıldı — henüz kaydedilmedi.'
          : sets.length + ' kural seti içe aktarıldı (' + sets.map(s => s.name).join(', ') +
            ') — henüz kaydedilmedi. "Kural setini kaydet" hepsini birden yazar.'
      ]);
    } catch (err) {
      RPA.showError('tc-rule-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('tc-drop', 'tc-file');
    renderRuleSet(null);
    loadSets('');
    loadUnitPresets();
    loadRuleSetStatus();
    loadReferenceLists();

    el('tc-suggest').addEventListener('click', suggest);
    el('tc-preview').addEventListener('click', preview);
    el('tc-excel').addEventListener('click', download);
    el('tc-rule-save').addEventListener('click', saveRules);
    el('tc-fixes-apply').addEventListener('click', applyFixes);

    el('tc-fixes-list').addEventListener('change', function (event) {
      // Choosing a column is what makes that card actionable, so it ticks itself.
      if (event.target.classList.contains('tc-fix-column') && event.target.value)
        event.target.closest('.tc-fix').querySelector('.tc-fix-on').checked = true;

      updateFixCount();
    });

    el('tc-ruleset').addEventListener('change', function () {
      const chosen = SETS.find(s => s.name === this.value);

      // The counts belong to the file that was scanned, not to this rule set. Keeping them here
      // would put a number next to a rule it was never measured against.
      HINTS = {};

      renderRuleSet(chosen || null);
      renderNotes(null);
      syncDeleteButton();
      RPA.clearError('tc-rule-alert');
    });

    el('tc-rule-delete').addEventListener('click', deleteSet);

    el('tc-rule-add').addEventListener('click', function () {
      const body = el('tc-rules-body');

      // The empty-state paragraph is not a rule; the first added row replaces it.
      if (!body.querySelector('[data-rule]')) body.innerHTML = '';

      body.insertAdjacentHTML('beforeend', ruleItemHtml(null));

      const added = body.lastElementChild;
      dressRuleRow(added);

      // Opened, because a new rule has nothing in its summary line to act on — the whole point of
      // adding one is to fill it in.
      toggleRule(added, true);
      added.querySelector('.tc-col').focus();
      added.scrollIntoView({ block: 'nearest' });
      markDirty();
    });

    el('tc-rules-body').addEventListener('click', function (event) {
      const rule = event.target.closest('[data-rule]');
      if (!rule) return;

      if (event.target.closest('.tc-rule-head')) {
        toggleRule(rule);
        return;
      }

      if (event.target.closest('.tc-rule-remove')) {
        rule.remove();
        if (!ruleRows().length) renderRuleSet(collectRuleSet(), true);
        markDirty();
        return;
      }

      // Order decides which of two rules claims a stretch of title both could match, so moving one
      // is a real edit rather than a view preference.
      const up = event.target.closest('.tc-rule-up');
      const down = event.target.closest('.tc-rule-down');
      if (!up && !down) return;

      const sibling = up ? rule.previousElementSibling : rule.nextElementSibling;
      if (!sibling) return;

      if (up) rule.parentNode.insertBefore(rule, sibling);
      else rule.parentNode.insertBefore(sibling, rule);

      // The moved rule keeps the focus so a second press moves it again without hunting for it.
      (up ? rule.querySelector('.tc-rule-up') : rule.querySelector('.tc-rule-down')).focus();
      rule.scrollIntoView({ block: 'nearest' });
      markDirty();
    });

    el('tc-rules-body').addEventListener('input', function (event) {
      const rule = event.target.closest('[data-rule]');
      if (!rule) return;

      // Typing over a ready-made set means it is no longer that set, so the picker lets go of it.
      if (event.target.classList.contains('tc-units')) syncUnitPreset(rule);

      if (event.target.classList.contains('tc-units') ||
          event.target.classList.contains('tc-aliases')) {
        autoGrow(event.target);
      }

      // The summary line carries the column name, so it follows what is typed rather than waiting
      // for the row to be closed and reopened.
      if (event.target.classList.contains('tc-col')) syncRuleHead(rule);

      markDirty();
    });

    el('tc-rules-body').addEventListener('change', function (event) {
      const rule = event.target.closest('[data-rule]');
      if (!rule) return;

      // Changing the type changes which box that row uses, so the locks follow immediately.
      if (event.target.classList.contains('tc-kind')) applyKindLock(rule);

      // The picker writes the server's own encoding into the visible box rather than replacing it.
      // The box is where the operator trims the family down to the units their column really uses.
      if (event.target.classList.contains('tc-unit-preset') && event.target.value) {
        const units = rule.querySelector('.tc-units');
        units.value = event.target.value;
        autoGrow(units);
      }

      // The picker is rebuilt whenever the loaded lists change, and it rebuilds itself from
      // data-value — so a choice that only lived in select.value would be lost the moment someone
      // uploaded another list.
      if (event.target.classList.contains('tc-reference'))
        event.target.dataset.value = event.target.value;

      if (event.target.type === 'checkbox') syncRuleHead(rule);

      markDirty();
    });

    // The three fields outside the table are part of the rule set too — a renamed set or a changed
    // title column is just as unsaved as an edited row.
    ['tc-set-name', 'tc-title-column'].forEach(id => el(id).addEventListener('input', markDirty));
    el('tc-decimal').addEventListener('change', markDirty);
    el('tc-collapse').addEventListener('change', markDirty);

    el('tc-rule-import').addEventListener('click', () => el('tc-rule-file').click());
    el('tc-rule-file').addEventListener('change', function () {
      if (this.files[0]) importRules(this.files[0]);
      this.value = '';
    });

    el('tc-ruleset-import').addEventListener('click', () => el('tc-ruleset-file').click());
    el('tc-ruleset-file').addEventListener('change', function () {
      if (this.files[0]) importCategoryRules(this.files[0]);
      this.value = '';
    });

    el('tc-reference-import').addEventListener('click', openReferenceFile);
    el('tc-reference-file').addEventListener('change', function () {
      if (this.files[0]) importReferenceList(this.files[0]);
      this.value = '';
    });

    el('tc-rule-export').addEventListener('click', async function () {
      const set = collectRuleSet();
      const others = SETS.filter(s => s.name.toLocaleLowerCase('tr') !== set.name.toLocaleLowerCase('tr'));
      const sets = set.name && set.attributes.length ? others.concat([set]) : SETS;

      RPA.setBusy(this, true, 'Hazırlanıyor…');
      try {
        await RPA.postDownloadJson('/api/title-cleaner/rules/excel',
          { version: 1, sets: sets }, 'baslik-kural-setleri.xlsx');
      } catch (err) {
        RPA.showError('tc-rule-alert', err.message);
      } finally {
        RPA.setBusy(this, false);
      }
    });
  });

})(window.RPA);
