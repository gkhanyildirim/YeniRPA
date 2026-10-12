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
    { key: 'activeOffers', label: 'Offers', numeric: true },
    { key: 'pdp', label: 'PDP views (30d)', numeric: true },
    { key: 'atc', label: 'Add to cart', numeric: true },
    { key: 'atcRate', label: 'ATC rate', numeric: true }
  ];

  function el(id) { return document.getElementById(id); }

  function filesChosen() {
    return el('sor-offers-file').files.length > 0 && el('sor-traffic-file').files.length > 0;
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
      ['SKUs with traffic data', RPA.fmtInt(summary.skusWithTraffic), '', RPA.fmtInt(summary.skusWithoutTraffic) + ' without']
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
        (r.firstOfferSeller ? '<div class="sor-meta">' + RPA.escapeHtml(r.firstOfferSeller) + '</div>' : '') + '</td>' +
      '<td class="num"><strong>' + RPA.fmtInt(r.activeOffers) + '</strong><span class="sor-meta"> / ' + RPA.fmtInt(r.totalOffers) + '</span></td>' +
      '<td class="num sor-pdp">' + pdp + '</td>' +
      '<td class="num">' + (r.hasTraffic ? fmtNum(r.addToCart) : '-') + '</td>' +
      '<td class="num">' + (r.atcRate === null || r.atcRate === undefined ? '-' : (r.atcRate * 100).toFixed(1) + '%') + '</td>' +
      '</tr>';
  }

  function queryString(extra) {
    const q = new URLSearchParams({
      batchId: state.batchId, search: state.search, filter: state.filter,
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
  // Wiring
  // ---------------------------------------------------------------------------

  async function generate() {
    RPA.clearError('sor-alert');
    el('sor-results').hidden = true;
    state.batchId = null;

    const form = new FormData();
    form.append('offersFile', el('sor-offers-file').files[0]);
    form.append('trafficFile', el('sor-traffic-file').files[0]);

    const button = el('sor-generate');
    RPA.setBusy(button, true, 'Reading files…');
    try {
      const body = await RPA.postJson('/api/sku-offer-report/generate', form);
      state.batchId = body.data.batchId;
      state.page = 1;
      state.search = '';
      el('sor-search').value = '';

      el('sor-results').hidden = false;
      renderOverview(body.data.summary);
      renderCharts(body.data.charts);
      setFilter('all');
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
        batchId: state.batchId, search: state.search, filter: state.filter, sort: state.sort, desc: state.desc
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

    el('sor-offers-file').addEventListener('change', updateGenerateButton);
    el('sor-traffic-file').addEventListener('change', updateGenerateButton);
    el('sor-generate').addEventListener('click', generate);
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

    el('sor-callout').addEventListener('click', function () {
      setFilter('firstClosed');
      el('sor-table').scrollIntoView({ behavior: RPA.reducedMotion() ? 'auto' : 'smooth', block: 'start' });
    });

    el('sor-thead').addEventListener('click', function (event) {
      const button = event.target.closest('.sor-sort');
      if (!button) return;
      const key = button.dataset.sort;
      if (state.sort === key) state.desc = !state.desc;
      else { state.sort = key; state.desc = key !== 'sku' && key !== 'product'; }
      state.page = 1;
      loadPage();
    });

    el('sor-prev').addEventListener('click', function () { if (state.page > 1) { state.page--; loadPage(); } });
    el('sor-next').addEventListener('click', function () { if (state.page < state.totalPages) { state.page++; loadPage(); } });
  });

})(window.RPA);
