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

  const KINDS = [
    ['Text', 'Metin'],
    ['Measure', 'Ölçü'],
    ['Alias', 'Eşanlamlı']
  ];

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
    if (!hint) return '<td class="num">&mdash;</td>';

    const short = hint.matched < Math.ceil(hint.filled * 0.35);
    return '<td class="num" title="' + hint.filled + ' dolu hücrenin ' + hint.matched +
      ' tanesi başlıkta bulundu">' +
      '<span class="badge' + (short ? ' amber' : ' green') + '">' +
      hint.matched + '/' + hint.filled + '</span></td>';
  }

  /**
   * Locks the box that this row's type does not use.
   *
   * Only one of the two ever applies — a measured attribute has units and no catalogue, a catalogue
   * attribute the other way round, and a plain text one neither. Leaving both open, both empty and
   * both showing an example was the whole reason the table could not be read: the examples looked
   * exactly like real values, on every row, including the rows where the box does nothing.
   *
   * A locked box keeps whatever was typed in it. Someone who switches type to look at something and
   * switches back does not lose their work, and collectRuleSet still reads it.
   */
  function applyKindLock(row) {
    const kind = row.querySelector('.tc-kind').value;
    const units = row.querySelector('.tc-units');
    const aliases = row.querySelector('.tc-aliases');

    const lock = (input, active, hint) => {
      input.disabled = !active;
      input.placeholder = active ? hint : '';
      input.closest('td').classList.toggle('is-locked', !active);
    };

    lock(units, kind === 'Measure', 'örn: GB=gb@1 ; TB=tb@1024');
    lock(aliases, kind === 'Alias', 'örn: W11P|Windows 11 Pro');
  }

  function ruleRowHtml(rule) {
    const r = rule || {};
    const checkbox = (cls, on, label) =>
      '<td class="num"><input type="checkbox" class="' + cls + '"' + (on ? ' checked' : '') +
      ' aria-label="' + label + '" /></td>';

    const options = KINDS.map(([value, label]) =>
      '<option value="' + value + '"' + (r.kind === value ? ' selected' : '') + '>' +
      RPA.escapeHtml(label) + '</option>').join('');

    return '<tr>' +
      '<td><input type="text" class="tc-col" value="' + RPA.escapeHtml(r.column || '') +
        '" aria-label="Kolon" /></td>' +
      '<td><select class="tc-kind" aria-label="Tip">' + options + '</select></td>' +
      checkbox('tc-remove', r.remove !== false, 'Çıkar') +
      checkbox('tc-correct', r.correct !== false, 'Düzelt') +
      checkbox('tc-fill', r.fillFromTitle === true, 'Başlıktan doldur') +
      matchCell(r.column) +
      '<td><input type="text" class="tc-units" value="' + RPA.escapeHtml(r.units || '') +
        '" aria-label="Birimler" /></td>' +
      '<td><input type="text" class="tc-aliases" value="' + RPA.escapeHtml(r.aliases || '') +
        '" aria-label="Eşanlamlılar" /></td>' +
      '<td class="num"><button type="button" class="btn btn-ghost btn-sm tc-rule-remove" ' +
        'aria-label="Satırı sil">Sil</button></td>' +
      '</tr>';
  }

  function renderRuleSet(set) {
    const s = set || { name: '', titleColumn: '', decimalSeparator: '.', attributes: [] };

    el('tc-set-name').value = s.name || '';
    el('tc-title-column').value = s.titleColumn || '';
    el('tc-decimal').value = s.decimalSeparator === ',' ? ',' : '.';
    el('tc-rules-body').innerHTML = (s.attributes || []).map(ruleRowHtml).join('');

    Array.from(el('tc-rules-body').querySelectorAll('tr')).forEach(applyKindLock);
    updateRuleCount();
  }

  /** Reads the table back out. A row with no column name is dropped — "Add row" leaves one behind
      whenever someone changes their mind. */
  function collectRuleSet() {
    const attributes = Array.from(el('tc-rules-body').querySelectorAll('tr')).map(row => ({
      column: row.querySelector('.tc-col').value.trim(),
      kind: row.querySelector('.tc-kind').value,
      remove: row.querySelector('.tc-remove').checked,
      correct: row.querySelector('.tc-correct').checked,
      fillFromTitle: row.querySelector('.tc-fill').checked,
      units: row.querySelector('.tc-units').value.trim(),
      aliases: row.querySelector('.tc-aliases').value.trim()
    })).filter(a => a.column);

    return {
      name: el('tc-set-name').value.trim(),
      titleColumn: el('tc-title-column').value.trim(),
      decimalSeparator: el('tc-decimal').value,
      attributes: attributes
    };
  }

  function updateRuleCount() {
    const set = collectRuleSet();
    const removed = set.attributes.filter(a => a.remove).length;

    el('tc-rule-count').textContent = set.attributes.length
      ? set.attributes.length + ' kolon · ' + removed + ' tanesi başlıktan çıkarılacak'
      : 'Henüz kural yok — dosya yükleyip "Dosyadan kural öner" deyin';
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
    { label: 'Tip', render: a => RPA.escapeHtml(a.kind), filter: 'select' },
    { label: 'Çıkar', render: a => a.remove ? '<span class="badge green">Evet</span>' : '<span class="badge">Hayır</span>' },
    { label: 'OK', render: a => RPA.fmtInt(a.ok), numeric: true },
    { label: 'Düzeltildi', render: a => RPA.fmtInt(a.corrected), numeric: true },
    { label: 'Çakışma', render: a => RPA.fmtInt(a.conflict), numeric: true },
    { label: 'Başlıkta yok', render: a => RPA.fmtInt(a.notInTitle), numeric: true },
    { label: 'Boş', render: a => RPA.fmtInt(a.empty), numeric: true }
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
    const columns = collectRuleSet().attributes.map(a => a.column);

    const chooser = fix.needsColumnChoice
      ? '<label class="tc-fix-field">Hangi kolona ait?' +
        '<select class="tc-fix-column"><option value="">— seçin —</option>' +
        columns.map(c => '<option value="' + RPA.escapeHtml(c) + '">' + RPA.escapeHtml(c) + '</option>').join('') +
        '</select></label>'
      : '';

    return '<div class="tc-fix" data-id="' + RPA.escapeHtml(fix.id) + '">' +
      '<label class="tc-fix-pick">' +
        '<input type="checkbox" class="tc-fix-on"' + (fix.needsColumnChoice ? '' : ' checked') + ' />' +
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
      renderRuleSet(await RPA.postJson('/api/title-cleaner/fixes/apply', form));

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
      ['Düzeltilen özellik', RPA.fmtInt(data.correctedValues), 'green'],
      ['Başlıktan doldurulan', RPA.fmtInt(data.filledValues), '']
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

      renderRuleSet(suggestion.ruleSet);
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
      renderRuleSet(sets[0]);

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
      body.insertAdjacentHTML('beforeend', ruleRowHtml(null));
      applyKindLock(body.lastElementChild);
      body.lastElementChild.querySelector('.tc-col').focus();
      updateRuleCount();
    });

    el('tc-rules-body').addEventListener('click', function (event) {
      if (!event.target.classList.contains('tc-rule-remove')) return;
      event.target.closest('tr').remove();
      updateRuleCount();
    });

    el('tc-rules-body').addEventListener('input', updateRuleCount);

    el('tc-rules-body').addEventListener('change', function (event) {
      // Changing the type changes which box that row uses, so the locks follow immediately.
      if (event.target.classList.contains('tc-kind'))
        applyKindLock(event.target.closest('tr'));

      updateRuleCount();
    });

    el('tc-rule-import').addEventListener('click', () => el('tc-rule-file').click());
    el('tc-rule-file').addEventListener('change', function () {
      if (this.files[0]) importRules(this.files[0]);
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
