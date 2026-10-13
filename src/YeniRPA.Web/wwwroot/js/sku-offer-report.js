/* =============================================================================
   SKU Offer & Traffic Report.

   Two fixed-template exports (offers + Offer KPIs) are uploaded once; the server
   joins them per product and keeps the result. The table never holds more than
   one page: search, filter chips, sorting and paging are server-side because the
   offers export covers ~190k products.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const PAGE_SIZE = 50;

  const state = {
    batchId: null,
    filter: 'all',
    age: 'all',
    search: '',
    sort: 'sku',
    desc: false,
    page: 1,
    totalPages: 1,
    maxPdp: 0
  };
  const charts = {};
  let searchTimer = null;
  let requestSeq = 0;

  const COLUMNS = [
    { key: 'sku', label: 'Product SKU' },
    { key: 'product', label: 'Product' },
    { key: 'firstDate', label: 'First offer' },
    { key: 'approvalDate', label: 'Approved' },
    { key: 'activeOffers', label: 'Offers', numeric: true },
    { key: 'pdp', label: 'PDP views (30d)', numeric: true },
    { key: 'atc', label: 'Add to cart', numeric: true },
    { key: 'atcRate', label: 'ATC rate', numeric: true }
  ];

  function el(id) { return document.getElementById(id); }

  function filesChosen() {
    return el('sor-offers-file').files.length > 0 && el('sor-traffic-file').files.length > 0 &&
      el('sor-approvals-file').files.length > 0;
  }

  function updateGenerateButton() { el('sor-generate').disabled = !filesChosen(); }

  /** ISO date from the server -> dd.MM.yyyy, the format the team reads dates in. */
  function fmtDate(iso) {
    if (!iso) return '-';
    const d = new Date(iso);
    const pad = n => String(n).padStart(2, '0');
    return pad(d.getDate()) + '.' + pad(d.getMonth() + 1) + '.' + d.getFullYear();
  }

  function fmtNum(v) { return v === null || v === undefined ? '-' : RPA.fmtInt(Math.round(v)); }

  async function getJson(url) {
    const response = await fetch(url);
    const text = await response.text();
    let body = null;
    try { body = JSON.parse(text); } catch (e) { /* handled below */ }
    if (!response.ok || !body || body.success === false) {
      throw new Error((body && body.message) || text || ('Request failed with status ' + response.status + '.'));
    }
    return body;
  }

  // ---------------------------------------------------------------------------
  // Overview
  // ---------------------------------------------------------------------------

  function renderOverview(summary) {
    const closed = summary.firstOfferClosedSkus;
    const pct = summary.totalSkus ? ' (' + (closed / summary.totalSkus * 100).toFixed(1) + '%)' : '';

    const items = [
      ['SKUs', RPA.fmtInt(summary.totalSkus), ''],
      ['Offers', RPA.fmtInt(summary.totalOffers), '', RPA.fmtInt(summary.activeOffers) + ' active'],
      ['First offer closed', RPA.fmtInt(closed), closed ? 'red' : 'green', pct.trim()],
      ['No active offer', RPA.fmtInt(summary.noActiveOfferSkus), summary.noActiveOfferSkus ? 'amber' : 'green'],
      ['PDP views (30d)', RPA.fmtInt(summary.totalPdpViews), '', RPA.fmtInt(summary.totalAddToCart) + ' add to cart'],
      ['SKUs with traffic data', RPA.fmtInt(summary.skusWithTraffic), '', RPA.fmtInt(summary.skusWithoutTraffic) + ' without'],
      ['Approved SKUs', RPA.fmtInt(summary.approvedSkus), '', RPA.fmtInt(summary.notApprovedSkus) + ' not in approvals file']
    ];
    if (summary.hasImpressions) items.splice(5, 0, ['Impressions', RPA.fmtInt(summary.totalImpressions), '']);
    RPA.renderKpis('sor-kpis', items);

    const notes = [];
    if (summary.trafficProductsNotInOffers) {
      notes.push(RPA.fmtInt(summary.trafficProductsNotInOffers) + ' product(s) in the traffic file have no offer in the offers file.');
    }
    if (summary.unreadableDates) {
      notes.push(RPA.fmtInt(summary.unreadableDates) + ' offer row(s) had an unreadable creation date and were left out of the first-offer calculation.');
    }
    if (summary.approvalProductsNotInOffers) {
      notes.push(RPA.fmtInt(summary.approvalProductsNotInOffers) + ' approved product(s) have no offer in the offers file.');
    }
    if (!summary.hasImpressions) {
      notes.push('The traffic file has no impression column - only PDP views and add-to-cart sessions are shown.');
    }
    el('sor-notes').textContent = notes.join(' ');
    el('sor-notes').hidden = notes.length === 0;

    const callout = el('sor-callout');
    callout.hidden = closed === 0;
    el('sor-callout-text').textContent = RPA.fmtInt(closed) +
      ' SKU(s) have a first offer that is no longer active' +
      (summary.noActiveOfferSkus ? ' - ' + RPA.fmtInt(summary.noActiveOfferSkus) + ' have no active offer at all.' : '.');
  }

  // ---------------------------------------------------------------------------
  // Charts
  // ---------------------------------------------------------------------------

  function drawChart(id, config) {
    if (charts[id]) charts[id].destroy();
    charts[id] = new Chart(el(id), config);
  }

  function renderCharts(data) {
    if (typeof Chart === 'undefined') return;
    const p = RPA.palette();
    const common = { maintainAspectRatio: false, responsive: true };
    const axis = { grid: { color: p.grid }, border: { display: false }, beginAtZero: true, ticks: { maxTicksLimit: 5 } };
    const cat = { grid: { display: false }, border: { display: false } };
    const legend = { position: 'bottom', labels: { boxWidth: 10, boxHeight: 10, usePointStyle: true } };

    drawChart('sor-chart-month', {
      type: 'bar',
      data: {
        labels: data.byMonth.map(m => m.month),
        datasets: [
          { label: 'Still open', data: data.byMonth.map(m => m.skus - m.closed), backgroundColor: p.series[0], borderRadius: 3, maxBarThickness: 36, stack: 's' },
          { label: 'First offer closed', data: data.byMonth.map(m => m.closed), backgroundColor: p.markCritical, borderRadius: 3, maxBarThickness: 36, stack: 's' }
        ]
      },
      options: Object.assign({}, common, {
        plugins: { legend },
        scales: { x: Object.assign({ stacked: true }, cat), y: Object.assign({ stacked: true }, axis) }
      })
    });

    drawChart('sor-chart-category', {
      type: 'bar',
      data: {
        labels: data.byCategory.map(c => c.category),
        datasets: [
          { label: 'First offer closed', data: data.byCategory.map(c => c.closed), backgroundColor: p.markCritical, borderRadius: 3, maxBarThickness: 22, stack: 's' },
          { label: 'Still open', data: data.byCategory.map(c => c.active), backgroundColor: p.series[0], borderRadius: 3, maxBarThickness: 22, stack: 's' }
        ]
      },
      options: Object.assign({}, common, {
        indexAxis: 'y',
        plugins: { legend },
        scales: { x: Object.assign({ stacked: true }, axis), y: Object.assign({ stacked: true }, cat) }
      })
    });

    drawChart('sor-chart-age', {
      type: 'bar',
      data: {
        labels: data.byAge.map(a => a.label),
        datasets: [
          { label: 'Still open', data: data.byAge.map(a => a.skus - a.closed), backgroundColor: p.series[0], borderRadius: 3, maxBarThickness: 48, stack: 's' },
          { label: 'First offer closed', data: data.byAge.map(a => a.closed), backgroundColor: p.markCritical, borderRadius: 3, maxBarThickness: 48, stack: 's' }
        ]
      },
      options: Object.assign({}, common, {
        plugins: { legend },
        scales: { x: Object.assign({ stacked: true }, cat), y: Object.assign({ stacked: true }, axis) }
      })
    });

    drawChart('sor-chart-top', {
      type: 'bar',
      data: {
        labels: data.topTraffic.map(t => t.sku),
        datasets: [{
          label: 'PDP views (30d)',
          data: data.topTraffic.map(t => t.pdpViews),
          backgroundColor: data.topTraffic.map(t => t.firstOfferClosed ? p.markCritical : p.series[0]),
          borderRadius: 3, maxBarThickness: 40
        }]
      },
      options: Object.assign({}, common, {
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              title: items => data.topTraffic[items[0].dataIndex].sku + ' - ' + data.topTraffic[items[0].dataIndex].product,
              label: item => RPA.fmtInt(item.parsed.y) + ' PDP views'
            }
          }
        },
        scales: { x: cat, y: axis }
      })
    });
  }

  // ---------------------------------------------------------------------------
  // Table
  // ---------------------------------------------------------------------------

  function renderHead() {
    el('sor-thead').innerHTML = '<tr>' + COLUMNS.map(function (c) {
      const active = state.sort === c.key;
      const arrow = active ? (state.desc ? ' ▼' : ' ▲') : '';
      return '<th scope="col" class="' + (c.numeric ? 'num ' : '') + (active ? 'is-sorted' : '') + '" aria-sort="' +
        (active ? (state.desc ? 'descending' : 'ascending') : 'none') + '">' +
        '<button type="button" class="sor-sort" data-sort="' + c.key + '">' + RPA.escapeHtml(c.label) + arrow + '</button></th>';
    }).join('') + '</tr>';
  }

  function statusPill(row) {
    if (!row.firstOfferDate) return '<span class="badge">No date</span>';
    return row.firstOfferActive
      ? '<span class="badge green">Active</span>'
      : '<span class="badge ' + (row.severity === 'critical' ? 'red' : 'amber') + '">Closed</span>';
  }

  const AGE_LABELS = { baby: 'Baby', young: 'Young', adult: 'Adult', mature: 'Mature' };

  function rowHtml(r) {
    const meta = [r.brand, r.category].filter(Boolean).map(RPA.escapeHtml).join(' &middot; ');
    const warn = r.warning
      ? '<div class="sor-warn is-' + RPA.escapeHtml(r.severity || 'warning') + '">' + RPA.escapeHtml(r.warning) +
        (r.firstOfferReason ? ' <span class="sor-reason">(' + RPA.escapeHtml(r.firstOfferReason) + ')</span>' : '') + '</div>'
      : '';

    let pdp = '<span class="sor-na" title="Not in the traffic file">no data</span>';
    if (r.hasTraffic) {
      const width = state.maxPdp > 0 ? Math.max(2, Math.round((r.pdpViews || 0) / state.maxPdp * 100)) : 0;
      pdp = '<span class="sor-bar" aria-hidden="true"><span style="width:' + width + '%"></span></span>' +
        '<span class="sor-bar-val">' + fmtNum(r.pdpViews) + '</span>';
    }

    return '<tr>' +
      '<td class="mono">' + RPA.escapeHtml(r.sku) + '</td>' +
      '<td class="sor-product"><div class="sor-product-name">' + RPA.escapeHtml(r.product || '-') + '</div>' +
        (meta ? '<div class="sor-meta">' + meta + '</div>' : '') + warn + '</td>' +
      '<td class="sor-first"><div>' + fmtDate(r.firstOfferDate) + '</div>' + statusPill(r) +
        (r.firstOfferSeller ? '<div class="sor-meta">' + RPA.escapeHtml(r.firstOfferSeller) + '</div>' : '') +
        (r.ageGroup ? '<div><span class="sor-age" title="' + RPA.fmtInt(r.ageDays) + ' days old">' + AGE_LABELS[r.ageGroup] + '</span></div>' : '') + '</td>' +
      '<td class="sor-approved">' + (r.hasApproval ? '<div>' + fmtDate(r.approvalDate) + '</div>' + (r.approvedBy ? '<div class="sor-meta">' + RPA.escapeHtml(r.approvedBy) + '</div>' : '') : '<span class="sor-na" title="Not in the approved products file">no data</span>') + '</td>' +
      '<td class="num"><strong>' + RPA.fmtInt(r.activeOffers) + '</strong><span class="sor-meta"> / ' + RPA.fmtInt(r.totalOffers) + '</span></td>' +
      '<td class="num sor-pdp">' + pdp + '</td>' +
      '<td class="num">' + (r.hasTraffic ? fmtNum(r.addToCart) : '-') + '</td>' +
      '<td class="num">' + (r.atcRate === null || r.atcRate === undefined ? '-' : (r.atcRate * 100).toFixed(1) + '%') + '</td>' +
      '</tr>';
  }

  function queryString(extra) {
    const q = new URLSearchParams({
      batchId: state.batchId, search: state.search, filter: state.filter, age: state.age,
      sort: state.sort, desc: state.desc ? 'true' : 'false'
    });
    Object.keys(extra || {}).forEach(k => q.set(k, extra[k]));
    return q.toString();
  }

  async function loadPage() {
    if (!state.batchId) return;
    const seq = ++requestSeq;
    el('sor-table').classList.add('is-loading');
    try {
      const body = await getJson('/api/sku-offer-report/rows?' + queryString({ page: state.page, pageSize: PAGE_SIZE }));
      if (seq !== requestSeq) return; // a newer keystroke/click superseded this response
      const data = body.data;

      state.page = data.page;
      state.totalPages = Math.max(1, Math.ceil(data.total / data.pageSize));
      state.maxPdp = data.rows.reduce((m, r) => Math.max(m, r.pdpViews || 0), 0);

      renderHead();
      el('sor-tbody').innerHTML = data.rows.length
        ? data.rows.map(rowHtml).join('')
        : '<tr><td colspan="' + COLUMNS.length + '" class="sor-empty">No SKUs match this search and filter.</td></tr>';

      el('sor-count').textContent = RPA.fmtInt(data.total) + ' SKU(s)';
      el('sor-page-label').textContent = 'Page ' + RPA.fmtInt(data.page) + ' of ' + RPA.fmtInt(state.totalPages);
      el('sor-prev').disabled = data.page <= 1;
      el('sor-next').disabled = data.page >= state.totalPages;
    } catch (err) {
      if (seq === requestSeq) RPA.showError('sor-alert', err.message);
    } finally {
      if (seq === requestSeq) el('sor-table').classList.remove('is-loading');
    }
  }

  function setFilter(filter) {
    state.filter = filter;
    state.page = 1;
    document.querySelectorAll('#sor-chips .sor-chip').forEach(function (chip) {
      chip.classList.toggle('is-on', chip.dataset.filter === filter);
      chip.setAttribute('aria-pressed', chip.dataset.filter === filter ? 'true' : 'false');
    });
    loadPage();
  }

  // ---------------------------------------------------------------------------
  // Stockout PDP view
  // ---------------------------------------------------------------------------

  const so = { rows: [], filter: 'all', search: '', sort: 'pdp', desc: true };

  const SO_COLUMNS = [
    { key: 'product', label: 'Product' },
    { key: 'pdp', label: 'PDP sessions (30d)', numeric: true },
    { key: 'atc', label: 'Add to cart (30d)', numeric: true },
    { key: 'atcRate', label: 'ATC rate', numeric: true },
    { key: 'sold7d', label: 'Sold (7d)', numeric: true },
    { key: 'gmv7d', label: 'GMV (7d)', numeric: true },
    { key: 'offers', label: 'Offers', numeric: true },
    { key: 'firstDate', label: 'First offer' }
  ];

  function soValue(r, key) {
    switch (key) {
      case 'product': return (r.product || '').toLowerCase();
      case 'pdp': return r.pdpViews === null ? -1 : r.pdpViews;
      case 'atc': return r.addToCart === null ? -1 : r.addToCart;
      case 'atcRate': return r.atcRate === null ? -1 : r.atcRate;
      case 'sold7d': return r.sold7d;
      case 'gmv7d': return r.gmv7d;
      case 'offers': return r.inOffers ? r.activeOffers : -1;
      case 'firstDate': return r.firstOfferDate ? new Date(r.firstOfferDate).getTime() : Number.MAX_SAFE_INTEGER;
      default: return 0;
    }
  }

  function soVisibleRows() {
    const q = so.search.toLowerCase();
    let rows = so.rows.filter(function (r) {
      if (so.filter === 'hasTraffic' && !r.hasTraffic) return false;
      if (so.filter === 'noTraffic' && r.hasTraffic) return false;
      if (so.filter === 'noActive' && !(r.inOffers && r.activeOffers === 0)) return false;
      if (so.filter === 'retail' && r.retailBuyable !== true) return false;
      if (!q) return true;
      return [r.productId, r.product, r.brand, r.category, r.sellers].join(' ').toLowerCase().indexOf(q) >= 0;
    });
    const dir = so.desc ? -1 : 1;
    rows = rows.slice().sort(function (a, b) {
      const x = soValue(a, so.sort), y = soValue(b, so.sort);
      return x < y ? -dir : x > y ? dir : 0;
    });
    return rows;
  }

  function soRowHtml(r) {
    const meta = [r.brand, r.category].filter(Boolean).map(RPA.escapeHtml).join(' &middot; ');
    const retail = r.retailBuyable === true ? ' <span class="sor-retail is-yes" title="Retail can still sell this product">Retail buyable</span>'
      : (r.retailBuyable === false ? ' <span class="sor-retail is-no">Not retail buyable</span>' : '');
    const offers = r.inOffers
      ? '<strong>' + RPA.fmtInt(r.activeOffers) + '</strong><span class="sor-meta"> / ' + RPA.fmtInt(r.totalOffers) + '</span>'
      : '<span class="sor-na" title="Product not in the offers file">not in offers</span>';
    const first = r.inOffers
      ? '<div>' + fmtDate(r.firstOfferDate) + '</div>' +
        (r.firstOfferDate ? '<span class="badge ' + (r.firstOfferClosed ? 'red' : 'green') + '">' + (r.firstOfferClosed ? 'Closed' : 'Active') + '</span>' : '') +
        (r.ageGroup ? '<div><span class="sor-age">' + AGE_LABELS[r.ageGroup] + '</span></div>' : '')
      : '<span class="sor-na">-</span>';

    return '<tr>' +
      '<td class="sor-product"><div class="sor-product-name">' + RPA.escapeHtml(r.product || '-') + '</div>' +
        '<div class="sor-meta"><span class="mono">' + RPA.escapeHtml(r.productId) + '</span>' + (meta ? ' &middot; ' + meta : '') + '</div>' +
        (r.sellers ? '<div class="sor-meta">' + RPA.escapeHtml(r.sellers) + retail + '</div>' : (retail ? '<div class="sor-meta">' + retail + '</div>' : '')) + '</td>' +
      '<td class="num">' + (r.hasTraffic ? '<strong>' + fmtNum(r.pdpViews) + '</strong>' : '<span class="sor-na" title="Not in the Offer KPIs file">no data</span>') + '</td>' +
      '<td class="num">' + (r.hasTraffic ? fmtNum(r.addToCart) : '-') + '</td>' +
      '<td class="num">' + (r.atcRate === null || r.atcRate === undefined ? '-' : (r.atcRate * 100).toFixed(1) + '%') + '</td>' +
      '<td class="num">' + fmtNum(r.sold7d) + '</td>' +
      '<td class="num">' + fmtNum(r.gmv7d) + '</td>' +
      '<td class="num">' + offers + '</td>' +
      '<td class="sor-first">' + first + '</td>' +
      '</tr>';
  }

  function renderStockoutTable() {
    el('sor-so-thead').innerHTML = '<tr>' + SO_COLUMNS.map(function (c) {
      const active = so.sort === c.key;
      const arrow = active ? (so.desc ? ' \u25BC' : ' \u25B2') : '';
      return '<th scope="col" class="' + (c.numeric ? 'num ' : '') + (active ? 'is-sorted' : '') + '" aria-sort="' +
        (active ? (so.desc ? 'descending' : 'ascending') : 'none') + '">' +
        '<button type="button" class="sor-sort" data-sosort="' + c.key + '">' + RPA.escapeHtml(c.label) + arrow + '</button></th>';
    }).join('') + '</tr>';

    const rows = soVisibleRows();
    el('sor-so-tbody').innerHTML = rows.length
      ? rows.map(soRowHtml).join('')
      : '<tr><td colspan="' + SO_COLUMNS.length + '" class="sor-empty">No stockout products match this search and filter.</td></tr>';
    el('sor-so-count').textContent = RPA.fmtInt(rows.length) + ' of ' + RPA.fmtInt(so.rows.length) + ' product(s)';
  }

  function renderStockoutSummary(data) {
    const s = data.summary;
    RPA.renderKpis('sor-so-kpis', [
      ['Stockout products', RPA.fmtInt(s.products), s.products ? 'amber' : 'green', RPA.fmtInt(s.notInOffers) + ' not in offers file'],
      ['PDP sessions (30d)', RPA.fmtInt(s.totalPdpViews), '', s.sharePctOfAllPdp.toFixed(2) + '% of all PDP sessions'],
      ['Add to cart (30d)', RPA.fmtInt(s.totalAddToCart), ''],
      ['Sold items (7d)', RPA.fmtInt(s.sold7d), ''],
      ['GMV (7d)', RPA.fmtInt(s.gmv7d), ''],
      ['With PDP data', RPA.fmtInt(s.withTraffic), '', RPA.fmtInt(s.withoutTraffic) + ' without']
    ]);

    const notes = [];
    if (s.withoutTraffic) {
      notes.push(RPA.fmtInt(s.withoutTraffic) + ' of ' + RPA.fmtInt(s.products) +
        ' stockout product(s) are missing from the Offer KPIs file, so their PDP sessions are unknown (not zero).');
    }
    el('sor-so-notes').textContent = notes.join(' ');
    el('sor-so-notes').hidden = notes.length === 0;

    if (typeof Chart === 'undefined') return;
    const p = RPA.palette();
    drawChart('sor-chart-stockout', {
      type: 'bar',
      data: {
        labels: data.topPdp.map(t => t.productId),
        datasets: [{ label: 'PDP sessions (30d)', data: data.topPdp.map(t => t.pdpViews), backgroundColor: p.markSerious, borderRadius: 3, maxBarThickness: 40 }]
      },
      options: {
        maintainAspectRatio: false, responsive: true,
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              title: items => data.topPdp[items[0].dataIndex].productId + ' - ' + data.topPdp[items[0].dataIndex].product,
              label: item => RPA.fmtInt(item.parsed.y) + ' PDP sessions'
            }
          }
        },
        scales: {
          x: { grid: { display: false }, border: { display: false } },
          y: { grid: { color: p.grid }, border: { display: false }, beginAtZero: true, ticks: { maxTicksLimit: 5 } }
        }
      }
    });
  }

  async function loadStockoutRows() {
    const body = await getJson('/api/sku-offer-report/stockout?batchId=' + encodeURIComponent(state.batchId));
    so.rows = body.data.rows;
    so.filter = 'all'; so.search = ''; so.sort = 'pdp'; so.desc = true;
    el('sor-so-search').value = '';
    document.querySelectorAll('#sor-so-chips .sor-chip').forEach(c => c.classList.toggle('is-on', c.dataset.sofilter === 'all'));
    renderStockoutTable();
  }

  function switchView(view) {
    el('sor-view-skus').hidden = view !== 'skus';
    el('sor-view-stockout').hidden = view !== 'stockout';
    document.querySelectorAll('#sor-views .sor-view').forEach(function (b) {
      const on = b.dataset.view === view;
      b.classList.toggle('is-on', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
    });
  }

  async function downloadStockout() {
    if (!state.batchId) return;
    RPA.clearError('sor-alert');
    const button = el('sor-so-download');
    RPA.setBusy(button, true, 'Preparing\u2026');
    try {
      await RPA.postDownloadJson('/api/sku-offer-report/stockout-download', { batchId: state.batchId }, 'stockout_pdp.xlsx');
    } catch (err) {
      RPA.showError('sor-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  async function generate() {
    RPA.clearError('sor-alert');
    el('sor-results').hidden = true;
    state.batchId = null;

    const form = new FormData();
    form.append('offersFile', el('sor-offers-file').files[0]);
    form.append('trafficFile', el('sor-traffic-file').files[0]);
    form.append('approvalsFile', el('sor-approvals-file').files[0]);
    if (el('sor-stockout-file').files.length > 0) form.append('stockoutFile', el('sor-stockout-file').files[0]);

    const button = el('sor-generate');
    RPA.setBusy(button, true, 'Reading files…');
    try {
      const body = await RPA.postJson('/api/sku-offer-report/generate', form);
      state.batchId = body.data.batchId;
      state.page = 1;
      state.search = '';
      state.age = 'all';
      el('sor-search').value = '';

      el('sor-results').hidden = false;
      renderOverview(body.data.summary);
      renderCharts(body.data.charts);
      document.querySelectorAll('#sor-age-chips .sor-chip').forEach(c => c.classList.toggle('is-on', c.dataset.age === 'all'));
      setFilter('all');

      const hasStockout = !!body.data.stockout;
      el('sor-views').hidden = !hasStockout;
      switchView('skus');
      if (hasStockout) {
        renderStockoutSummary(body.data.stockout);
        await loadStockoutRows();
      }
    } catch (err) {
      RPA.showError('sor-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  async function download() {
    if (!state.batchId) return;
    RPA.clearError('sor-alert');
    const button = el('sor-download');
    RPA.setBusy(button, true, 'Preparing…');
    try {
      await RPA.postDownloadJson('/api/sku-offer-report/download', {
        batchId: state.batchId, search: state.search, filter: state.filter, age: state.age, sort: state.sort, desc: state.desc
      }, 'sku_teklif_trafik.xlsx');
    } catch (err) {
      RPA.showError('sor-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('sor-offers-drop', 'sor-offers-file');
    RPA.initDropzone('sor-traffic-drop', 'sor-traffic-file');
    RPA.initDropzone('sor-approvals-drop', 'sor-approvals-file');
    RPA.initDropzone('sor-stockout-drop', 'sor-stockout-file');

    el('sor-offers-file').addEventListener('change', updateGenerateButton);
    el('sor-traffic-file').addEventListener('change', updateGenerateButton);
    el('sor-approvals-file').addEventListener('change', updateGenerateButton);
    el('sor-generate').addEventListener('click', generate);

    el('sor-views').addEventListener('click', function (event) {
      const tab = event.target.closest('.sor-view');
      if (tab) switchView(tab.dataset.view);
    });
    el('sor-so-download').addEventListener('click', downloadStockout);
    el('sor-so-search').addEventListener('input', function () {
      so.search = el('sor-so-search').value.trim();
      renderStockoutTable();
    });
    el('sor-so-chips').addEventListener('click', function (event) {
      const chip = event.target.closest('.sor-chip');
      if (!chip) return;
      so.filter = chip.dataset.sofilter;
      document.querySelectorAll('#sor-so-chips .sor-chip').forEach(c => c.classList.toggle('is-on', c === chip));
      renderStockoutTable();
    });
    el('sor-so-thead').addEventListener('click', function (event) {
      const button = event.target.closest('.sor-sort');
      if (!button) return;
      const key = button.dataset.sosort;
      if (so.sort === key) so.desc = !so.desc;
      else { so.sort = key; so.desc = key !== 'product'; }
      renderStockoutTable();
    });
    el('sor-download').addEventListener('click', download);

    el('sor-search').addEventListener('input', function () {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(function () {
        state.search = el('sor-search').value.trim();
        state.page = 1;
        loadPage();
      }, 250);
    });

    el('sor-chips').addEventListener('click', function (event) {
      const chip = event.target.closest('.sor-chip');
      if (chip) setFilter(chip.dataset.filter);
    });

    el('sor-age-chips').addEventListener('click', function (event) {
      const chip = event.target.closest('.sor-chip');
      if (!chip) return;
      state.age = chip.dataset.age;
      state.page = 1;
      document.querySelectorAll('#sor-age-chips .sor-chip').forEach(function (c) {
        c.classList.toggle('is-on', c.dataset.age === state.age);
        c.setAttribute('aria-pressed', c.dataset.age === state.age ? 'true' : 'false');
      });
      loadPage();
    });

    el('sor-callout').addEventListener('click', function () {
      setFilter('firstClosed');
      el('sor-table').scrollIntoView({ behavior: RPA.reducedMotion() ? 'auto' : 'smooth', block: 'start' });
    });

    el('sor-thead').addEventListener('click', function (event) {
      const button = event.target.closest('.sor-sort');
      if (!button) return;
      const key = button.dataset.sort;
      if (state.sort === key) state.desc = !state.desc;
      else { state.sort = key; state.desc = key !== 'sku' && key !== 'product' && key !== 'approvalDate'; }
      state.page = 1;
      loadPage();
    });

    el('sor-prev').addEventListener('click', function () { if (state.page > 1) { state.page--; loadPage(); } });
    el('sor-next').addEventListener('click', function () { if (state.page < state.totalPages) { state.page++; loadPage(); } });
  });

})(window.RPA);
