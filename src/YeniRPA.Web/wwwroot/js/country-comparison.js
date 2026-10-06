/* =============================================================================
   Order Report — Sales Analysis, "Ülke Kıyaslama" mode.

   Two orders exports, one per country, are uploaded together to
   /api/country-comparison/load and held server-side; every settings change asks
   /api/country-comparison/analyze for the whole comparison (KPIs, highlights,
   strengths, category/brand/product/behaviour breakdowns). No raw order line
   reaches this file — exactly like the period mode next to it.

   Formatting, tables, charts and CSV come from RPA.salesUi (sales-analysis.js),
   so both modes look and export alike. The page is Turkish and tr-TR formatted,
   the same explicit exception to the English-UI rule as the period mode.

   Money is shown in each country's own currency. When the two currencies differ
   and no exchange rate is entered, the server marks money rows non-comparable
   and this page prints "—" for their difference, with the reason on hover.
   ============================================================================= */

(function (RPA) {
  'use strict';

  let UI = null;
  let LOAD = null;       // the load result: token, file infos, statuses, currency flags
  let RESULT = null;     // the last analysis
  let TAB = 'category';
  let msStatus = null;

  const TABS = ['category', 'brand', 'product', 'behaviour'];
  const TAB_TABLES = ['cc-tab-strong-a', 'cc-tab-strong-b', 'cc-tab-all', 'cc-tab-products', 'cc-tab-status', 'cc-tab-daily-table'];

  // ---------------------------------------------------------------------------
  // Formatting
  // ---------------------------------------------------------------------------

  const curLabel = c => {
    if (!c) return '';
    const u = String(c).trim().toUpperCase();
    return u === 'TRY' || u === 'TL' ? 'TL' : u;
  };
  const moneyIn = (v, cur) => (UI.ok(v) ? UI.NF2.format(v) + (curLabel(cur) ? ' ' + curLabel(cur) : '') : UI.DASH);
  const money0In = (v, cur) => (UI.ok(v) ? UI.NF0.format(v) + (curLabel(cur) ? ' ' + curLabel(cur) : '') : UI.DASH);
  const num2 = v => (UI.ok(v) ? UI.NF2.format(v) : UI.DASH);

  const nameA = () => (RESULT ? RESULT.a.name : 'Ülke A');
  const nameB = () => (RESULT ? RESULT.b.name : 'Ülke B');

  function fmtKpi(k, v, side) {
    if (k.kind === 'money') return moneyIn(v, side.displayCurrency);
    if (k.kind === 'rate') return UI.pct(v);
    if (k.kind === 'number') return num2(v);
    return UI.count(v);
  }

  function fmtGap(k) {
    const v = k.abs;
    if (!k.comparable || !UI.ok(v)) return UI.DASH;
    if (k.kind === 'rate') return UI.points(v);
    const abs = Math.abs(v);
    const body = k.kind === 'money' ? moneyIn(abs, RESULT.a.displayCurrency) : k.kind === 'number' ? num2(abs) : UI.count(abs);
    return UI.sign(v) + body;
  }

  /** Who leads, in words and with an arrow, so it never rests on colour alone. */
  function leaderHtml(k) {
    if (!k.comparable) return '<span class="cc-muted" title="' + UI.esc(k.note || '') + '">Karşılaştırılamaz</span>';
    if (!k.higher) return UI.DASH;
    if (k.higher === 'tie') return '<span class="cc-muted">≈ Eşit</span>';
    const higherName = k.higher === 'a' ? nameA() : nameB();
    if (!k.leader) return '<span class="cc-muted">Daha yüksek: ' + UI.esc(higherName) + '</span>';
    if (k.leader === 'tie') return '<span class="cc-muted">≈ Eşit</span>';
    const side = k.leader === 'a' ? 'a' : 'b';
    return '<span class="cc-leader is-' + side + '"><span aria-hidden="true">▲</span> ' + UI.esc(side === 'a' ? nameA() : nameB()) + '</span>';
  }

  function leaderText(k) {
    if (!k.comparable) return 'Karşılaştırılamaz';
    if (!k.higher) return '';
    if (k.higher === 'tie' || k.leader === 'tie') return 'Eşit';
    const higherName = k.higher === 'a' ? nameA() : nameB();
    if (!k.leader) return 'Daha yüksek: ' + higherName;
    return k.leader === 'a' ? nameA() : nameB();
  }

  // ---------------------------------------------------------------------------
  // Settings
  // ---------------------------------------------------------------------------

  const fileOf = id => { const el = document.getElementById(id); return el && el.files && el.files[0]; };
  const val = id => document.getElementById(id).value;

  /** A suggested name (the file name) fills an empty field, or one that still holds the last suggestion. */
  function suggestName(id, name) {
    const input = document.getElementById(id);
    if (!input.value.trim() || input.dataset.auto === 'yes') {
      input.value = name;
      input.dataset.auto = 'yes';
    }
  }

  function syncRateField() {
    const field = document.getElementById('country-rate-field');
    field.hidden = !LOAD || LOAD.sameCurrency;
    if (!LOAD) return;
    document.getElementById('country-rate-label').textContent =
      'Kur: 1 ' + (LOAD.b.currency || '?') + ' kaç ' + (LOAD.a.currency || '?') + '? (isteğe bağlı)';
  }

  function resetSettings() {
    msStatus.reset(LOAD.defaultStatuses);
    document.getElementById('country-scope').value = 'all';
    document.getElementById('country-rate').value = '';
  }

  function initSettings() {
    msStatus = UI.multiSelect('country-ms-status', LOAD.statuses, LOAD.defaultStatuses, false);
    const overlap = document.querySelector('#country-scope option[value="overlap"]');
    overlap.disabled = !LOAD.hasOverlap;
    overlap.textContent = LOAD.hasOverlap ? 'Sadece ortak günler' : 'Sadece ortak günler (ortak gün yok)';
    syncRateField();
    resetSettings();
  }

  function buildRequest() {
    const statuses = msStatus.values();
    if (!statuses.length) throw new Error('Satış sayılacak en az bir statü seçin.');
    let rate = null;
    const rawRate = String(val('country-rate') || '').trim().replace(',', '.');
    if (rawRate && !LOAD.sameCurrency) {
      rate = parseFloat(rawRate);
      if (!isFinite(rate) || rate <= 0) throw new Error('Kur pozitif bir sayı olmalı (ör. 35,2). Çevirmek istemiyorsanız alanı boş bırakın.');
    }
    return {
      token: LOAD.token,
      nameA: val('country-a-name').trim(),
      nameB: val('country-b-name').trim(),
      statuses,
      dateScope: val('country-scope'),
      rate
    };
  }

  // ---------------------------------------------------------------------------
  // Header, notes, highlights
  // ---------------------------------------------------------------------------

  function renderHeader() {
    const a = RESULT.a;
    const b = RESULT.b;
    const side = (label, s) => '<strong>' + label + ' · ' + UI.esc(s.name) + ':</strong> ' + UI.esc(s.from) + ' – ' + UI.esc(s.to) +
      ' · ' + s.days + ' gün · ' + UI.count(s.orders) + ' sipariş' + (s.currency ? ' · ' + UI.esc(curLabel(s.currency)) : '');
    document.getElementById('country-period-note').innerHTML =
      '<span>' + side('A', a) + ' &nbsp;|&nbsp; ' + side('B', b) + '</span>';

    document.getElementById('country-warnings').innerHTML = (RESULT.warnings || [])
      .map(w => '<p class="note sales-warn" role="status">' + UI.esc(w) + '</p>').join('');

    const gaps = RESULT.notComparable || [];
    document.getElementById('country-gaps').hidden = !gaps.length;
    document.getElementById('country-gaps-list').innerHTML = gaps.map(g => '<li>' + UI.esc(g) + '</li>').join('');

    const scope = val('country-scope') === 'overlap' ? 'Sadece ortak günler' : 'Tüm tarihler';
    const parts = ['Statü: ' + msStatus.summary(), 'Kapsam: ' + scope];
    if (RESULT.rate) parts.push('Kur: 1 ' + (LOAD.b.currency || '?') + ' = ' + UI.NF2.format(RESULT.rate) + ' ' + (LOAD.a.currency || '?'));
    document.getElementById('country-filter-summary').textContent = a.name + ' ↔ ' + b.name + ' · ' + parts.join(' · ');

    document.getElementById('country-kpi-sub').textContent =
      'Fark = ' + a.name + ' − ' + b.name + '; yüzde fark ' + b.name + ' değerine göre hesaplanır. Oranlarda fark puan olarak gösterilir.' +
      (RESULT.moneyComparable ? '' : ' Para birimleri farklı olduğu ve kur girilmediği için tutar farkları hesaplanmadı.');

    RPA.setExportContext('Ülke Kıyaslama · ' + a.name + ' (' + a.from + '–' + a.to + ') ↔ ' + b.name + ' (' + b.from + '–' + b.to + ') · ' + parts.join(' · '));
  }

  function renderImportNote() {
    const note = document.getElementById('country-import-note');
    const part = (label, info) => {
      const r = info.report;
      const bits = [UI.count(r.rowsRead) + ' satır okundu, ' + UI.count(r.rowsUsed) + ' satır analizde'];
      (r.issues || []).forEach(i => bits.push(i.message + ' (' + UI.count(i.count) + ')'));
      if (r.missingOptionalColumns && r.missingOptionalColumns.length) bits.push('Eksik kolon: ' + r.missingOptionalColumns.join(', '));
      return '<strong>' + label + ' (' + UI.esc(info.fileName) + '):</strong> ' + bits.map(UI.esc).join(' · ');
    };
    note.innerHTML = '<span>' + part('Ülke A', LOAD.a) + '<br>' + part('Ülke B', LOAD.b) + '</span>';
    note.hidden = false;
  }

  function renderInsights() {
    const el = document.getElementById('country-insights');
    const kpis = RESULT.kpis.filter(k => k.leader === 'a' || k.leader === 'b');
    const leadA = kpis.filter(k => k.leader === 'a').length;
    const leadB = kpis.filter(k => k.leader === 'b').length;
    const headline = kpis.length
      ? 'Yönü belli olan ' + kpis.length + ' göstergenin ' + leadA + ' tanesinde ' + nameA() + ', ' + leadB + ' tanesinde ' + nameB() + ' önde.'
      : 'Karşılaştırılabilen göstergelerde iki ülke arasında belirgin bir öne geçme yok.';

    const items = RESULT.insights || [];
    const list = items.length
      ? '<ol class="reasons-list country-insights">' + items.map(i => {
          const side = i.side === 'a' || i.side === 'b' ? i.side : '';
          return '<li class="' + (side ? 'is-' + side : 'is-neutral') + '">' +
            '<span class="cc-badge' + (side ? ' is-' + side : '') + '" title="' + UI.esc(side === 'a' ? nameA() : side === 'b' ? nameB() : 'Genel') + '">' +
              (side ? side.toUpperCase() : '•') + '</span>' +
            '<span class="reason-text">' + UI.esc(i.text) + '</span></li>';
        }).join('') + '</ol>'
      : '<p class="sub">İki ülke arasında öne çıkan, anlamlı büyüklükte bir fark bulunamadı.</p>';

    el.innerHTML = '<p class="sales-headline">' + UI.esc(headline) + '</p>' + list +
      '<p class="table-note">Maddeler yalnızca yüklenen dosyalardaki verilerden hesaplanır ve farkın büyüklüğüne göre sıralanır. ' +
      'Küçük farklar (%5\'ten az ya da birkaç puandan az) listelenmez.</p>';
  }

  function renderKpiTable() {
    const a = RESULT.a;
    const b = RESULT.b;
    UI.table('country-kpi-wrap', RESULT.kpis, [
      { label: 'Gösterge', render: r => UI.esc(r.label), value: r => r.label },
      { label: a.name, numeric: true, render: r => fmtKpi(r, r.a, a), value: r => kpiRaw(r, r.a) },
      { label: b.name, numeric: true, render: r => fmtKpi(r, r.b, b), value: r => kpiRaw(r, r.b) },
      { label: 'Fark (A − B)', numeric: true, render: r => (r.comparable ? UI.esc(fmtGap(r)) : '<span class="cc-muted" title="' + UI.esc(r.note || '') + '">' + UI.DASH + '</span>'),
        value: r => (r.comparable && UI.ok(r.abs) ? (r.kind === 'rate' ? +(r.abs * 100).toFixed(2) : r.abs) : '') },
      { label: '% Fark (B\'ye göre)', numeric: true, render: r => (r.comparable ? UI.esc(UI.signedPct(r.pct)) : UI.DASH),
        value: r => (r.comparable && UI.ok(r.pct) ? +(r.pct * 100).toFixed(2) : '') },
      { label: 'Önde olan', render: leaderHtml, value: leaderText }
    ], 'Gösterge yok.');
  }

  const kpiRaw = (k, v) => (!UI.ok(v) ? '' : k.kind === 'rate' ? +(v * 100).toFixed(2) : v);

  /** Each metric scaled so the higher country is 100 — different units side by side, honestly. */
  function renderKpiChart() {
    const p = RPA.palette();
    const keys = ['orders', 'units', 'dailyOrders', 'gross', 'aov', 'avgPrice', 'unitsPerOrder', 'multiItemShare', 'cancelRate'];
    const rows = keys.map(key => RESULT.kpis.find(k => k.key === key))
      .filter(k => k && k.comparable && UI.ok(k.a) && UI.ok(k.b) && Math.max(k.a, k.b) > 0);
    const scale = k => 100 / Math.max(k.a, k.b);
    UI.chart('countryKpiChart', {
      type: 'bar',
      data: {
        labels: rows.map(k => k.label),
        datasets: [
          { label: nameA(), data: rows.map(k => k.a * scale(k)), backgroundColor: p.series[0], borderRadius: 4, maxBarThickness: 18 },
          { label: nameB(), data: rows.map(k => k.b * scale(k)), backgroundColor: p.series[1], borderRadius: 4, maxBarThickness: 18 }
        ]
      },
      options: {
        indexAxis: 'y', maintainAspectRatio: false, responsive: true,
        plugins: {
          tooltip: {
            callbacks: {
              label: c => {
                const k = rows[c.dataIndex];
                return c.dataset.label + ': ' + (c.datasetIndex === 0 ? fmtKpi(k, k.a, RESULT.a) : fmtKpi(k, k.b, RESULT.b));
              }
            }
          }
        },
        scales: { x: { min: 0, max: 100, ticks: { callback: v => v } }, y: { ticks: { autoSkip: false } } }
      }
    });
  }

  function renderStrengths() {
    const card = (id, name, items, other) => {
      document.getElementById(id).innerHTML =
        '<h3>Güçlü alanlar — ' + UI.esc(name) + '</h3>' +
        '<p class="sub">Bu ülkenin ' + UI.esc(other) + ' karşısında belirgin şekilde önde olduğu alanlar; aynı alanlar ' + UI.esc(other) + ' için görece zayıf</p>' +
        (items.length
          ? '<ul class="sales-summary cc-strength-list">' + items.map(s => '<li>' + UI.esc(s) + '</li>').join('') + '</ul>'
          : '<p class="sub">Belirgin şekilde önde olduğu bir alan bulunamadı.</p>');
    };
    card('country-strength-a', nameA(), RESULT.strengthsA || [], nameB());
    card('country-strength-b', nameB(), RESULT.strengthsB || [], nameA());
  }

  // ---------------------------------------------------------------------------
  // Detail tabs
  // ---------------------------------------------------------------------------

  const panel = () => document.getElementById('country-tab-panel');
  const PREFIX = 'Ülke Kıyaslama';
  const notes = items => (items || []).filter(Boolean).map(t => '<p class="note sales-warn">' + UI.esc(t) + '</p>').join('');

  function shareBars(id, rows, horizontal) {
    const p = RPA.palette();
    UI.chart(id, {
      type: 'bar',
      data: {
        labels: rows.map(r => r.label),
        datasets: [
          { label: nameA(), data: rows.map(r => (UI.ok(r.shareA) ? r.shareA * 100 : 0)), backgroundColor: p.series[0], borderRadius: 4, maxBarThickness: 22 },
          { label: nameB(), data: rows.map(r => (UI.ok(r.shareB) ? r.shareB * 100 : 0)), backgroundColor: p.series[1], borderRadius: 4, maxBarThickness: 22 }
        ]
      },
      options: {
        indexAxis: horizontal ? 'y' : 'x', maintainAspectRatio: false, responsive: true,
        plugins: { tooltip: { callbacks: { label: c => c.dataset.label + ': %' + UI.NF1.format(horizontal ? c.parsed.x : c.parsed.y) } } },
        scales: horizontal
          ? { x: { ticks: { callback: v => '%' + v } }, y: { ticks: { autoSkip: false } } }
          : { y: { ticks: { callback: v => '%' + v } } }
      }
    });
  }

  function renderDimension(dim, title, noun) {
    if (!dim.available) {
      panel().innerHTML = notes([dim.note]);
      return;
    }
    const top = dim.rows.filter(r => r.key !== '__other__')
      .slice().sort((x, y) => Math.max(y.shareA || 0, y.shareB || 0) - Math.max(x.shareA || 0, x.shareB || 0)).slice(0, 10);

    panel().innerHTML = UI.summaryList(dim.summary) + notes([dim.note]) +
      UI.chartCard('cc-tab-chart', 'En büyük 10 ' + noun + ' — satış payı', 'Her ülkenin kendi toplam satışı içindeki pay; para biriminden bağımsızdır', Math.max(260, top.length * 44 + 60)) +
      '<div class="grid-2">' +
        UI.tableCard('cc-tab-strong-a', nameA() + ' için güçlü ' + noun + 'ler', 'Satış payı diğer ülkeye göre en az 2 puan yüksek olanlar', PREFIX) +
        UI.tableCard('cc-tab-strong-b', nameB() + ' için güçlü ' + noun + 'ler', 'Satış payı diğer ülkeye göre en az 2 puan yüksek olanlar', PREFIX) +
      '</div>' +
      UI.tableCard('cc-tab-all', 'Tüm ' + noun + 'ler', noun === 'kategori' ? 'Her iki ülkede de payı %1\'in altında kalanlar "Diğer" altında toplanır' : '', PREFIX);

    shareBars('cc-tab-chart', top, true);

    const small = [
      UI.col.text(title, r => r.label),
      UI.col.pct('Pay — ' + nameA(), r => r.shareA),
      UI.col.pct('Pay — ' + nameB(), r => r.shareB),
      UI.col.points('Pay farkı', r => r.gap, null)
    ];
    UI.table('cc-tab-strong-a', dim.strongA, small, 'Belirgin şekilde güçlü olduğu bir ' + noun + ' yok.');
    UI.table('cc-tab-strong-b', dim.strongB, small, 'Belirgin şekilde güçlü olduğu bir ' + noun + ' yok.');

    UI.table('cc-tab-all', dim.rows, [
      UI.col.text(title, r => r.label, true),
      { label: 'Satış — ' + nameA(), numeric: true, render: r => moneyIn(r.salesA, RESULT.a.displayCurrency), value: r => UI.raw(r.salesA) },
      { label: 'Satış — ' + nameB(), numeric: true, render: r => moneyIn(r.salesB, RESULT.b.displayCurrency), value: r => UI.raw(r.salesB) },
      UI.col.pct('Pay — ' + nameA(), r => r.shareA),
      UI.col.pct('Pay — ' + nameB(), r => r.shareB),
      UI.col.points('Pay farkı (A − B)', r => r.gap, null),
      UI.col.qty('Adet — ' + nameA(), r => r.qtyA),
      UI.col.qty('Adet — ' + nameB(), r => r.qtyB),
      UI.col.count('Sipariş — ' + nameA(), r => r.ordersA),
      UI.col.count('Sipariş — ' + nameB(), r => r.ordersB),
      { label: 'Ort. fiyat — ' + nameA(), numeric: true, render: r => moneyIn(r.priceA, RESULT.a.displayCurrency), value: r => UI.raw(r.priceA) },
      { label: 'Ort. fiyat — ' + nameB(), numeric: true, render: r => moneyIn(r.priceB, RESULT.b.displayCurrency), value: r => UI.raw(r.priceB) }
    ], 'Satış yok.', { maxRows: 300 });
  }

  function renderProducts() {
    const d = RESULT.products;
    const lists = [
      ['topUnitsA', 'En çok satan (adet) — ' + nameA(), 'a'],
      ['topUnitsB', 'En çok satan (adet) — ' + nameB(), 'b'],
      ['topSalesA', 'En çok satan (ciro) — ' + nameA(), 'a'],
      ['topSalesB', 'En çok satan (ciro) — ' + nameB(), 'b']
    ];
    const current = (document.getElementById('cc-product-list') || {}).value || 'topUnitsA';
    panel().innerHTML = UI.summaryList(d.summary) + notes([d.note]) +
      '<div class="sales-inline-bar"><div class="field"><label for="cc-product-list">Liste</label>' +
      '<select id="cc-product-list">' + lists.map(([k, l]) =>
        '<option value="' + k + '"' + (k === current ? ' selected' : '') + '>' + UI.esc(l) + '</option>').join('') +
      '</select></div></div>' +
      UI.tableCard('cc-tab-products', 'Ürünler', 'Ürünler Product SKU ile eşleştirilir; son sütun aynı SKU\'nun diğer ülkedeki satış adedidir', PREFIX);

    const draw = key => {
      const entry = lists.find(l => l[0] === key) || lists[0];
      const side = entry[2] === 'a' ? RESULT.a : RESULT.b;
      const other = entry[2] === 'a' ? nameB() : nameA();
      UI.table('cc-tab-products', d[entry[0]] || [], [
        { label: 'Ürün', render: r => '<span class="sa-clip" title="' + UI.esc(r.title) + '">' + UI.esc(r.title || r.sku) + '</span>', value: r => r.title, filter: 'text' },
        UI.col.text('SKU', r => r.sku, true),
        UI.col.text('Marka', r => r.brand, true),
        UI.col.text('Kategori', r => r.category, true),
        UI.col.qty('Adet', r => r.qty),
        { label: 'Satış', numeric: true, render: r => moneyIn(r.sales, side.displayCurrency), value: r => UI.raw(r.sales) },
        UI.col.pct('Ülke içi pay', r => r.share),
        { label: other + ' adedi', numeric: true, render: r => (r.inOther ? UI.qty(r.otherQty) : '<span class="cc-muted">satılmadı</span>'), value: r => (r.inOther ? r.otherQty : '') }
      ], 'Bu listede ürün yok.');
    };
    draw(current);
    document.getElementById('cc-product-list').addEventListener('change', e => draw(e.target.value));
  }

  function renderBehaviour() {
    const d = RESULT.behaviour;
    const p = RPA.palette();
    panel().innerHTML = UI.summaryList(d.summary) +
      '<div class="grid-2">' +
        UI.chartCard('cc-tab-basket', 'Sipariş başına ürün adedi', 'Siparişlerin adet gruplarına dağılımı (ülke içi pay)', 280) +
        (d.weekdaysComparable
          ? UI.chartCard('cc-tab-weekday', 'Haftanın günlerine göre siparişler', 'Ülke içi pay', 280)
          : '<div class="card"><h3>Haftanın günlerine göre siparişler</h3><p class="sub">Karşılaştırılamadı: iki dosyanın da en az 7 günlük veri içermesi gerekir.</p></div>') +
      '</div>' +
      UI.chartCard('cc-tab-hours', 'Saatlere göre siparişler', 'Ülke içi pay; siparişin ilk satırının oluşturulma saatine göre', 300) +
      UI.chartCard('cc-tab-daily', 'Günlük sipariş sayısı', 'İki ülkenin dönemleri gün sırasıyla hizalandı: her ülkenin 1. günü aynı hizada', 300) +
      UI.tableCard('cc-tab-status', 'Statü dağılımı', 'Tüm satırlar üzerinden; ülke içi pay', PREFIX) +
      UI.tableCard('cc-tab-daily-table', 'Günlük seri', '', PREFIX);

    shareBars('cc-tab-basket', d.basketSizes, false);
    if (d.weekdaysComparable) shareBars('cc-tab-weekday', d.weekdays, false);

    UI.chart('cc-tab-hours', {
      type: 'line',
      data: {
        labels: d.hours.map(h => h.label),
        datasets: [
          { label: nameA(), data: d.hours.map(h => (UI.ok(h.shareA) ? h.shareA * 100 : 0)), borderColor: p.series[0], backgroundColor: RPA.alpha(p.series[0], 0.12), fill: true, tension: 0.3, pointRadius: 2 },
          { label: nameB(), data: d.hours.map(h => (UI.ok(h.shareB) ? h.shareB * 100 : 0)), borderColor: p.series[1], borderDash: [5, 4], tension: 0.3, pointRadius: 2 }
        ]
      },
      options: {
        maintainAspectRatio: false, responsive: true, interaction: { mode: 'index', intersect: false },
        plugins: { tooltip: { callbacks: { label: c => c.dataset.label + ': %' + UI.NF1.format(c.parsed.y) } } },
        scales: { y: { ticks: { callback: v => '%' + v } } }
      }
    });

    UI.chart('cc-tab-daily', {
      type: 'line',
      data: {
        labels: d.daily.map(pt => (pt.index + 1) + '. gün'),
        datasets: [
          { label: nameA(), data: d.daily.map(pt => (pt.labelA ? pt.ordersA : null)), borderColor: p.series[0], tension: 0.25, pointRadius: 3, spanGaps: false },
          { label: nameB(), data: d.daily.map(pt => (pt.labelB ? pt.ordersB : null)), borderColor: p.series[1], borderDash: [5, 4], tension: 0.25, pointRadius: 3, spanGaps: false }
        ]
      },
      options: {
        maintainAspectRatio: false, responsive: true, interaction: { mode: 'index', intersect: false },
        plugins: {
          tooltip: {
            callbacks: {
              title: items => {
                const pt = d.daily[items[0].dataIndex];
                return [pt.labelA ? 'A: ' + pt.labelA : '', pt.labelB ? 'B: ' + pt.labelB : ''].filter(Boolean).join(' · ');
              },
              label: c => c.dataset.label + ': ' + UI.count(c.parsed.y) + ' sipariş'
            }
          }
        }
      }
    });

    UI.table('cc-tab-status', d.statuses, [
      UI.col.text('Statü', r => r.label),
      UI.col.count('Satır — ' + nameA(), r => r.a),
      UI.col.count('Satır — ' + nameB(), r => r.b),
      UI.col.pct('Pay — ' + nameA(), r => r.shareA),
      UI.col.pct('Pay — ' + nameB(), r => r.shareB)
    ], 'Satır yok.');

    UI.table('cc-tab-daily-table', d.daily, [
      UI.col.count('#', r => r.index + 1),
      UI.col.text(nameA() + ' tarihi', r => r.labelA || ''),
      UI.col.count('Sipariş — ' + nameA(), r => (r.labelA ? r.ordersA : null)),
      { label: 'Satış — ' + nameA(), numeric: true, render: r => (r.labelA ? money0In(r.salesA, RESULT.a.displayCurrency) : UI.DASH), value: r => (r.labelA ? r.salesA : '') },
      UI.col.text(nameB() + ' tarihi', r => r.labelB || ''),
      UI.col.count('Sipariş — ' + nameB(), r => (r.labelB ? r.ordersB : null)),
      { label: 'Satış — ' + nameB(), numeric: true, render: r => (r.labelB ? money0In(r.salesB, RESULT.b.displayCurrency) : UI.DASH), value: r => (r.labelB ? r.salesB : '') }
    ], 'Seri yok.');
  }

  function renderTab(tab) {
    UI.destroyCharts('cc-tab-');
    UI.forgetTables(TAB_TABLES);
    if (!RESULT) return;
    if (tab === 'category') renderDimension(RESULT.category, 'Kategori', 'kategori');
    else if (tab === 'brand') renderDimension(RESULT.brand, 'Marka', 'marka');
    else if (tab === 'product') renderProducts();
    else if (tab === 'behaviour') renderBehaviour();
    UI.syncCsv();
  }

  function openTab(tab) {
    TAB = tab;
    document.querySelectorAll('#country-tabs .sales-tab').forEach(button => {
      const active = button.dataset.tab === tab;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
    });
    renderTab(tab);
  }

  function renderAll() {
    renderHeader();
    renderInsights();
    renderKpiTable();
    renderKpiChart();
    renderStrengths();
    renderTab(TAB);
  }

  // ---------------------------------------------------------------------------
  // Load and analyze
  // ---------------------------------------------------------------------------

  async function analyze() {
    let body;
    try {
      body = buildRequest();
    } catch (err) {
      RPA.showError('country-alert', err.message);
      return;
    }
    RPA.clearError('country-alert');

    const apply = document.getElementById('country-apply');
    const results = document.getElementById('country-results');
    RPA.setBusy(apply, true, 'Hesaplanıyor…');
    results.classList.add('is-busy');
    try {
      RESULT = await UI.call('/api/country-comparison/analyze', body);
      renderAll();
    } catch (err) {
      RPA.showError('country-alert', err.message);
    } finally {
      RPA.setBusy(apply, false);
      results.classList.remove('is-busy');
    }
  }

  async function load() {
    const fa = fileOf('country-a-file');
    const fb = fileOf('country-b-file');
    if (!fa || !fb) {
      RPA.showError('country-alert', 'İki ülkenin de sipariş Excel dosyasını (.xlsx) seçin.');
      return;
    }
    RPA.clearError('country-alert');

    const form = new FormData();
    form.append('fileA', fa);
    form.append('fileB', fb);
    const button = document.getElementById('country-load');
    RPA.setBusy(button, true, 'Yükleniyor…');
    RPA.showSkeleton('country-skeleton', 'country-results');
    try {
      LOAD = await UI.call('/api/country-comparison/load', form);
      RESULT = null;
      suggestName('country-a-name', LOAD.a.name);
      suggestName('country-b-name', LOAD.b.name);
      initSettings();
      renderImportNote();
      document.getElementById('country-intro').hidden = true;
      document.getElementById('country-results').hidden = false;
      await analyze();
      RPA.revealResults('country-results');
    } catch (err) {
      RPA.showError('country-alert', err.message);
    } finally {
      RPA.hideSkeleton('country-skeleton');
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  document.addEventListener('DOMContentLoaded', function () {
    if (!document.getElementById('country-mode') || !RPA.salesUi) return;
    UI = RPA.salesUi;

    RPA.initDropzone('country-a-drop', 'country-a-file');
    RPA.initDropzone('country-b-drop', 'country-b-file');
    ['country-a-name', 'country-b-name'].forEach(id => {
      const input = document.getElementById(id);
      input.addEventListener('input', () => { input.dataset.auto = 'no'; });
      input.addEventListener('keydown', e => { if (e.key === 'Enter' && LOAD) analyze(); });
    });

    document.getElementById('country-load').addEventListener('click', load);
    document.getElementById('country-apply').addEventListener('click', () => { if (LOAD) analyze(); });
    document.getElementById('country-reset').addEventListener('click', () => { if (LOAD) { resetSettings(); analyze(); } });

    document.getElementById('country-tabs').addEventListener('click', e => {
      const button = e.target.closest('.sales-tab');
      if (button) openTab(button.dataset.tab);
    });
    document.getElementById('country-tabs').addEventListener('keydown', e => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      const i = TABS.indexOf(TAB);
      const next = TABS[(i + (e.key === 'ArrowRight' ? 1 : TABS.length - 1)) % TABS.length];
      openTab(next);
      document.querySelector('#country-tabs [data-tab="' + next + '"]').focus();
    });

    try {
      if (localStorage.getItem('rpa-filters-country-filter-bar') === null) {
        document.querySelector('#country-filter-bar .filter-toggle').click();
      }
    } catch (e) { /* private mode */ }

    // Charts bake in theme colours, and a chart drawn while its mode was hidden has no size: both
    // redraw from the analysis already on hand.
    const redraw = () => {
      if (!RESULT || document.getElementById('country-mode').hidden || document.getElementById('sales-view').hidden) return;
      renderKpiChart();
      renderTab(TAB);
    };
    document.addEventListener('rpa:themechange', redraw);
    document.addEventListener('rpa:salesmode', e => { if (e.detail === 'country') redraw(); });
  });

})(window.RPA);
