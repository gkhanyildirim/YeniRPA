/* =============================================================================
   Order Report — Sales Analysis view ("Satış Analizi").

   Explains *why* sales, average price and order count moved between two
   periods. Unlike the Late Shipment view next to it, nothing is aggregated in
   the browser: the orders export is uploaded once to /api/sales-analysis/load,
   held server-side, and every filter or tab asks /api/sales-analysis/analyze
   for the aggregated sections it needs. No raw order line reaches this file.

   The page is Turkish and tr-TR formatted (1.234,56 ₺, dd.mm.yyyy) — the one
   explicit exception to the English-UI rule, requested by the operator.

   Layout: KPIs → Reasons → PVM waterfall + outlier effect → detail tabs. The
   first four arrive with "Uygula"; each detail tab is requested the first time
   it is opened for the current filter (lazy), then kept until the filter moves.
   ============================================================================= */

(function (RPA) {
  'use strict';

  // ---------------------------------------------------------------------------
  // tr-TR formatting. A missing or non-finite figure is always "—", never 0/NaN/∞.
  // ---------------------------------------------------------------------------

  const NF0 = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 });
  const NF1 = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const NF2 = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const PCT1 = new Intl.NumberFormat('tr-TR', { style: 'percent', minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const DASH = '—';

  const ok = v => v !== null && v !== undefined && typeof v === 'number' && isFinite(v);
  const esc = v => RPA.escapeHtml(v === null || v === undefined ? '' : String(v));
  const money = v => (ok(v) ? NF2.format(v) + ' ₺' : DASH);
  const money0 = v => (ok(v) ? NF0.format(v) + ' ₺' : DASH);
  const count = v => (ok(v) ? NF0.format(v) : DASH);
  const qty = v => (ok(v) ? (Number.isInteger(v) ? NF0.format(v) : NF1.format(v)) : DASH);
  const pct = v => (ok(v) ? PCT1.format(v) : DASH);
  const sign = v => (v > 0 ? '+' : v < 0 ? '−' : '');
  const signedMoney = v => (ok(v) ? sign(v) + money(Math.abs(v)) : DASH);
  const signedMoney0 = v => (ok(v) ? sign(v) + money0(Math.abs(v)) : DASH);
  const signedPct = v => (ok(v) ? sign(v) + PCT1.format(Math.abs(v)) : DASH);
  /** A rate move, given as a ratio difference (0.019), shown in percentage points. */
  const points = v => (ok(v) ? sign(v) + NF1.format(Math.abs(v * 100)) + ' puan' : DASH);
  const dateTr = iso => (iso ? iso.slice(8, 10) + '.' + iso.slice(5, 7) + '.' + iso.slice(0, 4) : '');
  const raw = v => (ok(v) ? v : '');

  const arrowOf = v => (!ok(v) || v === 0 ? '→' : v > 0 ? '▲' : '▼');

  /**
   * A change with an arrow *and* a sign, so the direction never rests on colour alone. `goodWhen`
   * ('up' | 'down' | null) decides the tone; null leaves it neutral.
   */
  function trend(v, text, goodWhen) {
    if (!ok(v)) return '<span class="sa-trend">' + DASH + '</span>';
    const dir = v > 0 ? 'up' : v < 0 ? 'down' : 'flat';
    const tone = (!goodWhen || dir === 'flat') ? '' : ((dir === 'up') === (goodWhen === 'up') ? ' is-good' : ' is-bad');
    return '<span class="sa-trend is-' + dir + tone + '"><span aria-hidden="true">' + arrowOf(v) + '</span> ' +
      esc(text) + '</span>';
  }

  // ISO day arithmetic in UTC, so the browser's own time zone can never move a date.
  const dayMs = 86400000;
  const toUtc = iso => Date.UTC(+iso.slice(0, 4), +iso.slice(5, 7) - 1, +iso.slice(8, 10));
  const fromUtc = ms => new Date(ms).toISOString().slice(0, 10);
  const addDays = (iso, n) => fromUtc(toUtc(iso) + n * dayMs);
  const daysBetween = (from, to) => Math.round((toUtc(to) - toUtc(from)) / dayMs) + 1;

  // ---------------------------------------------------------------------------
  // State
  // ---------------------------------------------------------------------------

  const DETAIL_TABS = ['category', 'product', 'brand', 'seller', 'status', 'time', 'city', 'profit'];
  const MAIN_SECTIONS = ['kpi', 'reasons', 'pvm', 'outlier'];

  let LOAD = null;        // the load result: token, defaults, filter options, import report
  let FILE_ID = '';       // which file LOAD belongs to
  let REQUEST = null;     // the last applied request, without `sections`
  let MAIN = null;        // the last main response
  let TAB = 'category';
  let tabCache = {};      // tab → section, for the current REQUEST
  const charts = {};
  const csvSpecs = new Map();
  const ms = {};          // the four multi-selects

  // ---------------------------------------------------------------------------
  // Requests — the new endpoints answer { success, message, data }.
  // ---------------------------------------------------------------------------

  async function call(url, body) {
    const response = body instanceof FormData ? await RPA.postJson(url, body) : await RPA.sendJson(url, body);
    if (!response || response.success === false) throw new Error((response && response.message) || 'İstek başarısız oldu.');
    return response.data;
  }

  const fileId = f => [f.name, f.size, f.lastModified].join('|');

  function currentFile() {
    const input = document.getElementById('order-file');
    return input && input.files && input.files[0];
  }

  // ---------------------------------------------------------------------------
  // Multi-select with search
  // ---------------------------------------------------------------------------

  /**
   * A searchable checkbox list in a popover. `emptyMeansAll` is for the dimension filters (nothing
   * ticked = no filter); the status filter is an explicit inclusion list instead.
   */
  function multiSelect(rootId, options, selected, emptyMeansAll) {
    const root = document.getElementById(rootId);
    const label = root.dataset.label || '';
    const chosen = new Set(selected || []);

    root.innerHTML =
      '<button type="button" class="ms-toggle" aria-haspopup="true" aria-expanded="false">' +
        '<span class="ms-text"></span>' +
        '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M6 9l6 6 6-6"/></svg>' +
      '</button>' +
      '<div class="ms-pop" hidden>' +
        '<input type="search" class="ms-search" placeholder="Ara…" aria-label="' + esc(label) + ' içinde ara" />' +
        '<div class="ms-bulk">' +
          '<button type="button" class="btn btn-ghost btn-sm" data-ms="all">Görünenleri seç</button>' +
          '<button type="button" class="btn btn-ghost btn-sm" data-ms="none">Temizle</button>' +
        '</div>' +
        '<div class="ms-list" role="group" aria-label="' + esc(label) + '">' +
          options.map(o =>
            '<label class="ms-opt" data-fold="' + esc(RPA.fold(o.label + ' ' + o.value)) + '">' +
              '<input type="checkbox" value="' + esc(o.value) + '"' + (chosen.has(o.value) ? ' checked' : '') + ' />' +
              '<span class="ms-name">' + esc(o.label) + '</span>' +
              '<span class="ms-n">' + count(o.lines) + '</span>' +
            '</label>').join('') +
        '</div>' +
      '</div>';

    const toggle = root.querySelector('.ms-toggle');
    const pop = root.querySelector('.ms-pop');
    const search = root.querySelector('.ms-search');
    const boxes = () => Array.from(root.querySelectorAll('.ms-list input[type=checkbox]'));
    const labelOf = new Map(options.map(o => [o.value, o.label]));

    function text() {
      const checked = boxes().filter(b => b.checked);
      let t;
      if (!checked.length) t = emptyMeansAll ? 'Tümü' : 'Hiçbiri';
      else if (checked.length === options.length) t = 'Tümü';
      else if (checked.length === 1) t = labelOf.get(checked[0].value) || checked[0].value;
      else t = checked.length + ' seçili';
      root.querySelector('.ms-text').textContent = t;
      root.classList.toggle('is-filtered', checked.length > 0 && checked.length < options.length);
    }

    function open(state) {
      pop.hidden = !state;
      toggle.setAttribute('aria-expanded', String(state));
      if (state) { search.value = ''; filter(); search.focus(); }
    }

    function filter() {
      const q = RPA.fold(search.value.trim());
      root.querySelectorAll('.ms-opt').forEach(opt => {
        opt.hidden = q && opt.dataset.fold.indexOf(q) === -1;
      });
    }

    toggle.addEventListener('click', () => open(pop.hidden));
    search.addEventListener('input', filter);
    root.addEventListener('change', text);
    root.addEventListener('click', e => {
      const bulk = e.target.closest('[data-ms]');
      if (!bulk) return;
      const all = bulk.dataset.ms === 'all';
      root.querySelectorAll('.ms-opt').forEach(opt => {
        if (all && opt.hidden) return;
        opt.querySelector('input').checked = all;
      });
      text();
    });
    root.addEventListener('keydown', e => {
      if (e.key === 'Escape') { open(false); toggle.focus(); }
    });
    document.addEventListener('click', e => {
      if (!pop.hidden && !root.contains(e.target)) open(false);
    });

    text();

    return {
      /** The ticked values; [] means "no filter" for a dimension, and "all ticked" is sent as [] too. */
      values() {
        const checked = boxes().filter(b => b.checked).map(b => b.value);
        return (emptyMeansAll && checked.length === options.length) ? [] : checked;
      },
      reset(values) {
        const set = new Set(values || []);
        boxes().forEach(b => { b.checked = set.has(b.value); });
        text();
      },
      summary() { return root.querySelector('.ms-text').textContent; }
    };
  }

  // ---------------------------------------------------------------------------
  // Charts, tables and CSV
  // ---------------------------------------------------------------------------

  function chart(id, config) {
    if (charts[id]) { charts[id].destroy(); charts[id] = null; }
    const el = document.getElementById(id);
    if (el) charts[id] = new Chart(el, config);
  }

  function destroyTabCharts() {
    Object.keys(charts).forEach(id => {
      if (id.indexOf('sa-tab-') === 0 && charts[id]) { charts[id].destroy(); charts[id] = null; }
    });
  }

  const scratch = document.createElement('div');
  function plain(html) { scratch.innerHTML = html; return scratch.textContent.replace(/\s+/g, ' ').trim(); }

  /** A sortable, searchable table that also feeds the CSV button next to it. */
  function table(wrapId, rows, columns, emptyMessage, options) {
    RPA.renderDataTable(wrapId, rows, columns, emptyMessage, options);
    csvSpecs.set(wrapId, rows.length ? { columns, rows } : null);
    syncCsv();
  }

  function syncCsv() {
    document.querySelectorAll('[data-csv]').forEach(button => {
      button.disabled = !csvSpecs.get(button.dataset.csv);
    });
    RPA.syncExportButtons();
  }

  /** Semicolon-separated with a BOM and decimal commas — the shape Turkish Excel opens directly. */
  function downloadCsv(key, title, prefix) {
    const spec = csvSpecs.get(key);
    if (!spec) return;
    const cell = v => {
      let s = typeof v === 'number' ? String(v).replace('.', ',') : String(v === null || v === undefined ? '' : v);
      if (/[";\n\r]/.test(s)) s = '"' + s.replace(/"/g, '""') + '"';
      return s;
    };
    const lines = [spec.columns.map(c => cell(plain(c.label))).join(';')].concat(
      spec.rows.map(r => spec.columns.map(c => cell(c.value ? c.value(r) : plain(c.render(r)))).join(';')));
    const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = (prefix || 'Satış Analizi') + ' - ' + (title || key) + '.csv';
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  }

  // Column builders. `value` carries the raw number so sorting, Excel and CSV get numbers, not text.
  const col = {
    text: (label, get, filter) => ({ label, render: r => esc(get(r)), value: r => get(r) || '', filter: filter ? 'text' : undefined }),
    money: (label, get) => ({ label, numeric: true, render: r => money(get(r)), value: r => raw(get(r)) }),
    count: (label, get) => ({ label, numeric: true, render: r => count(get(r)), value: r => raw(get(r)) }),
    qty: (label, get) => ({ label, numeric: true, render: r => qty(get(r)), value: r => raw(get(r)) }),
    pct: (label, get) => ({ label, numeric: true, render: r => pct(get(r)), value: r => (ok(get(r)) ? +(get(r) * 100).toFixed(2) : '') }),
    delta: (label, get, goodWhen) => ({ label, numeric: true, render: r => trend(get(r), signedMoney(get(r)), goodWhen === undefined ? 'up' : goodWhen), value: r => raw(get(r)) }),
    deltaPct: (label, get, goodWhen) => ({ label, numeric: true, render: r => trend(get(r), signedPct(get(r)), goodWhen === undefined ? 'up' : goodWhen), value: r => (ok(get(r)) ? +(get(r) * 100).toFixed(2) : '') }),
    points: (label, get, goodWhen) => ({ label, numeric: true, render: r => trend(get(r), points(get(r)), goodWhen), value: r => (ok(get(r)) ? +(get(r) * 100).toFixed(2) : '') })
  };

  function exportButtons(key, title, prefix) {
    const p = prefix || 'Satış Analizi';
    return '<div class="card-actions">' +
      '<button type="button" class="btn btn-ghost btn-export" data-export="' + esc(key) + '" data-export-title="' + esc(title) +
        '" data-export-prefix="' + esc(p) + '" title="' + esc(title) + ' — Excel" aria-label="' + esc(title) + ' Excel olarak indir" disabled>' +
        '<span class="spinner" aria-hidden="true"></span><span class="btn-text">Excel</span></button>' +
      '<button type="button" class="btn btn-ghost btn-export" data-csv="' + esc(key) + '" data-csv-title="' + esc(title) +
        '" data-csv-prefix="' + esc(p) + '" aria-label="' + esc(title) + ' CSV olarak indir" disabled>CSV</button>' +
      '</div>';
  }

  function tableCard(key, title, sub, prefix) {
    return '<div class="card">' + exportButtons(key, title, prefix) + '<h3>' + esc(title) + '</h3>' +
      (sub ? '<p class="sub">' + esc(sub) + '</p>' : '') + '<div class="table-scroll" id="' + key + '"></div></div>';
  }

  function chartCard(id, title, sub, height) {
    return '<div class="card"><h3>' + esc(title) + '</h3>' + (sub ? '<p class="sub">' + esc(sub) + '</p>' : '') +
      '<div class="chart-box" style="height:' + (height || 300) + 'px"><canvas id="' + id + '" role="img" aria-label="' +
      esc(title) + '"></canvas></div></div>';
  }

  function summaryList(items) {
    if (!items || !items.length) return '';
    return '<ul class="sales-summary">' + items.map(s => '<li>' + esc(s) + '</li>').join('') + '</ul>';
  }

  /** Horizontal bars of a signed change, green up / red down — with the sign in every label too. */
  function moversChart(id, rows) {
    const p = RPA.palette();
    chart(id, {
      type: 'bar',
      data: {
        labels: rows.map(r => r.label),
        datasets: [{
          data: rows.map(r => r.delta),
          backgroundColor: rows.map(r => (r.delta >= 0 ? p.markGood : p.markCritical)),
          borderRadius: 4,
          maxBarThickness: 22
        }]
      },
      options: {
        indexAxis: 'y', maintainAspectRatio: false, responsive: true,
        plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => signedMoney(c.parsed.x) } } },
        scales: { x: { ticks: { callback: v => NF0.format(v) } }, y: { ticks: { autoSkip: false } } }
      }
    });
  }

  // ---------------------------------------------------------------------------
  // Filters and periods
  // ---------------------------------------------------------------------------

  const val = id => document.getElementById(id).value;

  function syncCompareFields() {
    const custom = val('sales-compare') === 'custom';
    document.getElementById('sales-a-from-field').hidden = !custom;
    document.getElementById('sales-a-to-field').hidden = !custom;
  }

  function resetFilters() {
    document.getElementById('sales-b-from').value = LOAD.defaultB.from;
    document.getElementById('sales-b-to').value = LOAD.defaultB.to;
    document.getElementById('sales-a-from').value = LOAD.defaultA.from;
    document.getElementById('sales-a-to').value = LOAD.defaultA.to;
    document.getElementById('sales-compare').value = 'previous';
    document.getElementById('sales-outlier').value = 'none';
    document.getElementById('sales-other').value = '1';
    syncCompareFields();
    ms.status.reset(LOAD.defaultStatuses);
    ms.category.reset([]);
    ms.brand.reset([]);
    ms.seller.reset([]);
  }

  function initFilters() {
    ['sales-b-from', 'sales-b-to', 'sales-a-from', 'sales-a-to'].forEach(id => {
      const input = document.getElementById(id);
      input.min = LOAD.minDate;
      input.max = LOAD.maxDate;
    });
    ms.status = multiSelect('sales-ms-status', LOAD.statuses, LOAD.defaultStatuses, false);
    ms.category = multiSelect('sales-ms-category', LOAD.categories, [], true);
    ms.brand = multiSelect('sales-ms-brand', LOAD.brands, [], true);
    ms.seller = multiSelect('sales-ms-seller', LOAD.sellers, [], true);
    resetFilters();
  }

  /** Both periods from the inputs. B is the chosen range; A is the equal-length window before it unless custom. */
  function periods() {
    const bFrom = val('sales-b-from');
    const bTo = val('sales-b-to');
    if (!bFrom || !bTo) throw new Error('Dönem B için başlangıç ve bitiş tarihi seçin.');
    if (bTo < bFrom) throw new Error('Dönem B: bitiş tarihi başlangıçtan önce olamaz.');

    if (val('sales-compare') === 'custom') {
      const aFrom = val('sales-a-from');
      const aTo = val('sales-a-to');
      if (!aFrom || !aTo) throw new Error('Özel karşılaştırma için Dönem A tarihlerini seçin.');
      if (aTo < aFrom) throw new Error('Dönem A: bitiş tarihi başlangıçtan önce olamaz.');
      return { a: { from: aFrom, to: aTo }, b: { from: bFrom, to: bTo } };
    }

    const days = daysBetween(bFrom, bTo);
    return { a: { from: addDays(bFrom, -days), to: addDays(bFrom, -1) }, b: { from: bFrom, to: bTo } };
  }

  function buildRequest() {
    const p = periods();
    const statuses = ms.status.values();
    if (!statuses.length) throw new Error('Satış sayılacak en az bir statü seçin.');
    const other = parseFloat(String(val('sales-other')).replace(',', '.'));
    return {
      token: LOAD.token,
      periodA: p.a,
      periodB: p.b,
      statuses,
      categories: ms.category.values(),
      brands: ms.brand.values(),
      sellers: ms.seller.values(),
      outlierMode: val('sales-outlier'),
      otherThresholdPct: isFinite(other) ? other : 1
    };
  }

  // ---------------------------------------------------------------------------
  // Main sections
  // ---------------------------------------------------------------------------

  function renderHeader(data) {
    const a = data.periodA;
    const b = data.periodB;
    document.getElementById('sales-period-note').innerHTML =
      '<span><strong>Karşılaştırma dönemi (A):</strong> ' + esc(a.from) + ' – ' + esc(a.to) + ' · ' + a.days + ' gün · ' +
      count(a.orders) + ' sipariş &nbsp;|&nbsp; <strong>İncelenen dönem (B):</strong> ' + esc(b.from) + ' – ' + esc(b.to) + ' · ' +
      b.days + ' gün · ' + count(b.orders) + ' sipariş</span>';

    document.getElementById('sales-warnings').innerHTML = (data.warnings || [])
      .map(w => '<p class="note sales-warn" role="status">' + esc(w) + '</p>').join('');

    const parts = ['Statü: ' + ms.status.summary(), 'Kategori: ' + ms.category.summary(),
      'Marka: ' + ms.brand.summary(), 'Satıcı: ' + ms.seller.summary()];
    if (data.exclusion) parts.push('Çıkarılan büyük sipariş: ' + data.exclusion.orders);
    document.getElementById('sales-filter-summary').textContent =
      a.from + '–' + a.to + ' ↔ ' + b.from + '–' + b.to + ' · ' + parts.join(' · ');

    RPA.setExportContext('Satış Analizi · Dönem A ' + a.from + '–' + a.to + ' · Dönem B ' + b.from + '–' + b.to +
      ' · ' + parts.join(' · '));
  }

  function renderImportNote() {
    const note = document.getElementById('sales-import-note');
    const r = LOAD.report;
    const bits = [count(r.rowsRead) + ' satır okundu, ' + count(r.rowsUsed) + ' satır analizde'];
    (r.issues || []).forEach(i => {
      bits.push(i.message + ' (' + count(i.count) +
        (i.sampleRows && i.sampleRows.length ? '; örn. satır ' + i.sampleRows.join(', ') : '') + ')');
    });
    if (r.missingOptionalColumns && r.missingOptionalColumns.length) {
      bits.push('Eksik opsiyonel kolon: ' + r.missingOptionalColumns.join(', '));
    }
    note.innerHTML = '<span><strong>İçe aktarma:</strong> ' + bits.map(esc).join(' · ') + '</span>';
    note.hidden = false;
  }

  function renderKpis(section) {
    const fmt = kind => (kind === 'money' ? money : kind === 'rate' ? pct : count);
    const tone = (v, goodWhen) => (!goodWhen || !ok(v) || v === 0 ? '' : ((v > 0) === (goodWhen === 'up') ? 'green' : 'red'));

    const items = section.data.map(k => {
      const c = k.change;
      const f = fmt(k.kind);
      let delta;
      if (k.kind === 'rate') {
        delta = { text: ok(c.abs) ? arrowOf(c.abs) + ' ' + points(c.abs) : DASH, tone: tone(c.abs, k.goodWhen), context: '' };
      } else {
        delta = {
          text: ok(c.pct) ? arrowOf(c.pct) + ' ' + signedPct(c.pct) : DASH,
          tone: tone(c.pct, k.goodWhen),
          context: ok(c.abs) ? 'fark ' + sign(c.abs) + f(Math.abs(c.abs)) : ''
        };
      }
      return [k.label, f(c.b), '', 'Dönem A: ' + f(c.a), delta];
    });

    RPA.renderKpis('sales-kpis', items, {
      exportColumns: [{ label: 'Gösterge' }, { label: 'Dönem A', numeric: true }, { label: 'Dönem B', numeric: true },
        { label: 'Fark', numeric: true }, { label: '% Değişim', numeric: true }],
      exportRows: section.data.map(k => [k.label, raw(k.change.a), raw(k.change.b), raw(k.change.abs),
        ok(k.change.pct) ? +(k.change.pct * 100).toFixed(2) : ''])
    });
  }

  function renderReasons(section) {
    const d = section.data;
    const el = document.getElementById('sales-reasons');
    const list = d.reasons.length
      ? '<ol class="reasons-list">' + d.reasons.map(r =>
          '<li class="' + (r.impact > 0 ? 'is-up' : 'is-down') + '">' +
            '<span class="reason-mark" aria-hidden="true">' + arrowOf(r.impact) + '</span>' +
            '<span class="reason-text">' + esc(r.text) + '</span>' +
            '<span class="reason-impact" title="Satışa etkisi">' + signedMoney0(r.impact) + '</span>' +
          '</li>').join('') + '</ol>'
      : '<p class="sub">Öne çıkan bir sebep bulunamadı; iki dönem birbirine çok yakın.</p>';
    el.innerHTML = '<p class="sales-headline">' + esc(d.headline) + '</p>' + list +
      '<p class="table-note">Sebepler, satışa etkisi en büyük olandan başlayarak sıralanır. Sağdaki tutar, o sebebin satışlara yaptığı yaklaşık etkidir.</p>';
  }

  function renderPvm(section) {
    const d = section.data;
    const p = RPA.palette();
    document.getElementById('sales-pvm-summary').innerHTML = (section.summary || []).map(s => '<li>' + esc(s) + '</li>').join('');

    const steps = [
      { label: 'Dönem A', value: d.salesA, total: true },
      { label: 'Satılan adet', value: d.volume },
      { label: 'Fiyat', value: d.price },
      { label: 'Ürün dağılımı', value: d.mix },
      { label: 'Dönem B', value: d.salesB, total: true }
    ];
    let running = d.salesA;
    const bars = steps.map(s => {
      if (s.total) return [0, s.value];
      const bar = [running, running + s.value];
      running += s.value;
      return bar;
    });

    // The effects are small next to the totals, so the axis starts near the lowest point of the
    // walk instead of at zero — otherwise the three bars that matter are a few pixels tall.
    const walk = bars.slice(1, 4).flat().concat([d.salesA, d.salesB]);
    const lo = Math.min.apply(null, walk);
    const hi = Math.max.apply(null, walk);
    const pad = (hi - lo) * 0.6 || hi * 0.05 || 1;

    chart('salesPvmChart', {
      type: 'bar',
      data: {
        labels: steps.map(s => s.label),
        datasets: [{
          data: bars,
          backgroundColor: steps.map(s => (s.total ? p.series[0] : s.value >= 0 ? p.markGood : p.markCritical)),
          borderRadius: 4,
          maxBarThickness: 64
        }]
      },
      options: {
        maintainAspectRatio: false, responsive: true,
        plugins: {
          legend: { display: false },
          tooltip: { callbacks: { label: c => (steps[c.dataIndex].total ? money(steps[c.dataIndex].value) : signedMoney(steps[c.dataIndex].value)) } }
        },
        scales: { y: { min: Math.max(0, lo - pad), ticks: { callback: v => NF0.format(v) } } }
      }
    });

    table('sales-pvm-wrap', d.topItems, [
      col.text('Ürün', r => r.label, true),
      col.text('SKU', r => r.key),
      col.delta('Toplam fark', r => r.delta),
      col.delta('Adet etkisi', r => r.volume, null),
      col.delta('Fiyat etkisi', r => r.price, null),
      col.delta('Ürün dağılımı etkisi', r => r.mix, null)
    ], 'Karşılaştırılacak ürün yok.');
  }

  function renderOutlier(section, exclusion) {
    const d = section.data;
    const lines = (section.summary || []).slice();
    if (exclusion) {
      lines.unshift(exclusion.orders + ' çok büyük sipariş (' + money0(exclusion.amount) + ') filtre gereği raporun tamamından çıkarıldı' +
        (ok(exclusion.threshold) ? '; sınır ' + money0(exclusion.threshold) + '.' : '.'));
    }
    document.getElementById('sales-outlier-summary').innerHTML = lines.map(s => '<li>' + esc(s) + '</li>').join('');

    table('sales-outlier-wrap', d.scenarios, [
      col.text('Çıkarılan siparişler', r => r.label),
      col.count('A\'dan çıkarılan', r => r.ordersA),
      col.count('B\'den çıkarılan', r => r.ordersB),
      col.delta('Bunlar olmadan fark', r => r.deltaWithout),
      col.delta('Bu siparişlerin etkisi', r => r.effect, null),
      col.pct('Farktaki payı', r => r.shareOfDelta)
    ], 'Sipariş yok.');

    table('sales-outlier-orders-wrap', d.topOrdersB, [
      col.text('Sipariş', r => r.orderNumber),
      col.text('Tarih', r => r.date),
      { label: 'Ürün', render: r => '<span class="sa-clip" title="' + esc(r.title) + '">' + esc(r.title) + '</span>' +
          '<span class="sa-meta">' + esc(r.seller) + ' · ' + esc(r.category) + '</span>', value: r => r.title },
      col.money('Tutar', r => r.amount)
    ], 'Dönem B\'de sipariş yok.');
  }

  // ---------------------------------------------------------------------------
  // Detail tabs (lazy)
  // ---------------------------------------------------------------------------

  const panel = () => document.getElementById('sales-tab-panel');

  const DIMENSION = {
    category: { title: 'Kategori', plural: 'kategori', locative: 'kategoride' },
    brand: { title: 'Marka', plural: 'marka', locative: 'markada' },
    seller: { title: 'Satıcı', plural: 'satıcı', locative: 'satıcıda' },
    city: { title: 'Şehir', plural: 'şehir', locative: 'şehirde' }
  };

  function dimensionColumns(name) {
    return [
      col.text(name, r => r.label, true),
      col.money('Dönem A', r => r.salesA),
      col.money('Dönem B', r => r.salesB),
      col.delta('Fark', r => r.delta),
      col.deltaPct('% Değişim', r => r.pct),
      col.pct('Farka katkı', r => r.contribution),
      col.pct('Pay A', r => r.shareA),
      col.pct('Pay B', r => r.shareB),
      col.qty('Adet A', r => r.qtyA),
      col.qty('Adet B', r => r.qtyB),
      col.money('Ort. fiyat A', r => r.priceA),
      col.money('Ort. fiyat B', r => r.priceB)
    ];
  }

  function renderDimension(tab, section) {
    const d = section.data;
    const info = DIMENSION[tab];
    const isCity = tab === 'city';
    let concentration = '';
    if (d.concentration && ok(d.concentration.shareA) && ok(d.concentration.shareB)) {
      const c = d.concentration;
      // The shares themselves are in the server's summary sentence above; this names who they are.
      concentration = '<p class="note"><span><strong>Dönem B\'de en çok satış yapan 5 ' + info.plural + ':</strong> ' +
        esc(c.topB.join(', ')) + '.</span></p>';
    }

    const movers = d.gainers.concat(d.losers.slice().reverse());
    const top10 = d.rows.filter(r => r.key !== '__other__').slice(0, 10);

    panel().innerHTML = summaryList(section.summary) + concentration +
      (isCity
        ? chartCard('sa-tab-chart', 'İlk 10 şehir — Dönem A ve B', 'Dönem B satışına göre sıralı', 360)
        : chartCard('sa-tab-chart', 'En çok artan ve azalan 10 ' + info.plural, 'Satış farkı (₺)', Math.max(240, movers.length * 26 + 40))) +
      (isCity ? '' : '<div class="grid-2">' +
        tableCard('sa-tab-gainers', 'En çok artan 10 ' + info.plural) +
        tableCard('sa-tab-losers', 'En çok azalan 10 ' + info.plural) + '</div>') +
      tableCard('sa-tab-all', 'Tüm ' + info.plural + ' (' + count(d.distinctCount) + ')',
        tab === 'category' ? 'Payı eşiğin altındaki kategoriler "Diğer" altında toplanır' : '');

    if (isCity) {
      const p = RPA.palette();
      chart('sa-tab-chart', {
        type: 'bar',
        data: {
          labels: top10.map(r => r.label),
          datasets: [
            { label: 'Dönem A', data: top10.map(r => r.salesA), backgroundColor: RPA.alpha(p.series[0], 0.45), borderRadius: 4 },
            { label: 'Dönem B', data: top10.map(r => r.salesB), backgroundColor: p.series[0], borderRadius: 4 }
          ]
        },
        options: {
          maintainAspectRatio: false, responsive: true,
          plugins: { tooltip: { callbacks: { label: c => c.dataset.label + ': ' + money0(c.parsed.y) } } },
          scales: { y: { ticks: { callback: v => NF0.format(v) } } }
        }
      });
    } else {
      moversChart('sa-tab-chart', movers);
      const small = [col.text(info.title, r => r.label), col.delta('Fark', r => r.delta), col.deltaPct('%', r => r.pct), col.pct('Katkı', r => r.contribution)];
      table('sa-tab-gainers', d.gainers, small, 'Artan yok.');
      table('sa-tab-losers', d.losers, small, 'Azalan yok.');
    }

    table('sa-tab-all', d.rows, dimensionColumns(info.title), 'Bu filtrede satış yok.', { maxRows: 500 });
  }

  const PRODUCT_LISTS = [
    ['topBySales', 'En çok satan (ciro, Dönem B)'],
    ['topByUnits', 'En çok satan (adet, Dönem B)'],
    ['gainers', 'En çok artan'],
    ['losers', 'En çok azalan'],
    ['newProducts', 'Sadece Dönem B\'de satılanlar'],
    ['lostProducts', 'Sadece Dönem A\'da satılanlar'],
    ['priceChanged', 'Fiyatı değişen ürünler']
  ];

  function renderProduct(section) {
    const d = section.data;
    const current = (document.getElementById('sa-product-list') || {}).value || 'topBySales';
    panel().innerHTML = summaryList(section.summary) +
      '<p class="note"><span>İki dönemde toplam ' + count(d.distinctProducts) + ' farklı ürün satıldı. ' +
      'Sadece Dönem B\'de satılan ' + count(d.newTotal.count) + ' ürün ' + money0(d.newTotal.sales) + ' satış yaptı; ' +
      'sadece Dönem A\'da satılan ' + count(d.lostTotal.count) + ' ürün ise ' + money0(d.lostTotal.sales) + ' satış yapmıştı.</span></p>' +
      '<div class="sales-inline-bar"><div class="field"><label for="sa-product-list">Liste</label>' +
      '<select id="sa-product-list">' + PRODUCT_LISTS.map(([k, l]) =>
        '<option value="' + k + '"' + (k === current ? ' selected' : '') + '>' + esc(l) + '</option>').join('') +
      '</select></div></div>' +
      tableCard('sa-tab-products', 'Ürünler', 'Ürünler Product SKU ile ayırt edilir');

    const columns = [
      col.text('Ürün', r => r.title, true),
      col.text('SKU', r => r.sku, true),
      col.text('Marka', r => r.brand, true),
      col.text('Kategori', r => r.category, true),
      col.qty('Adet A', r => r.qtyA),
      col.qty('Adet B', r => r.qtyB),
      col.money('Satış A', r => r.salesA),
      col.money('Satış B', r => r.salesB),
      col.delta('Fark', r => r.delta),
      col.money('Ort. fiyat A', r => r.priceA),
      col.money('Ort. fiyat B', r => r.priceB),
      col.deltaPct('Fiyat değişimi', r => r.pricePct, null)
    ];
    const draw = key => table('sa-tab-products', d[key] || [], columns, 'Bu listede ürün yok.');
    draw(current);
    document.getElementById('sa-product-list').addEventListener('change', e => draw(e.target.value));
  }

  function renderStatus(section) {
    const d = section.data;
    const p = RPA.palette();
    panel().innerHTML = summaryList(section.summary) +
      chartCard('sa-tab-chart', 'Statü dağılımı', 'Satır sayısı, Dönem A ve B', 300) +
      tableCard('sa-tab-status', 'Statüler', 'Son sütun, o statünün filtrede satışa sayılıp sayılmadığını gösterir') +
      '<div class="grid-2">' +
      tableCard('sa-tab-cancel-cat', 'İptal oranı — kategori', 'Dönem B\'de en az 5 satırı olanlar; en çok artan önce') +
      tableCard('sa-tab-cancel-seller', 'İptal oranı — satıcı', 'Dönem B\'de en az 5 satırı olanlar; en çok artan önce') +
      '</div>' +
      tableCard('sa-tab-cancel-req', 'İptal talebi durumu (Cancellation Request Status)');

    chart('sa-tab-chart', {
      type: 'bar',
      data: {
        labels: d.statuses.map(s => s.label),
        datasets: [
          { label: 'Dönem A', data: d.statuses.map(s => s.linesA), backgroundColor: RPA.alpha(p.series[0], 0.45), borderRadius: 4 },
          { label: 'Dönem B', data: d.statuses.map(s => s.linesB), backgroundColor: p.series[0], borderRadius: 4 }
        ]
      },
      options: { maintainAspectRatio: false, responsive: true }
    });

    table('sa-tab-status', d.statuses, [
      col.text('Statü', r => r.label),
      { label: 'Satışa sayılıyor mu?', render: r => (r.inSales ? '✓ Evet' : '– Hayır'), value: r => (r.inSales ? 'Evet' : 'Hayır') },
      col.count('Satır A', r => r.linesA),
      col.count('Satır B', r => r.linesB),
      col.pct('Pay A', r => r.shareA),
      col.pct('Pay B', r => r.shareB),
      col.money('Tutar A', r => r.amountA),
      col.money('Tutar B', r => r.amountB)
    ], 'Satır yok.');

    const cancelColumns = name => [
      col.text(name, r => r.label, true),
      col.count('Satır B', r => r.linesB),
      col.count('İptal A', r => r.canceledA),
      col.count('İptal B', r => r.canceledB),
      col.pct('Oran A', r => r.rateA),
      col.pct('Oran B', r => r.rateB),
      { label: 'Değişim', numeric: true, render: r => trend(r.changePp, ok(r.changePp) ? sign(r.changePp) + NF1.format(Math.abs(r.changePp)) + ' puan' : DASH, 'down'), value: r => raw(r.changePp) }
    ];
    table('sa-tab-cancel-cat', d.byCategory, cancelColumns('Kategori'), 'İptal yok.');
    table('sa-tab-cancel-seller', d.bySeller, cancelColumns('Satıcı'), 'İptal yok.');
    table('sa-tab-cancel-req', d.cancellationRequests, [
      col.text('Durum', r => r.label), col.count('Dönem A', r => r.a), col.count('Dönem B', r => r.b)
    ], 'İptal talebi yok.');
  }

  const WEEKDAYS = ['Pzt', 'Sal', 'Çar', 'Per', 'Cum', 'Cmt', 'Paz'];

  function heatGrid(grid) {
    const max = Math.max.apply(null, grid.flat().concat([0]));
    let html = '<table class="sales-heat"><thead><tr><th scope="col">Gün</th>';
    for (let h = 0; h < 24; h++) html += '<th scope="col">' + (h < 10 ? '0' : '') + h + '</th>';
    html += '</tr></thead><tbody>';
    grid.forEach((row, d) => {
      html += '<tr><th scope="row">' + WEEKDAYS[d] + '</th>';
      row.forEach((v, h) => {
        const level = max > 0 ? v / max : 0;
        html += '<td style="--heat:' + level.toFixed(3) + '" title="' + WEEKDAYS[d] + ' ' + h + ':00 — ' + money0(v) +
          '" aria-label="' + WEEKDAYS[d] + ' saat ' + h + ': ' + money0(v) + '"></td>';
      });
      html += '</tr>';
    });
    return html + '</tbody></table>';
  }

  function renderTime(section) {
    const d = section.data;
    const p = RPA.palette();
    const hourly = d.granularity === 'hour';
    const extremes = (label, peak, low) => (peak && low
      ? '<li><strong>' + label + ':</strong> en yüksek ' + esc(peak.date) + ' (' + money0(peak.sales) + ', ' + count(peak.orders) +
        ' sipariş), en düşük ' + esc(low.date) + ' (' + money0(low.sales) + ')</li>'
      : '');

    panel().innerHTML =
      '<ul class="sales-summary">' + extremes('Dönem A', d.peakA, d.lowA) + extremes('Dönem B', d.peakB, d.lowB) + '</ul>' +
      chartCard('sa-tab-chart', hourly ? 'Saatlik satış — iki dönem üst üste' : 'Günlük satış — iki dönem üst üste',
        'İki dönem üst üste çizildi: A\'nın 1. günü ile B\'nin 1. günü aynı hizada', 320) +
      '<div class="card"><h3>Haftanın günü × saat yoğunluğu</h3><p class="sub">Hangi gün ve saatte ne kadar satış yapıldığı; renk koyulaştıkça satış artar</p>' +
      '<div class="sales-inline-bar"><div class="field"><label for="sa-heat-period">Dönem</label>' +
      '<select id="sa-heat-period"><option value="b">Dönem B</option><option value="a">Dönem A</option></select></div></div>' +
      '<div class="table-scroll sales-heat-wrap" id="sa-tab-heat"></div></div>' +
      tableCard('sa-tab-series', hourly ? 'Saatlik seri' : 'Günlük seri');

    chart('sa-tab-chart', {
      type: 'line',
      data: {
        labels: d.points.map(pt => (hourly ? (pt.labelB || pt.labelA) : (d.granularity === 'day' ? (pt.index + 1) + '. gün' : ''))),
        datasets: [
          { label: 'Dönem A', data: d.points.map(pt => (pt.labelA ? pt.salesA : null)), borderColor: p.ink3, borderDash: [5, 4],
            pointRadius: hourly ? 0 : 3, tension: 0.25, spanGaps: false },
          { label: 'Dönem B', data: d.points.map(pt => (pt.labelB ? pt.salesB : null)), borderColor: p.series[0],
            backgroundColor: RPA.areaGradient(p.series[0]), fill: true, pointRadius: hourly ? 0 : 3, tension: 0.25, spanGaps: false }
        ]
      },
      options: {
        maintainAspectRatio: false, responsive: true, interaction: { mode: 'index', intersect: false },
        plugins: {
          tooltip: {
            callbacks: {
              title: items => {
                const pt = d.points[items[0].dataIndex];
                return [pt.labelA ? 'A: ' + pt.labelA : '', pt.labelB ? 'B: ' + pt.labelB : ''].filter(Boolean).join(' · ');
              },
              label: c => c.dataset.label + ': ' + money0(c.parsed.y)
            }
          }
        },
        scales: { y: { ticks: { callback: v => NF0.format(v) } } }
      }
    });

    const heat = document.getElementById('sa-tab-heat');
    const drawHeat = which => { heat.innerHTML = heatGrid(which === 'a' ? d.weekdayHourA : d.weekdayHourB); };
    drawHeat('b');
    document.getElementById('sa-heat-period').addEventListener('change', e => drawHeat(e.target.value));

    table('sa-tab-series', d.points, [
      col.count('#', r => r.index + 1),
      col.text('Dönem A', r => r.labelA || ''),
      col.money('Satış A', r => (r.labelA ? r.salesA : null)),
      col.count('Sipariş A', r => (r.labelA ? r.ordersA : null)),
      col.text('Dönem B', r => r.labelB || ''),
      col.money('Satış B', r => (r.labelB ? r.salesB : null)),
      col.count('Sipariş B', r => (r.labelB ? r.ordersB : null))
    ], 'Seri yok.');
  }

  function renderProfit(section) {
    const d = section.data;
    panel().innerHTML = summaryList(section.summary) +
      '<div class="kpi-grid cols-4" id="sa-tab-profit-kpis"></div>' +
      tableCard('sa-tab-profit', 'Kategori bazında komisyon etkisi',
        'Oran etkisi: komisyon oranı değişmeseydi Dönem B\'de ne kadar farklı komisyon alınacağını gösterir');

    const tile = (label, c, f, kind) => {
      const delta = kind === 'rate'
        ? { text: ok(c.abs) ? arrowOf(c.abs) + ' ' + points(c.abs) : DASH, tone: '' }
        : { text: ok(c.pct) ? arrowOf(c.pct) + ' ' + signedPct(c.pct) : DASH, tone: '' };
      return [label, f(c.b), '', 'Dönem A: ' + f(c.a), delta];
    };
    RPA.renderKpis('sa-tab-profit-kpis', [
      tile('Komisyon (KDV hariç)', d.commission, money0),
      tile('Komisyon oranı', d.commissionRate, v => (ok(v) ? '%' + NF2.format(v * 100) : DASH), 'rate'),
      tile('Satıcıya aktarılan', d.transferredToSeller, money0)
    ]);

    table('sa-tab-profit', d.byCategory, [
      col.text('Kategori', r => r.label, true),
      col.money('Satış A', r => r.salesA),
      col.money('Satış B', r => r.salesB),
      col.money('Komisyon A', r => r.commissionA),
      col.money('Komisyon B', r => r.commissionB),
      col.pct('Oran A', r => r.rateA),
      col.pct('Oran B', r => r.rateB),
      { label: 'Oran değişimi', numeric: true, render: r => trend(r.changePp, ok(r.changePp) ? sign(r.changePp) + NF2.format(Math.abs(r.changePp)) + ' puan' : DASH, null), value: r => raw(r.changePp) },
      col.delta('Oran etkisi', r => r.rateEffect, null)
    ], 'Komisyon verisi yok.');
  }

  function renderTab(tab, section) {
    destroyTabCharts();
    // Tables that lived in the previous tab no longer exist; their export entries must not linger.
    ['sa-tab-gainers', 'sa-tab-losers', 'sa-tab-all', 'sa-tab-products', 'sa-tab-status', 'sa-tab-cancel-cat',
      'sa-tab-cancel-seller', 'sa-tab-cancel-req', 'sa-tab-series', 'sa-tab-profit'].forEach(k => {
      csvSpecs.delete(k);
      RPA.registerExport(k, null);
    });

    if (DIMENSION[tab]) renderDimension(tab, section);
    else if (tab === 'product') renderProduct(section);
    else if (tab === 'status') renderStatus(section);
    else if (tab === 'time') renderTime(section);
    else if (tab === 'profit') renderProfit(section);
    syncCsv();
  }

  async function openTab(tab) {
    TAB = tab;
    document.querySelectorAll('#sales-tabs .sales-tab').forEach(button => {
      const active = button.dataset.tab === tab;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
    });
    if (!REQUEST) return;

    if (tabCache[tab]) { renderTab(tab, tabCache[tab]); return; }

    destroyTabCharts();
    panel().innerHTML = '<div class="empty-state sales-loading" role="status">Yükleniyor…</div>';
    const requested = REQUEST;
    try {
      const data = await call('/api/sales-analysis/analyze', Object.assign({}, requested, { sections: [tab] }));
      if (requested !== REQUEST) return;      // the filter moved while this was in flight
      tabCache[tab] = data.sections[tab];
      if (TAB === tab) renderTab(tab, tabCache[tab]);
    } catch (err) {
      if (TAB === tab) panel().innerHTML = '<div class="empty-state">Bu sekme yüklenemedi: ' + esc(err.message) + '</div>';
    }
  }

  // ---------------------------------------------------------------------------
  // Load and analyze
  // ---------------------------------------------------------------------------

  function renderMain() {
    renderHeader(MAIN);
    renderKpis(MAIN.sections.kpi);
    renderReasons(MAIN.sections.reasons);
    renderPvm(MAIN.sections.pvm);
    renderOutlier(MAIN.sections.outlier, MAIN.exclusion);
    syncCsv();
  }

  async function analyze() {
    let body;
    try {
      body = buildRequest();
    } catch (err) {
      RPA.showError('sales-alert', err.message);
      return;
    }
    RPA.clearError('sales-alert');

    const apply = document.getElementById('sales-apply');
    const results = document.getElementById('sales-results');
    RPA.setBusy(apply, true, 'Hesaplanıyor…');
    results.classList.add('is-busy');
    try {
      MAIN = await call('/api/sales-analysis/analyze', Object.assign({}, body, { sections: MAIN_SECTIONS }));
      REQUEST = body;
      tabCache = {};
      renderMain();
      await openTab(TAB);
    } catch (err) {
      RPA.showError('sales-alert', err.message);
    } finally {
      RPA.setBusy(apply, false);
      results.classList.remove('is-busy');
    }
  }

  async function load() {
    const file = currentFile();
    if (!file) {
      RPA.showError('sales-alert', 'Önce yukarıdan sipariş Excel dosyasını (.xlsx) seçin.');
      return;
    }
    RPA.clearError('sales-alert');

    const form = new FormData();
    form.append('file', file);
    const button = document.getElementById('sales-load');
    RPA.setBusy(button, true, 'Yükleniyor…');
    RPA.showSkeleton('sales-skeleton', 'sales-results');
    try {
      LOAD = await call('/api/sales-analysis/load', form);
      FILE_ID = fileId(file);
      REQUEST = null;
      initFilters();
      renderImportNote();
      document.getElementById('sales-intro').hidden = true;
      document.getElementById('sales-results').hidden = false;
      await analyze();
      RPA.revealResults('sales-results');
    } catch (err) {
      RPA.showError('sales-alert', err.message);
    } finally {
      RPA.hideSkeleton('sales-skeleton');
      RPA.setBusy(button, false);
    }
  }

  // The building blocks the country comparison (country-comparison.js) renders with, so both modes
  // format, sort, export and chart exactly alike — and share one CSV registry and click handler.
  RPA.salesUi = {
    NF0, NF1, NF2, DASH, ok, esc, money, money0, count, qty, pct, sign, signedPct, points, raw, arrowOf, trend,
    multiSelect, chart, table, syncCsv, exportButtons, tableCard, chartCard, summaryList, col,
    call,
    forgetTables(keys) { keys.forEach(k => { csvSpecs.delete(k); RPA.registerExport(k, null); }); syncCsv(); },
    destroyCharts(prefix) {
      Object.keys(charts).forEach(id => {
        if (id.indexOf(prefix) === 0 && charts[id]) { charts[id].destroy(); charts[id] = null; }
      });
    }
  };

  // ---------------------------------------------------------------------------
  // View switch
  // ---------------------------------------------------------------------------

  const VIEW_KEY = 'rpa-order-view';
  const MODE_KEY = 'rpa-sales-mode';
  let MODE = 'period';   // 'period' (two periods of one export) | 'country' (two countries' exports)

  /** The shared Orders-export uploader belongs to the period mode; the country mode has its own two. */
  function syncUploadCard() {
    const sales = !document.getElementById('sales-view').hidden;
    document.getElementById('order-upload-card').hidden = sales && MODE === 'country';
    if (sales) document.getElementById('order-title').textContent = MODE === 'country' ? 'Satış Analizi · Ülke Kıyaslama' : 'Satış Analizi';
  }

  function setMode(mode) {
    MODE = mode === 'country' ? 'country' : 'period';
    document.querySelectorAll('.sales-mode-btn').forEach(button => {
      const active = button.dataset.mode === MODE;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
    });
    document.getElementById('sales-period-mode').hidden = MODE !== 'period';
    document.getElementById('country-mode').hidden = MODE !== 'country';
    try { localStorage.setItem(MODE_KEY, MODE); } catch (e) { /* private mode */ }
    syncUploadCard();
    document.dispatchEvent(new CustomEvent('rpa:salesmode', { detail: MODE }));

    if (MODE !== 'period' || document.getElementById('sales-view').hidden) return;
    const file = currentFile();
    if (file && fileId(file) !== FILE_ID) load();
    else if (MAIN) { renderMain(); if (tabCache[TAB]) renderTab(TAB, tabCache[TAB]); }
  }

  function setView(view) {
    const sales = view === 'sales';
    document.querySelectorAll('.view-switch-btn').forEach(button => {
      const active = button.dataset.view === view;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
    });
    document.getElementById('order-view-late').hidden = sales;
    document.getElementById('sales-view').hidden = !sales;
    document.getElementById('order-generate').hidden = sales;
    document.getElementById('order-excel').hidden = sales;
    document.getElementById('sales-load').hidden = !sales;
    document.getElementById('order-title').textContent = sales ? 'Satış Analizi' : 'Late Shipment & Cancellation Report';
    try { localStorage.setItem(VIEW_KEY, view); } catch (e) { /* private mode */ }
    syncUploadCard();

    if (!sales || MODE !== 'period') return;
    // A file picked (or swapped) since the last load is loaded on arrival; the same file is not
    // re-uploaded just because the operator flipped views.
    const file = currentFile();
    if (file && fileId(file) !== FILE_ID) load();
    else if (MAIN) { renderHeader(MAIN); charts.salesPvmChart && charts.salesPvmChart.resize(); }
  }

  document.addEventListener('DOMContentLoaded', function () {
    if (!document.getElementById('sales-view')) return;

    document.querySelectorAll('.view-switch-btn').forEach(button => {
      button.addEventListener('click', () => setView(button.dataset.view));
      button.addEventListener('keydown', e => {
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
        const next = button.dataset.view === 'late' ? 'sales' : 'late';
        setView(next);
        document.querySelector('.view-switch-btn[data-view="' + next + '"]').focus();
      });
    });

    document.getElementById('sales-load').addEventListener('click', load);
    document.getElementById('sales-apply').addEventListener('click', analyze);
    document.getElementById('sales-reset').addEventListener('click', () => { if (LOAD) { resetFilters(); analyze(); } });
    document.getElementById('sales-compare').addEventListener('change', syncCompareFields);

    document.getElementById('sales-tabs').addEventListener('click', e => {
      const button = e.target.closest('.sales-tab');
      if (button) openTab(button.dataset.tab);
    });
    document.getElementById('sales-tabs').addEventListener('keydown', e => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      const i = DETAIL_TABS.indexOf(TAB);
      const next = DETAIL_TABS[(i + (e.key === 'ArrowRight' ? 1 : DETAIL_TABS.length - 1)) % DETAIL_TABS.length];
      openTab(next);
      document.querySelector('#sales-tabs [data-tab="' + next + '"]').focus();
    });

    document.addEventListener('click', e => {
      const button = e.target.closest('[data-csv]');
      if (button && !button.disabled) downloadCsv(button.dataset.csv, button.dataset.csvTitle, button.dataset.csvPrefix);
    });

    // The filter bar opens by default here — the periods are the first thing to set — until the
    // operator collapses it, after which app.js remembers that choice.
    try {
      if (localStorage.getItem('rpa-filters-sales-filter-bar') === null) {
        document.querySelector('#sales-filter-bar .filter-toggle').click();
      }
    } catch (e) { /* private mode */ }

    // Charts bake in theme colours, so redraw from the data already on hand.
    document.addEventListener('rpa:themechange', function () {
      if (!MAIN || document.getElementById('sales-view').hidden || MODE !== 'period') return;
      renderMain();
      if (tabCache[TAB]) renderTab(TAB, tabCache[TAB]);
    });

    document.querySelectorAll('.sales-mode-btn').forEach(button => {
      button.addEventListener('click', () => setMode(button.dataset.mode));
      button.addEventListener('keydown', e => {
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
        const next = button.dataset.mode === 'period' ? 'country' : 'period';
        setMode(next);
        document.querySelector('.sales-mode-btn[data-mode="' + next + '"]').focus();
      });
    });
    let mode = 'period';
    try { mode = localStorage.getItem(MODE_KEY) || 'period'; } catch (e) { /* private mode */ }
    if (mode === 'country') setMode('country');

    let view = 'late';
    try { view = localStorage.getItem(VIEW_KEY) || 'late'; } catch (e) { /* private mode */ }
    if (view === 'sales') setView('sales');
  });

})(window.RPA);
