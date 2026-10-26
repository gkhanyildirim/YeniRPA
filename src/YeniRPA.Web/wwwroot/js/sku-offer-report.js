/* =============================================================================
   SKU Offer & Traffic Report.

   Fixed-template exports (offers, combined sales, approvals, optional stockout) are uploaded once; the server
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
    { key: 'impressions', label: 'Impressions', numeric: true },
    { key: 'pdp', label: 'PDP sessions', numeric: true },
    { key: 'atcRate', label: 'ATC %', numeric: true },
    { key: 'crPdp', label: 'CR %', numeric: true },
    { key: 'sold', label: 'Sold', numeric: true },
    { key: 'gmv', label: 'GMV (₺)', numeric: true },
    { key: 'trend', label: 'Sales by period', nosort: true }
  ];

  function el(id) { return document.getElementById(id); }

  function filesChosen() {
    return el('sor-offers-file').files.length > 0 && el('sor-sales-file').files.length > 0 &&
      el('sor-approvals-file').files.length > 0;
  }

  /**
   * The EUR -> TRY rate as typed: "48,5" and "48.5" both work. Returns the number, or null when it is
   * empty or not a usable rate (the server enforces the same bounds).
   */
  function parseRate() {
    const text = el('sor-rate').value.trim().replace(',', '.');
    if (!/^\d+(\.\d+)?$/.test(text)) return null;
    const rate = parseFloat(text);
    return rate > 0 && rate <= 1000 ? rate : null;
  }

  function updateGenerateButton() {
    const typed = el('sor-rate').value.trim();
    el('sor-rate').setAttribute('aria-invalid', typed && parseRate() === null ? 'true' : 'false');
    el('sor-generate').disabled = !(filesChosen() && parseRate() !== null);
  }

  function rememberRate(rate) {
    try { localStorage.setItem('sor-eur-try-rate', String(rate)); } catch (e) { /* private window: just not remembered */ }
  }

  function savedRate() {
    try { return localStorage.getItem('sor-eur-try-rate') || ''; } catch (e) { return ''; }
  }

  /** Active sellers: one name inline, several behind a disclosure so the Offers cell stays compact. */
  function sellersHtml(names) {
    if (!names || names.length === 0) return '';
    if (names.length === 1) return '<div class="sor-meta" title="Seller with an active offer">' + RPA.escapeHtml(names[0]) + '</div>';
    return '<details class="sor-sellers"><summary title="Sellers with an active offer">' + names.length + ' sellers</summary>' +
      '<ul class="sor-sellers-list">' + names.map(n => '<li>' + RPA.escapeHtml(n) + '</li>').join('') + '</ul></details>';
  }

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
      ['Approved SKUs', RPA.fmtInt(summary.approvedSkus), '', RPA.fmtInt(summary.notApprovedSkus) + ' not in approvals file'],
      { group: 'Sales & traffic \u00b7 ' + summary.salesWindow },
      ['Unique SKUs sold', RPA.fmtInt(summary.uniqueSkusSold), 'green', RPA.fmtInt(summary.uniqueSkusSoldInOffers) + ' of them in the offers file'],
      ['Sold items', RPA.fmtInt(summary.totalSoldQty), ''],
      ['GMV (₺, w/o shipping)', RPA.fmtInt(summary.totalGmv), '', '1 EUR = ' + summary.eurTryRate + ' TRY'],
      ['Impressions', RPA.fmtInt(summary.totalImpressions), '', 'CTR ' + (summary.overallCtr * 100).toFixed(2) + '%'],
      ['PDP sessions', RPA.fmtInt(summary.totalPdpViews), '', 'ATC ' + (summary.overallAtcRate * 100).toFixed(2) + '%'],
      ['Conversion (CR PDP)', (summary.overallCrPdp * 100).toFixed(2) + '%', ''],
      ['SKUs with sales data', RPA.fmtInt(summary.skusWithTraffic), '', RPA.fmtInt(summary.skusWithoutTraffic) + ' without'],
      ['Traffic, no active offer', RPA.fmtInt(summary.lostTrafficSkus), summary.lostTrafficSkus ? 'amber' : 'green', RPA.fmtInt(summary.lostTrafficPdp) + ' PDP sessions'],
      ['PDP, never sold', RPA.fmtInt(summary.pdpButNeverSoldSkus), summary.pdpButNeverSoldSkus ? 'amber' : 'green']
    ];
    RPA.renderKpis('sor-kpis', items);

    const notes = [];
    if (summary.trafficProductsNotInOffers) {
      notes.push(RPA.fmtInt(summary.trafficProductsNotInOffers) + ' product(s) in the sales file have no offer in the offers file (their sales are still counted in the totals above).');
    }
    if (summary.unreadableDates) {
      notes.push(RPA.fmtInt(summary.unreadableDates) + ' offer row(s) had an unreadable creation date and were left out of the first-offer calculation.');
    }
    if (summary.approvalProductsNotInOffers) {
      notes.push(RPA.fmtInt(summary.approvalProductsNotInOffers) + ' approved product(s) have no offer in the offers file.');
    }
    notes.push('GMV, AOV and prices were converted from EUR to TRY at 1 EUR = ' + summary.eurTryRate + ' TRY.');
    notes.push('Sales, PDP and funnel figures are summed over ' + summary.salesWindow + ' (the sales file period), not the last 30 days. Unique SKUs sold = products with a sold quantity above zero.');
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

  /** Log-scaled bars: impressions are ~5,800x the order sessions, so a linear bar would hide every later stage. */
  function renderFunnel(f) {
    const stages = [
      ['Impressions', f.impressions], ['Clicks', f.clicks], ['PDP sessions', f.pdp],
      ['Add to cart', f.addToCart], ['Checkout', f.checkout], ['Order sessions', f.orders]
    ].filter(s => s[1] > 0);
    const top = stages.length ? Math.log10(stages[0][1] + 1) : 1;

    el('sor-funnel').innerHTML = stages.map(function (s, i) {
      const width = Math.max(2, Math.round(Math.log10(s[1] + 1) / top * 100));
      const step = i === 0 ? '' : '<small>' + (s[1] / stages[i - 1][1] * 100).toFixed(2) + '% of ' + stages[i - 1][0].toLowerCase() + '</small>';
      return '<div class="sor-funnel-row"><span class="sor-funnel-name">' + RPA.escapeHtml(s[0]) + '</span>' +
        '<span class="sor-funnel-track"><span class="sor-funnel-fill" style="width:' + width + '%"></span></span>' +
        '<span class="sor-funnel-val">' + RPA.fmtInt(s[1]) + step + '</span></div>';
    }).join('');
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

    drawChart('sor-chart-sold', {
      type: 'bar',
      data: {
        labels: data.periods.map(x => x.label),
        datasets: [{ label: 'Unique SKUs sold', data: data.periods.map(x => x.uniqueSkusSold), backgroundColor: p.markGood, borderRadius: 3, maxBarThickness: 48 }]
      },
      options: Object.assign({}, common, {
        plugins: { legend: { display: false }, tooltip: { callbacks: { afterLabel: item => RPA.fmtInt(data.periods[item.dataIndex].qty) + ' items sold' } } },
        scales: { x: cat, y: axis }
      })
    });

    drawChart('sor-chart-gmv', {
      type: 'bar',
      data: {
        labels: data.periods.map(x => x.label),
        datasets: [{ label: 'GMV (TRY)', data: data.periods.map(x => x.gmv), backgroundColor: p.series[0], borderRadius: 3, maxBarThickness: 48 }]
      },
      options: Object.assign({}, common, {
        plugins: { legend: { display: false }, tooltip: { callbacks: { label: item => RPA.fmtInt(item.parsed.y) + ' TRY' } } },
        scales: { x: cat, y: axis }
      })
    });

    renderFunnel(data.funnel);

    drawChart('sor-chart-top', {
      type: 'bar',
      data: {
        labels: data.topTraffic.map(t => t.sku),
        datasets: [{
          label: 'PDP sessions',
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
      if (c.nosort) return '<th scope="col">' + RPA.escapeHtml(c.label) + '</th>';
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

  function fmtPct(v) { return v === null || v === undefined ? '-' : (v * 100).toFixed(2) + '%'; }

  /** One bar per period of the sales file; the hover title carries the exact figures. */
  function trendHtml(qty) {
    if (!qty) return '<span class="sor-na">-</span>';
    const max = qty.reduce((m, v) => Math.max(m, v || 0), 0);
    return '<span class="sor-trend" title="' + RPA.escapeHtml(qty.map(v => v === null ? '-' : RPA.fmtInt(v)).join(' / ')) + '">' +
      qty.map(function (v) {
        const h = max > 0 && v > 0 ? Math.max(12, Math.round(v / max * 100)) : 8;
        return '<span class="' + (v > 0 ? '' : 'is-zero') + '" style="height:' + h + '%"></span>';
      }).join('') + '</span>';
  }

  function rowHtml(r) {
    const meta = [r.brand, r.category].filter(Boolean).map(RPA.escapeHtml).join(' &middot; ');
    const warn = r.warning
      ? '<div class="sor-warn is-' + RPA.escapeHtml(r.severity || 'warning') + '">' + RPA.escapeHtml(r.warning) +
        (r.firstOfferReason ? ' <span class="sor-reason">(' + RPA.escapeHtml(r.firstOfferReason) + ')</span>' : '') + '</div>'
      : '';

    let pdp = '<span class="sor-na" title="Not in the sales file">no data</span>';
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
      '<td class="num"><strong>' + RPA.fmtInt(r.activeOffers) + '</strong><span class="sor-meta"> / ' + RPA.fmtInt(r.totalOffers) + '</span>' +
        sellersHtml(r.activeSellers) + '</td>' +
      '<td class="num">' + (r.hasTraffic ? fmtNum(r.impressions) : '-') + '</td>' +
      '<td class="num sor-pdp">' + pdp + '</td>' +
      '<td class="num">' + fmtPct(r.atcRate) + '</td>' +
      '<td class="num">' + fmtPct(r.crPdp) + '</td>' +
      '<td class="num">' + (r.soldQty > 0 ? '<strong>' + fmtNum(r.soldQty) + '</strong>' : '<span class="sor-na">-</span>') + '</td>' +
      '<td class="num">' + (r.gmv > 0 ? fmtNum(r.gmv) : '<span class="sor-na">-</span>') + '</td>' +
      '<td>' + trendHtml(r.periodQty) + '</td>' +
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
    { key: 'impressions', label: 'Impressions', numeric: true },
    { key: 'pdp', label: 'PDP sessions', numeric: true },
    { key: 'atcRate', label: 'ATC %', numeric: true },
    { key: 'crPdp', label: 'CR %', numeric: true },
    { key: 'sold7d', label: 'Sold (7d)', numeric: true },
    { key: 'gmv7d', label: 'GMV (7d)', numeric: true },
    { key: 'offers', label: 'Offers', numeric: true },
    { key: 'firstDate', label: 'First offer' }
  ];

  function soValue(r, key) {
    switch (key) {
      case 'product': return (r.product || '').toLowerCase();
      case 'pdp': return r.pdpViews === null ? -1 : r.pdpViews;
      case 'impressions': return r.impressions === null ? -1 : r.impressions;
      case 'crPdp': return r.crPdp === null ? -1 : r.crPdp;
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

  /** The stockout file's sellers (a comma separated string): inline when there are one or two, a disclosure beyond that. */
  function soSellersHtml(sellers, retail) {
    const names = sellers ? sellers.split(', ').filter(Boolean) : [];
    if (names.length > 2) return sellersHtml(names) + (retail ? '<div class="sor-meta">' + retail + '</div>' : '');
    return names.length || retail
      ? '<div class="sor-meta">' + RPA.escapeHtml(names.join(', ')) + retail + '</div>'
      : '';
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
        soSellersHtml(r.sellers, retail) + '</td>' +
      '<td class="num">' + (r.hasTraffic ? '<strong>' + fmtNum(r.pdpViews) + '</strong>' : '<span class="sor-na" title="Not in the sales file">no data</span>') + '</td>' +
      '<td class="num">' + (r.hasTraffic ? fmtNum(r.impressions) : '-') + '</td>' +
      '<td class="num">' + fmtPct(r.atcRate) + '</td>' +
      '<td class="num">' + fmtPct(r.crPdp) + '</td>' +
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
      if (c.nosort) return '<th scope="col">' + RPA.escapeHtml(c.label) + '</th>';
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

  function renderStockoutSummary(data, salesWindow) {
    const s = data.summary;
    RPA.renderKpis('sor-so-kpis', [
      ['Stockout products', RPA.fmtInt(s.products), s.products ? 'amber' : 'green', RPA.fmtInt(s.notInOffers) + ' not in offers file'],
      ['Impressions', RPA.fmtInt(s.totalImpressions), ''],
      ['PDP sessions', RPA.fmtInt(s.totalPdpViews), '', s.sharePctOfAllPdp.toFixed(2) + '% of all PDP sessions'],
      ['Add to cart', RPA.fmtInt(s.totalAddToCart), ''],
      ['Sold items (7d)', RPA.fmtInt(s.sold7d), ''],
      ['GMV (7d)', RPA.fmtInt(s.gmv7d), ''],
      ['With PDP data', RPA.fmtInt(s.withTraffic), '', RPA.fmtInt(s.withoutTraffic) + ' without']
    ]);

    const notes = [];
    if (s.withoutTraffic) {
      notes.push(RPA.fmtInt(s.withoutTraffic) + ' of ' + RPA.fmtInt(s.products) +
        ' stockout product(s) are missing from the sales file, so their PDP sessions are unknown (not zero).');
    }
    el('sor-so-window').textContent = 'Impressions, PDP sessions and add to cart cover ' + salesWindow + ' (sales file period); sold items and GMV are the last 7 days (stockout file).';
    el('sor-so-notes').textContent = notes.join(' ');
    el('sor-so-notes').hidden = notes.length === 0;

    if (typeof Chart === 'undefined') return;
    const p = RPA.palette();
    drawChart('sor-chart-stockout', {
      type: 'bar',
      data: {
        labels: data.topPdp.map(t => t.productId),
        datasets: [{ label: 'PDP sessions', data: data.topPdp.map(t => t.pdpViews), backgroundColor: p.markSerious, borderRadius: 3, maxBarThickness: 40 }]
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
    form.append('salesFile', el('sor-sales-file').files[0]);
    const rate = parseRate();
    if (rate === null) { RPA.showError('sor-alert', 'Enter the EUR to TRY exchange rate first.'); return; }
    form.append('eurTryRate', String(rate));
    form.append('approvalsFile', el('sor-approvals-file').files[0]);
    if (el('sor-stockout-file').files.length > 0) form.append('stockoutFile', el('sor-stockout-file').files[0]);

    const button = el('sor-generate');
    RPA.setBusy(button, true, 'Reading files…');
    try {
      const body = await RPA.postJson('/api/sku-offer-report/generate', form);
      state.batchId = body.data.batchId;
      rememberRate(rate);
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
        renderStockoutSummary(body.data.stockout, body.data.summary.salesWindow);
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
    RPA.initDropzone('sor-sales-drop', 'sor-sales-file');
    RPA.initDropzone('sor-approvals-drop', 'sor-approvals-file');
    RPA.initDropzone('sor-stockout-drop', 'sor-stockout-file');

    el('sor-offers-file').addEventListener('change', updateGenerateButton);
    el('sor-sales-file').addEventListener('change', updateGenerateButton);
    el('sor-approvals-file').addEventListener('change', updateGenerateButton);
    el('sor-rate').value = savedRate();
    el('sor-rate').addEventListener('input', updateGenerateButton);
    updateGenerateButton();
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
