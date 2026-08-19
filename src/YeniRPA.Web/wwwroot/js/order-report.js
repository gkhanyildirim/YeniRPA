/* =============================================================================
   Order Report dashboard — Late Shipment & Cancellation.

   The aggregation below started as a direct port of the dashboard that the old
   app generated server-side into a standalone HTML document. Most rules are
   unchanged: late = shipping date present and later than the deadline; a
   carrier is integrated when its name contains one of the server-supplied
   keywords; the best/worst seller lists require MIN_SAMPLE_SIZE shipped orders.

   One rule was deliberately changed away from the port. "Key metrics" used to
   report *shipped orders*, which counted rows with a shipping date rather than
   a status. The outcome counts on that row — received, rejected, canceled —
   now come from the Status column alone, and each is also shown as a share of
   all order lines. Only the late-shipment rate still uses the narrower
   "lines that have actually shipped" denominator, since a line with no
   shipping date is neither late nor on time.

   Everything under renderExtended() is additive: cancellation/rejection/refund
   breakdown, lead time (SLA) analysis, category performance and data quality.
   Those sections read optional columns, so each one renders an empty state
   naming the column when the upload does not carry it.

   Row fields are the terse names the server emits:
     s   seller            dc  date created (ISO)   st  status
     amt amount            cur currency             sd  shipping deadline
     sh  shipping date     rd  received date        rsn reason
     k   carrier index (into data.carriers; 0 = no shipping company recorded)

   Extended fields are omitted from the JSON when they hold their default, so
   every read has to fall back to 0 / '' rather than assume the key is present:
     ord order number      ac  acceptance date      crs 'A' | 'R' | absent
     crr reason code       lt  lead time (days)     pay transferred to seller
     can canceled amount   ref refunded amount      inv invoice issued
     ci  category index    bi  brand index          yi  city index
   ============================================================================= */

(function (RPA) {
  'use strict';

  // Both thresholds arrive with the payload so the Methodology page and this file cannot disagree;
  // the literals here are only a fallback for a response that predates them.
  let MIN_SAMPLE_SIZE = 3;
  let MIN_LEAD_TIME_SAMPLE = 2;

  let ROWS = [];

  // Carrier groups as the server merged them: { n: name, i: integrated, v: [raw spellings] }.
  // Index 0 is the "no shipping company recorded" slot.
  let CARRIERS = [{ n: '', i: false, v: [] }];

  /** Distinct seller names, for the filter bar's type-ahead list. */
  let SELLERS = [];
  let CANCELED_STATUS = 'Canceled';
  let REFUNDED_STATUS = 'Refunded';
  let RECEIVED_STATUS = 'Received';
  let REJECTED_STATUS = 'Rejected';
  let PENDING_ACCEPTANCE_STATUS = 'Pending acceptance';
  let AUTO_RECEIVED_REASON = 'Received automatically';
  let CATEGORIES = [''];
  let BRANDS = [''];
  let CITIES = [''];
  let REASON_LABELS = {};
  let MISSING_COLUMNS = [];
  const charts = {};

  // ---------------------------------------------------------------------------
  // Rules
  // ---------------------------------------------------------------------------

  /**
   * The carrier of a line, already merged across the spellings the sellers typed. The keyword rule
   * that decides Integrated vs. Manual now runs once per carrier on the server, against the
   * canonical name — so one carrier can no longer carry both badges because someone wrote it two
   * ways.
   */
  const carrierOf = r => CARRIERS[r.k || 0] || CARRIERS[0];
  const carrierName = r => carrierOf(r).n;
  const isIntegratedCarrier = r => carrierOf(r).i;

  function groupBy(arr, keyFn) {
    const m = new Map();
    arr.forEach(item => {
      const k = keyFn(item);
      if (!m.has(k)) m.set(k, []);
      m.get(k).push(item);
    });
    return m;
  }

  const avg = arr => (arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0);
  const sum = (arr, fn) => arr.reduce((a, r) => a + (fn(r) || 0), 0);

  /** Linear-interpolated percentile. Used for lead-time advice, where the mean hides slow days. */
  function percentile(values, p) {
    const a = values.filter(v => v !== null && v !== undefined && !isNaN(v)).slice().sort((x, y) => x - y);
    if (!a.length) return null;
    const i = (a.length - 1) * p;
    const lo = Math.floor(i);
    const hi = Math.ceil(i);
    return lo === hi ? a[lo] : a[lo] + (a[hi] - a[lo]) * (i - lo);
  }

  const hoursBetween = (fromIso, toIso) => (new Date(toIso) - new Date(fromIso)) / 3.6e6;

  /**
   * A line closed as "Received automatically" carries a bulk system timestamp instead of a real
   * delivery date, so it is excluded from every delivery-duration metric in the extended sections.
   */
  const isAutoReceived = r => r.rsn === AUTO_RECEIVED_REASON;

  const label = (dict, index) => dict[index || 0] || '—';

  /**
   * Cancel / reject / refund are three different outcomes that the Status column alone cannot tell
   * apart: a canceled line may or may not have started as a customer request, and a rejected request
   * leaves the line looking perfectly normal. Crossing Status with the request outcome separates
   * them, which is what makes seller-caused cancellations visible.
   */
  const OUTCOME_CLEAN = 1;
  const OUTCOME_CUSTOMER_CANCEL = 2;
  const OUTCOME_SELLER_CANCEL = 3;
  const OUTCOME_REQUEST_REJECTED = 4;
  const OUTCOME_REFUND = 5;

  const OUTCOMES = [
    { id: OUTCOME_CLEAN, label: 'Completed normally', tone: '',
      meaning: 'No cancellation request, no refund.' },
    { id: OUTCOME_CUSTOMER_CANCEL, label: 'Customer cancellation', tone: 'amber',
      meaning: 'Customer opened a request and it was accepted.' },
    { id: OUTCOME_SELLER_CANCEL, label: 'Seller / operator cancellation', tone: 'red',
      meaning: 'Canceled with no customer request on record — stock-out or pricing error.' },
    { id: OUTCOME_REQUEST_REJECTED, label: 'Cancellation request rejected', tone: 'green',
      meaning: 'Customer asked to cancel, request was refused, the order went through.' },
    { id: OUTCOME_REFUND, label: 'Refunded after delivery', tone: 'red',
      meaning: 'Goods reached the customer and the money was paid back.' }
  ];

  function outcomeOf(r) {
    if (r.st === REFUNDED_STATUS) return OUTCOME_REFUND;
    if (r.st === CANCELED_STATUS) return r.crs === 'A' ? OUTCOME_CUSTOMER_CANCEL : OUTCOME_SELLER_CANCEL;
    if (r.crs === 'R') return OUTCOME_REQUEST_REJECTED;
    return OUTCOME_CLEAN;
  }

  /** True when the named optional column was absent from the upload. */
  const columnMissing = name => MISSING_COLUMNS.indexOf(name) !== -1;

  function emptyBecauseMissing(wrapperId, columns) {
    const absent = columns.filter(columnMissing);
    if (!absent.length) return false;
    RPA.registerExport(wrapperId, null);
    const el = document.getElementById(wrapperId);
    if (el) {
      el.innerHTML = '<div class="empty-state">Not available — the upload has no ' +
        absent.map(c => '<code>' + RPA.escapeHtml(c) + '</code>').join(' or ') + ' column.</div>';
      el.style.border = 'none';
    }
    return true;
  }

  // ---------------------------------------------------------------------------
  // Charts
  // ---------------------------------------------------------------------------

  function destroyCharts() {
    Object.keys(charts).forEach(id => {
      if (charts[id]) { charts[id].destroy(); charts[id] = null; }
    });
  }

  /**
   * A chart exports as the table it was drawn from, so a card whose only content is a canvas still
   * has something to download. `extra` adds one more column — the rate printed beside the bars.
   */
  function registerChartExport(id, labelHeader, valueHeader, labels, data, extra) {
    const columns = [{ label: labelHeader }, { label: valueHeader, numeric: true }];
    if (extra) columns.push({ label: extra.header, numeric: true });

    RPA.registerExport(id, {
      columns,
      rows: labels.map((label, i) => (extra ? [label, data[i], extra.values[i]] : [label, data[i]]))
    });
  }

  /**
   * Horizontal bar chart. `rates` turns on the value labels: each bar is annotated with its count
   * and the rate that count came from, because "95 late" says nothing until you know whether it is
   * 95 out of 100 or 95 out of 10,000.
   *
   * rates = { values: [0.124, …], denominators: [766, …], header: 'Late shipment rate',
   *           noun: 'late', denominatorNoun: 'shipped' }
   */
  function hBar(id, labels, data, color, labelHeader, valueHeader, rates) {
    registerChartExport(id, labelHeader, valueHeader, labels, data,
      rates ? { header: rates.header, values: rates.values.map(v => +(v * 100).toFixed(1)) } : null);

    const p = RPA.palette();
    const barLabels = rates ? data.map((v, i) => RPA.fmtInt(v) + ' · ' + RPA.fmtPct(rates.values[i])) : null;

    charts[id] = new Chart(document.getElementById(id), {
      type: 'bar',
      data: { labels, datasets: [{ data, backgroundColor: color, borderRadius: 4, maxBarThickness: 26 }] },
      plugins: barLabels ? [RPA.barLabelPlugin] : [],
      options: {
        indexAxis: 'y', maintainAspectRatio: false, responsive: true,
        // The labels are painted past the end of the longest bar, so the plot area has to give
        // that space back or they are drawn off-canvas.
        layout: barLabels ? { padding: { right: 96 } } : {},
        plugins: {
          legend: { display: false },
          rpaBarLabels: { labels: barLabels },
          tooltip: rates ? {
            callbacks: {
              label: ctx => RPA.fmtInt(ctx.parsed.x) + ' ' + rates.noun + ' of ' +
                RPA.fmtInt(rates.denominators[ctx.dataIndex]) + ' ' + rates.denominatorNoun +
                ' — ' + RPA.fmtPct(rates.values[ctx.dataIndex])
            }
          } : {}
        },
        scales: {
          x: { beginAtZero: true, grid: { color: p.line }, border: { display: false } },
          y: { grid: { display: false }, border: { display: false } }
        }
      }
    });
  }

  /**
   * Doughnut with the total printed in the hole and an HTML legend under it.
   *
   * `slices` are `{ label, value, color }`. A null colour takes the next categorical slot — that is
   * how the status mix gets identity colours, while the outcome mix passes its own semantic ones
   * (a seller cancellation is not "series 3", it is a bad outcome, and it stays red).
   */
  function doughnut(id, slices, options) {
    const opts = options || {};
    const p = RPA.palette();
    const shown = slices.filter(s => s.value > 0);
    const colors = shown.map((s, i) => s.color || p.series[i % p.series.length]);
    const total = shown.reduce((sum, s) => sum + s.value, 0);

    registerChartExport(id, opts.labelHeader || 'Label', opts.valueHeader || 'Order lines',
      shown.map(s => s.label), shown.map(s => s.value));

    if (opts.legend) {
      RPA.chartLegend(opts.legend, shown.map((s, i) => ({
        label: s.label,
        color: colors[i],
        value: RPA.fmtInt(s.value) + ' · ' + RPA.fmtPct(total ? s.value / total : 0)
      })));
    }

    return new Chart(document.getElementById(id), {
      type: 'doughnut',
      data: {
        labels: shown.map(s => s.label),
        datasets: [{
          data: shown.map(s => s.value),
          backgroundColor: colors,
          // A 2px ring in the surface colour keeps neighbouring arcs from bleeding into each other.
          borderColor: p.surface,
          borderWidth: 2,
          hoverOffset: 5
        }]
      },
      plugins: [RPA.doughnutCenterPlugin],
      options: {
        maintainAspectRatio: false, responsive: true, cutout: '62%',
        plugins: {
          legend: { display: false },
          rpaDoughnutCenter: {
            value: RPA.fmtInt(opts.total === undefined ? total : opts.total),
            label: opts.totalLabel
          },
          tooltip: {
            callbacks: {
              label: ctx => ' ' + RPA.fmtInt(ctx.parsed) + ' · ' +
                RPA.fmtPct(total ? ctx.parsed / total : 0)
            }
          }
        }
      }
    });
  }

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  /**
   * The lists stay ranked by on-time *rate* — the rate is simply not printed, because
   * "296/296 on time" already says it and the card's coloured edge carries the verdict.
   */
  function renderSellerPerfList(elId, list) {
    const el = document.getElementById(elId);
    RPA.registerExport(elId, {
      columns: [{ label: 'Seller' }, { label: 'On-time lines', numeric: true }, { label: 'Shipped lines', numeric: true }],
      rows: list.map(x => [x.seller, x.onTimeCount, x.shippedCount])
    });

    if (!list.length) {
      el.innerHTML = '<div class="empty-state">Not enough data (min. ' + MIN_SAMPLE_SIZE +
        ' shipped orders per seller).</div>';
      return;
    }
    el.innerHTML = list.map(x =>
      '<li>' +
        '<span class="name" title="' + RPA.escapeHtml(x.seller) + '">' + RPA.escapeHtml(x.seller) + '</span>' +
        '<span class="meta">' + x.onTimeCount + '/' + x.shippedCount + ' on time</span>' +
      '</li>'
    ).join('');
  }

  /**
   * How the volume splits across the shipping companies. Counted over *every* order line rather than
   * the delivered ones the chart beside it measures — a parcel still in transit was carried all the
   * same. Lines with no shipping company keep a row of their own so the counts add up to the total.
   */
  function renderCarrierVolume(rows) {
    // Lines with no shipping company are not a carrier, so they are neither a row nor part of the
    // denominator — the shares are read as "of the lines that name a carrier" and add up to 100%.
    // The count is printed under the table instead, so it is left out but not lost.
    const recorded = rows.filter(r => r.k);
    const missing = rows.length - recorded.length;
    const total = recorded.length;

    const carriers = [...groupBy(recorded, r => r.k).entries()]
      .map(([key, list]) => {
        const carrier = CARRIERS[key] || CARRIERS[0];
        return {
          carrier: carrier.n,
          variants: carrier.v || [],
          count: list.length,
          share: total ? list.length / total : 0,
          integrated: carrier.i
        };
      })
      .sort((a, b) => b.count - a.count);

    const note = document.getElementById('carrier-volume-note');
    if (note) {
      note.hidden = missing === 0;
      note.textContent = missing
        ? RPA.fmtInt(missing) + ' order line' + (missing === 1 ? '' : 's') +
          ' name no shipping company and are left out of this table; shares are taken over the ' +
          RPA.fmtInt(total) + ' lines that do.'
        : '';
    }

    RPA.renderDataTable('carrier-volume-wrap', carriers, [
      // The spellings that folded into one carrier stay on the cell's tooltip: the merge remains
      // checkable without a badge on every second row.
      { label: 'Carrier', filter: 'text', value: x => x.carrier, render: x =>
          '<span' + (x.variants.length > 1
            ? ' title="' + RPA.escapeHtml(x.variants.join(' · ')) + '"'
            : '') + '>' + RPA.escapeHtml(x.carrier) + '</span>' },
      { label: 'Order lines', numeric: true, filter: 'number', value: x => x.count,
        render: x => RPA.fmtInt(x.count) },
      { label: 'Share', numeric: true, value: x => +(x.share * 100).toFixed(1),
        render: x => RPA.fmtPct(x.share) },
      { label: 'Delivery reporting', filter: 'select',
        value: x => (x.integrated ? 'Integrated' : 'Manual'),
        render: x => x.integrated
          ? '<span class="badge green">Integrated</span>'
          : '<span class="badge amber">Manual</span>' }
    ], 'No order line in the selected range names a shipping company.');
  }

  function computeAndRender(rows) {
    const p = RPA.palette();

    const shippedRows = rows.filter(r => r.sh);
    const totalLines = rows.length;
    const shipped = shippedRows.length;
    const lateShippedRows = shippedRows.filter(r => new Date(r.sh) > new Date(r.sd));
    const lateShipped = lateShippedRows.length;
    const lateRate = shipped ? lateShipped / shipped : 0;
    const canceledRows = rows.filter(r => r.st === CANCELED_STATUS);
    const canceled = canceledRows.length;
    const cancelRate = totalLines ? canceled / totalLines : 0;

    // Received and rejected are read from Status alone — no date column stands in for them. Their
    // denominator is every line, the same rule the cancellation rate already uses; only the
    // late-shipment rate keeps its narrower "lines that have actually shipped" denominator.
    const received = rows.filter(r => r.st === RECEIVED_STATUS).length;
    const rejected = rows.filter(r => r.st === REJECTED_STATUS).length;
    const receivedRate = totalLines ? received / totalLines : 0;
    const rejectedRate = totalLines ? rejected / totalLines : 0;

    // Acceptance is measured over the lines the seller has actually decided on: a line still
    // "Pending acceptance" has not been accepted, but counting it against the seller would mark
    // every fresh batch of orders down as a refusal that has not happened.
    const pendingAcceptance = rows.filter(r => r.st === PENDING_ACCEPTANCE_STATUS).length;
    const decided = totalLines - pendingAcceptance;
    const acceptanceRate = decided ? (decided - rejected) / decided : 0;

    const shipDurations = rows.filter(r => r.dc && r.sh).map(r => (new Date(r.sh) - new Date(r.dc)) / 3.6e6);
    const avgHoursToShip = shipDurations.length ? shipDurations.reduce((a, b) => a + b, 0) / shipDurations.length : 0;

    const receivedRows = rows.filter(r => r.sh && r.rd);
    const integratedDurations = receivedRows.filter(isIntegratedCarrier).map(r => (new Date(r.rd) - new Date(r.sh)) / 3.6e6);
    const manualDurations = receivedRows.filter(r => !isIntegratedCarrier(r)).map(r => (new Date(r.rd) - new Date(r.sh)) / 3.6e6);
    const avgHoursReceiveIntegrated = avg(integratedDurations);
    const avgHoursReceiveManual = avg(manualDurations);

    const currencyCounts = {};
    rows.forEach(r => { if (r.cur) currencyCounts[r.cur] = (currencyCounts[r.cur] || 0) + 1; });
    const sortedCurrencies = Object.entries(currencyCounts).sort((a, b) => b[1] - a[1]);
    const primaryCurrency = sortedCurrencies.length ? sortedCurrencies[0][0] : '';

    const trendMap = {};
    rows.forEach(r => { if (r.dc) { const d = r.dc.slice(0, 10); trendMap[d] = (trendMap[d] || 0) + 1; } });
    const trendEntries = Object.entries(trendMap).sort((a, b) => a[0].localeCompare(b[0]));

    const sellerCount = new Set(rows.map(r => r.s).filter(Boolean)).size;

    // The figures the report exists to answer, set large above everything else. They are the same
    // numbers as the twelve key metrics below — the whole list still travels to Excel through
    // `exportRows`, so the workbook and the Summary sheet keep their nine boxes either way.
    //
    // The four rates read as one sequence: how many orders the sellers took, how many fell over
    // afterwards, and how many of the survivors shipped late. The order-lines-per-day sparkline that
    // used to sit beside them is gone — the same trend has its own chart further down the page.
    RPA.renderHero('order-hero', [
      { value: RPA.fmtInt(totalLines), label: 'Order lines',
        context: RPA.fmtInt(sellerCount) + ' seller' + (sellerCount === 1 ? '' : 's') + ' · ' +
                 RPA.fmtInt(shipped) + ' shipped' },
      { value: RPA.fmtPct(acceptanceRate), label: 'Acceptance rate',
        tone: acceptanceRate >= 0.99 ? 'green' : acceptanceRate >= 0.95 ? 'amber' : 'red',
        context: RPA.fmtInt(decided - rejected) + ' of ' + RPA.fmtInt(decided) + ' decided lines' },
      // Above 5% is where these stop being background noise and start being a problem worth opening
      // the report for, which is the only reason they are ever painted red.
      { value: RPA.fmtPct(cancelRate), label: 'Cancellation rate', tone: cancelRate > 0.05 ? 'red' : 'green',
        context: RPA.fmtInt(canceled) + ' of ' + RPA.fmtInt(totalLines) + ' lines' },
      // Any refusal at all is worth seeing: a rejected line is an order the customer placed and did
      // not get, so nought is the only good number and the only green one.
      { value: RPA.fmtPct(rejectedRate), label: 'Rejection rate', tone: rejected > 0 ? 'red' : 'green',
        context: RPA.fmtInt(rejected) + ' of ' + RPA.fmtInt(totalLines) + ' lines' },
      { value: RPA.fmtPct(lateRate), label: 'Late shipment rate', tone: lateRate > 0.05 ? 'red' : 'green',
        context: RPA.fmtInt(lateShipped) + ' of ' + RPA.fmtInt(shipped) + ' shipped lines' }
    ]);

    // A tile only turns red when there is something red about it: nought rejected lines is a good
    // result, and painting the zero red would tell the opposite story at a glance.
    const badIf = count => (count > 0 ? 'red' : 'green');

    RPA.renderKpis('order-kpis', [
      { group: 'Outcome — read from the Status column alone' },
      ['Received orders', RPA.fmtInt(received), 'green', RPA.fmtPct(receivedRate) + ' of all lines'],
      ['Rejected orders', RPA.fmtInt(rejected), badIf(rejected), RPA.fmtPct(rejectedRate) + ' of all lines'],
      ['Rejection rate', RPA.fmtPct(rejectedRate), badIf(rejected), 'over every order line'],
      { group: 'Shipping deadline & cancellations' },
      ['Late shipped', RPA.fmtInt(lateShipped), badIf(lateShipped), 'of ' + RPA.fmtInt(shipped) + ' shipped lines'],
      ['Canceled orders', RPA.fmtInt(canceled), badIf(canceled), RPA.fmtPct(cancelRate) + ' of all lines'],
      { group: 'Speed' },
      ['Avg. hours to ship', RPA.fmtHours(avgHoursToShip), '', 'created → shipped'],
      ['Avg. hours to receive (integrated)', RPA.fmtHours(avgHoursReceiveIntegrated), 'green',
        'carriers that report delivery'],
      ['Avg. hours to receive (manual)', RPA.fmtHours(avgHoursReceiveManual), 'red',
        'delivery ticked by hand']
    ], {
      exportRows: [
        ['Total order lines', RPA.fmtInt(totalLines)],
        ['Received orders', RPA.fmtInt(received)],
        ['Received rate', RPA.fmtPct(receivedRate)],
        ['Rejected orders', RPA.fmtInt(rejected)],
        ['Rejection rate', RPA.fmtPct(rejectedRate)],
        ['Late shipped', RPA.fmtInt(lateShipped)],
        ['Late shipment rate', RPA.fmtPct(lateRate)],
        ['Canceled orders', RPA.fmtInt(canceled)],
        ['Cancellation rate', RPA.fmtPct(cancelRate)],
        ['Avg. hours to ship', RPA.fmtHours(avgHoursToShip)],
        ['Avg. hours to receive (integrated)', RPA.fmtHours(avgHoursReceiveIntegrated)],
        ['Avg. hours to receive (manual)', RPA.fmtHours(avgHoursReceiveManual)]
      ]
    });

    // Status distribution
    const statusMap = {};
    rows.forEach(r => { if (r.st) statusMap[r.st] = (statusMap[r.st] || 0) + 1; });
    const statusEntries = Object.entries(statusMap).sort((a, b) => b[1] - a[1]);

    // Both top-5 lists carry the denominator their rate is taken over: the seller's own shipped
    // lines for late, the seller's own order lines for canceled — the same two denominators the
    // Key metrics row uses, so a bar and a KPI can never disagree.
    const top5Late = [...groupBy(shippedRows, r => r.s).entries()]
      .map(([seller, list]) => ({
        seller, total: list.length,
        count: list.filter(r => new Date(r.sh) > new Date(r.sd)).length
      }))
      .filter(x => x.count > 0).sort((a, b) => b.count - a.count).slice(0, 5);

    const top5Canceled = [...groupBy(rows, r => r.s).entries()]
      .map(([seller, list]) => ({
        seller, total: list.length,
        count: list.filter(r => r.st === CANCELED_STATUS).length
      }))
      .filter(x => x.count > 0).sort((a, b) => b.count - a.count).slice(0, 5);

    const carrierGroups = [...groupBy(receivedRows.filter(r => r.k), r => r.k).entries()]
      .map(([key, list]) => ({
        carrier: CARRIERS[key].n, count: list.length,
        avgHours: avg(list.map(r => (new Date(r.rd) - new Date(r.sh)) / 3.6e6)),
        integrated: CARRIERS[key].i
      }))
      .sort((a, b) => b.count - a.count).slice(0, 8)
      .sort((a, b) => a.avgHours - b.avgHours);

    // Seller shipping-deadline performance (best & worst)
    const sellerShipGroups = [...groupBy(shippedRows, r => r.s).entries()]
      .map(([seller, list]) => {
        const shippedCount = list.length;
        const onTimeCount = list.filter(r => new Date(r.sh) <= new Date(r.sd)).length;
        return { seller, shippedCount, onTimeCount, onTimeRate: shippedCount ? onTimeCount / shippedCount : 0 };
      })
      .filter(x => x.shippedCount >= MIN_SAMPLE_SIZE);

    const bestSellers = [...sellerShipGroups].sort((a, b) => b.onTimeRate - a.onTimeRate || b.shippedCount - a.shippedCount).slice(0, 5);
    const worstSellers = [...sellerShipGroups].sort((a, b) => a.onTimeRate - b.onTimeRate || b.shippedCount - a.shippedCount).slice(0, 5);

    renderSellerPerfList('best-sellers-list', bestSellers);
    renderSellerPerfList('worst-sellers-list', worstSellers);

    destroyCharts();
    RPA.applyChartDefaults();

    // The trend exports with full dates; the axis only shows MM-DD because it has to fit.
    registerChartExport('trendChart', 'Date', 'Order lines',
      trendEntries.map(e => e[0]), trendEntries.map(e => e[1]));

    charts.trendChart = new Chart(document.getElementById('trendChart'), {
      type: 'line',
      data: {
        labels: trendEntries.map(e => e[0].slice(5)),
        datasets: [{
          label: 'Order lines',
          data: trendEntries.map(e => e[1]),
          borderColor: p.series[0],
          // Painted from the plot area's own geometry, so the fill fades out at the baseline
          // instead of sitting on the axis as a block of colour.
          backgroundColor: context => {
            const area = context.chart.chartArea;
            if (!area) return 'transparent';
            const gradient = context.chart.ctx.createLinearGradient(0, area.top, 0, area.bottom);
            gradient.addColorStop(0, RPA.alpha(p.series[0], .30));
            gradient.addColorStop(1, RPA.alpha(p.series[0], 0));
            return gradient;
          },
          fill: true, tension: .32, pointRadius: 0, pointHoverRadius: 4, borderWidth: 2
        }]
      },
      options: {
        maintainAspectRatio: false, responsive: true,
        plugins: { legend: { display: false } },
        interaction: { mode: 'index', intersect: false },
        scales: {
          y: {
            beginAtZero: true,
            grid: { color: p.line, drawTicks: false },
            border: { display: false, dash: [3, 3] },
            ticks: { padding: 8, maxTicksLimit: 5 }
          },
          x: {
            grid: { display: false },
            border: { display: false },
            ticks: { maxRotation: 0, autoSkipPadding: 20, padding: 6 }
          }
        }
      }
    });

    registerChartExport('statusChart', 'Status', 'Order lines',
      statusEntries.map(e => e[0]), statusEntries.map(e => e[1]));

    charts.statusChart = doughnut('statusChart', statusEntries.map(e => ({
      label: e[0], value: e[1], color: null
    })), { total: totalLines, totalLabel: 'order lines', legend: 'statusChart-legend' });

    hBar('carrierChart', carrierGroups.map(c => c.carrier), carrierGroups.map(c => +c.avgHours.toFixed(1)),
      // Integrated vs. manual is a property of the carrier, not a ranking: the series colour marks
      // the ones that report delivery themselves, amber marks the ones somebody has to tick.
      carrierGroups.map(c => (c.integrated ? p.series[0] : p.markSerious)), 'Carrier', 'Avg. hours to receive');
    renderCarrierVolume(rows);
    hBar('lateChart', top5Late.map(x => x.seller), top5Late.map(x => x.count), p.markCritical,
      'Seller', 'Late shipped lines', {
        header: 'Late shipment rate %',
        values: top5Late.map(x => (x.total ? x.count / x.total : 0)),
        denominators: top5Late.map(x => x.total),
        noun: 'late', denominatorNoun: 'shipped lines'
      });
    hBar('cancelChart', top5Canceled.map(x => x.seller), top5Canceled.map(x => x.count), p.markSerious,
      'Seller', 'Canceled lines', {
        header: 'Cancellation rate %',
        values: top5Canceled.map(x => (x.total ? x.count / x.total : 0)),
        denominators: top5Canceled.map(x => x.total),
        noun: 'canceled', denominatorNoun: 'order lines'
      });

    renderExtended(rows, primaryCurrency, p);
    RPA.syncExportButtons();
  }

  // ---------------------------------------------------------------------------
  // Extended sections. Purely additive — nothing here feeds the ported metrics
  // above, so the original numbers cannot drift when this changes.
  // ---------------------------------------------------------------------------

  function renderDeliveryQuality(rows, cur) {
    const shipped = rows.filter(r => r.sh);
    const autoClosed = rows.filter(isAutoReceived);
    const realDeliveries = rows.filter(r => r.sh && r.rd && !isAutoReceived(r));

    const transit = realDeliveries.map(r => hoursBetween(r.sh, r.rd));
    const cycle = realDeliveries.filter(r => r.dc).map(r => hoursBetween(r.dc, r.rd));
    const autoTransit = autoClosed.filter(r => r.sh && r.rd).map(r => hoursBetween(r.sh, r.rd));

    const onTime = shipped.filter(r => new Date(r.sh) <= new Date(r.sd)).length;
    const accepted = rows.filter(r => r.dc && r.ac).map(r => hoursBetween(r.dc, r.ac));

    RPA.renderKpis('delivery-kpis', [
      ['On-time shipment rate', shipped.length ? RPA.fmtPct(onTime / shipped.length) : '-',
        shipped.length && onTime / shipped.length >= 0.95 ? 'green' : 'red'],
      ['Avg. transit (excl. auto-closed)', transit.length ? (avg(transit) / 24).toFixed(2) + ' days' : '-', ''],
      ['Avg. order-to-delivery', cycle.length ? (avg(cycle) / 24).toFixed(2) + ' days' : '-', ''],
      ['Measured deliveries', RPA.fmtInt(realDeliveries.length), ''],
      ['Auto-closed lines', RPA.fmtInt(autoClosed.length), autoClosed.length ? 'amber' : ''],
      // The auto-closed cohort measured on its own, *not* the blended average the two sets would
      // produce together — the point is how far apart this sits from the card next to it.
      ['Avg. transit on auto-closed lines',
        autoTransit.length ? (avg(autoTransit) / 24).toFixed(1) + ' days' : '-', 'red'],
      ['Avg. acceptance time', accepted.length ? RPA.fmtHours(avg(accepted)) : '-', ''],
      ['Invoice coverage', rows.length ? RPA.fmtPct(rows.filter(r => r.inv).length / rows.length) : '-',
        rows.length && rows.filter(r => r.inv).length / rows.length < 0.8 ? 'red' : 'green']
    ]);
  }

  function renderCancellationBreakdown(rows, cur, p) {
    const total = rows.length;
    const byOutcome = OUTCOMES.map(o => {
      const list = rows.filter(r => outcomeOf(r) === o.id);
      return {
        outcome: o,
        count: list.length,
        share: total ? list.length / total : 0
      };
    });

    RPA.renderTable('cancel-class-wrap', byOutcome, [
      { label: 'Outcome', render: x =>
          '<span class="badge ' + x.outcome.tone + '">' + RPA.escapeHtml(x.outcome.label) + '</span>' },
      { label: 'What it means', render: x => RPA.escapeHtml(x.outcome.meaning) },
      { label: 'Lines', numeric: true, value: x => x.count, render: x => RPA.fmtInt(x.count) },
      { label: 'Share', numeric: true, value: x => +(x.share * 100).toFixed(1),
        render: x => RPA.fmtPct(x.share) }
    ], 'No order lines in the selected range.');

    // Semantic colours, not categorical ones: these five slices are outcomes with a verdict
    // attached, and the verdict is what the colour has to carry.
    charts.cancelClassChart = doughnut('cancelClassChart', byOutcome.map(x => ({
      label: x.outcome.label,
      value: x.count,
      color: x.outcome.tone === 'red' ? p.markCritical
        : x.outcome.tone === 'amber' ? p.markSerious
        : x.outcome.tone === 'green' ? p.markGood
        : p.series[0]
    })), { labelHeader: 'Outcome', total, totalLabel: 'order lines', legend: 'cancelClassChart-legend' });

    // Reason codes
    if (!emptyBecauseMissing('cancel-reason-wrap', ['Cancellation Request Payload'])) {
      const requested = rows.filter(r => r.crr);
      const byReason = [...groupBy(requested, r => r.crr).entries()]
        .map(([code, list]) => {
          const accepted = list.filter(r => r.crs === 'A').length;
          const meta = REASON_LABELS[code];
          return {
            code,
            label: meta ? meta.label : code,
            action: meta ? meta.action : 'Reason code not in the known list — confirm it on the platform.',
            count: list.length,
            accepted,
            rejected: list.filter(r => r.crs === 'R').length,
            acceptRate: list.length ? accepted / list.length : 0,
            amount: sum(list, r => (r.can || 0) + (r.ref || 0) + (r.amt || 0))
          };
        })
        .sort((a, b) => b.count - a.count);

      RPA.renderTable('cancel-reason-wrap', byReason, [
        { label: 'Code', render: x => '<span class="badge">' + RPA.escapeHtml(x.code) + '</span>' },
        { label: 'Reason', render: x => RPA.escapeHtml(x.label) },
        { label: 'Requests', numeric: true, value: x => x.count, render: x => RPA.fmtInt(x.count) },
        { label: 'Accepted', numeric: true, value: x => x.accepted, render: x => RPA.fmtInt(x.accepted) },
        { label: 'Rejected', numeric: true, value: x => x.rejected, render: x => RPA.fmtInt(x.rejected) },
        { label: 'Accept rate', numeric: true, value: x => +(x.acceptRate * 100).toFixed(1),
          render: x => RPA.fmtPct(x.acceptRate) },
        { label: 'Suggested action', render: x => RPA.escapeHtml(x.action) }
      ], 'No cancellation requests in the selected range.');
    }

    // Detail list — everything that did not complete cleanly, worst money first. The 200-row cap is
    // applied by the table *after* its own filters, so searching for a seller here searches every
    // line that did not complete cleanly, not just the 200 costliest.
    const detail = rows
      .filter(r => outcomeOf(r) !== OUTCOME_CLEAN)
      .map(r => ({ r, lost: (r.can || 0) + (r.ref || 0) }))
      .sort((a, b) => b.lost - a.lost);

    RPA.renderDataTable('cancel-detail-wrap', detail, [
      { label: 'Order', filter: 'text', value: x => x.r.ord || '—',
        render: x => RPA.escapeHtml(x.r.ord || '—') },
      { label: 'Seller', filter: 'text', value: x => x.r.s, render: x => RPA.escapeHtml(x.r.s) },
      { label: 'Outcome', filter: 'select', render: x => {
          const o = OUTCOMES.find(v => v.id === outcomeOf(x.r));
          return '<span class="badge ' + o.tone + '">' + RPA.escapeHtml(o.label) + '</span>';
        } },
      { label: 'Category', filter: 'text', value: x => label(CATEGORIES, x.r.ci),
        render: x => RPA.escapeHtml(label(CATEGORIES, x.r.ci)) },
      { label: 'Reason', filter: 'select', render: x => {
          if (!x.r.crr) return '—';
          const meta = REASON_LABELS[x.r.crr];
          return RPA.escapeHtml(meta ? meta.label : x.r.crr);
        } },
      { label: 'Already shipped?', filter: 'select', render: x => x.r.sh
          ? '<span class="badge amber">Yes — return leg cost</span>'
          : 'No' }
    ], 'Every order line completed normally in the selected range.', { maxRows: 200 });
  }

  function renderLeadTime(rows, cur) {
    if (emptyBecauseMissing('lt-opportunity-wrap', ['Lead time to ship'])) return;

    const shipped = rows.filter(r => r.sh && r.dc);

    // One row per seller, measured over *all* of their shipped lines. A seller can promise 2 days on
    // accessories and 15 on white goods, so the promise is the line-weighted average of what they
    // actually committed to, and the spread is printed next to it — a wide range means the single
    // suggestion below has to be taken per product group rather than applied across the board.
    const opportunities = [...groupBy(shipped, r => r.s).values()]
      .map(list => {
        // Reduced rather than spread into Math.min: a dominant seller in a large export can hold
        // more lines than the argument limit allows.
        const promises = list.map(r => Number(r.lt || 0));
        const promised = avg(promises);
        const hours = list.map(r => hoursBetween(r.dc, r.sh));
        const p90 = percentile(hours, 0.9);
        const suggested = Math.max(0, Math.ceil(p90 / 24));
        return {
          seller: list[0].s,
          promised,
          minLt: promises.reduce((a, b) => Math.min(a, b)),
          maxLt: promises.reduce((a, b) => Math.max(a, b)),
          count: list.length,
          avgHours: avg(hours), p90,
          utilisation: promised > 0 ? avg(hours) / (promised * 24) : null,
          suggested, gain: promised - suggested
        };
      })
      .filter(x => x.count >= MIN_LEAD_TIME_SAMPLE && x.gain >= 1)
      // Ranked by shipped lines, so the sellers whose promise affects the most order lines come
      // first. Equal volumes fall back to the size of the opportunity — both keys are columns that
      // are actually on screen.
      .sort((a, b) => b.count - a.count || b.gain - a.gain);

    const promiseRange = x => (x.minLt === x.maxLt ? x.minLt + ' d' : x.minLt + '–' + x.maxLt + ' d');

    RPA.renderDataTable('lt-opportunity-wrap', opportunities, [
      { label: 'Seller', filter: 'text', value: x => x.seller, render: x => RPA.escapeHtml(x.seller) },
      { label: 'Promised (avg.)', numeric: true, filter: 'number', value: x => +x.promised.toFixed(1),
        render: x => x.promised.toFixed(1) + ' d' },
      // Sorted and read, never filtered: the cell is a span ("2–15 d"), not a figure to compare.
      { label: 'Promise range', numeric: true, value: promiseRange, render: promiseRange },
      { label: 'Shipped lines', numeric: true, filter: 'number', value: x => x.count,
        render: x => RPA.fmtInt(x.count) },
      { label: 'Avg. to ship', numeric: true, filter: 'number', value: x => +x.avgHours.toFixed(1),
        render: x => RPA.fmtHours(x.avgHours) },
      { label: '90th pct.', numeric: true, filter: 'number', value: x => +x.p90.toFixed(1),
        render: x => RPA.fmtHours(x.p90) },
      { label: 'SLA usage', numeric: true, filter: 'number', value: x => +(x.utilisation * 100).toFixed(1),
        render: x => '<span class="badge green">' + RPA.fmtPct(x.utilisation) + '</span>' },
      { label: 'Suggested', numeric: true, filter: 'number', value: x => x.suggested,
        render: x => x.suggested + ' d' },
      { label: 'Days to gain', numeric: true, filter: 'number', value: x => +x.gain.toFixed(1),
        render: x => '<span class="badge green">−' + x.gain.toFixed(1) + ' d</span>' }
    ], 'No seller is shipping a full day earlier than promised (minimum ' +
       MIN_LEAD_TIME_SAMPLE + ' shipped lines per seller).');
  }

  function renderCategories(rows, cur, p) {
    if (columnMissing('Category label')) {
      ['categoryRevenueChart', 'categoryCancelChart'].forEach(id => {
        RPA.registerExport(id, null);
        const canvas = document.getElementById(id);
        if (canvas && canvas.parentElement) {
          canvas.parentElement.innerHTML =
            '<div class="empty-state">Not available — the upload has no <code>Category label</code> column.</div>';
        }
      });
      return;
    }

    const byCategory = [...groupBy(rows.filter(r => r.ci), r => r.ci).entries()]
      .map(([ci, list]) => ({
        name: label(CATEGORIES, Number(ci)),
        revenue: sum(list, r => r.amt),
        lines: list.length,
        canceled: list.filter(r => r.st === CANCELED_STATUS).length
      }));

    const topRevenue = [...byCategory].sort((a, b) => b.revenue - a.revenue).slice(0, 8);
    hBar('categoryRevenueChart', topRevenue.map(c => c.name),
      topRevenue.map(c => +c.revenue.toFixed(2)), p.series[0],
      'Category', 'Revenue (' + (cur || 'amount') + ')');

    // Ranked by cancellation rate, but only where there is enough volume for the rate to mean
    // something — one cancellation out of one line is not a 100% problem category.
    const topCancel = byCategory
      .filter(c => c.lines >= MIN_SAMPLE_SIZE && c.canceled > 0)
      .map(c => ({ name: c.name, rate: c.canceled / c.lines }))
      .sort((a, b) => b.rate - a.rate).slice(0, 8);
    hBar('categoryCancelChart', topCancel.map(c => c.name),
      topCancel.map(c => +(c.rate * 100).toFixed(1)), p.markCritical,
      'Category', 'Cancellation rate (%)');
  }

  function renderDataQuality(rows, cur) {
    const canceled = rows.filter(r => r.st === CANCELED_STATUS);
    const shipped = rows.filter(r => r.sh);

    const checks = [
      ['high', 'Order without an invoice', rows.filter(r => !r.inv), 'Order with invoice',
        'Legal-compliance and customer-complaint exposure; chase the sellers involved.'],
      ['high', 'Canceled with no customer request', rows.filter(r => outcomeOf(r) === OUTCOME_SELLER_CANCEL),
        'Cancellation Request Status', 'Stock sync and price accuracy; should count against the seller score.'],
      ['high', 'Shipping deadline missed', shipped.filter(r => new Date(r.sh) > new Date(r.sd)), null,
        'Run the SLA-breach process for these sellers.'],
      ['high', 'Payout still filled in on a canceled line', canceled.filter(r => r.pay > 0),
        'Amount transferred to seller (including taxes)',
        'Exclude canceled lines before summing the payout column, or settlement is overstated.'],
      ['medium', 'Delivery closed automatically', rows.filter(isAutoReceived), null,
        'The carrier never reported delivery. Delivery-time metrics exclude these lines.'],
      ['medium', 'Canceled after it had shipped', canceled.filter(r => r.sh), null,
        'Move the cancellation cut-off to the carrier hand-off; these carry a return leg cost.'],
      ['medium', 'Active line not shipped yet', rows.filter(r => !r.sh && r.st !== CANCELED_STATUS), null,
        'Open backlog — worth chasing before the deadline passes.'],
      ['low', 'Never accepted by the seller', rows.filter(r => r.dc && !r.ac), 'Acceptance date',
        'Risk of automatic cancellation.']
    ];

    const visible = checks
      .filter(c => !(c[3] && columnMissing(c[3])))
      .map(([severity, name, list, , action]) => ({
        severity, name, count: list.length,
        share: rows.length ? list.length / rows.length : 0,
        amount: sum(list, r => r.amt), action
      }))
      .sort((a, b) => {
        const order = { high: 0, medium: 1, low: 2 };
        return order[a.severity] - order[b.severity] || b.count - a.count;
      });

    RPA.renderTable('dq-wrap', visible, [
      { label: 'Severity', render: x => '<span class="badge ' +
          (x.severity === 'high' ? 'red' : x.severity === 'medium' ? 'amber' : '') + '">' +
          x.severity.toUpperCase() + '</span>' },
      { label: 'Check', render: x => RPA.escapeHtml(x.name) },
      { label: 'Lines', numeric: true, value: x => x.count, render: x =>
          x.count === 0 ? '<span class="badge green">0</span>' : RPA.fmtInt(x.count) },
      { label: 'Share', numeric: true, value: x => +(x.share * 100).toFixed(1),
        render: x => RPA.fmtPct(x.share) },
      { label: 'Suggested action', render: x => RPA.escapeHtml(x.action) }
    ], 'No checks could be run on the selected range.');
  }

  function renderExtended(rows, cur, p) {
    // Delivery quality is temporarily switched off for a presentation. Its markup is commented out
    // in Views/Home/Index.cshtml; uncomment both together — renderKpis writes straight into the
    // element and would throw here if the section is missing, taking every section below it down.
    // renderDeliveryQuality(rows, cur);
    renderCancellationBreakdown(rows, cur, p);
    renderLeadTime(rows, cur);
    renderCategories(rows, cur, p);
    renderDataQuality(rows, cur);
  }

  function applyFilter() {
    const fromVal = document.getElementById('order-date-from').value;
    const toVal = document.getElementById('order-date-to').value;
    const sellerVal = document.getElementById('order-seller').value.trim();
    const from = fromVal ? new Date(fromVal + 'T00:00:00') : null;
    const to = toVal ? new Date(toVal + 'T23:59:59') : null;

    let filtered = ROWS;
    if (from || to) {
      filtered = filtered.filter(r => {
        if (!r.dc) return false;
        const d = new Date(r.dc);
        if (from && d < from) return false;
        if (to && d > to) return false;
        return true;
      });
    }

    // Picked from the list -> that seller exactly; typed by hand -> everyone whose name contains
    // it, so a half-remembered name still narrows the dashboard instead of emptying it.
    let sellerLabel = '';
    if (sellerVal) {
      const wanted = RPA.fold(sellerVal);
      const exact = SELLERS.some(s => RPA.fold(s) === wanted);
      filtered = filtered.filter(r => {
        const seller = RPA.fold(r.s);
        return exact ? seller === wanted : seller.indexOf(wanted) !== -1;
      });
      sellerLabel = exact ? sellerVal : 'Seller contains "' + sellerVal + '"';
    }

    const narrowed = from || to || sellerVal;
    const summary = narrowed
      ? 'Showing ' + filtered.length.toLocaleString('en-US') + ' of ' + ROWS.length.toLocaleString('en-US') + ' order lines'
      : 'Showing all ' + ROWS.length.toLocaleString('en-US') + ' order lines';
    document.getElementById('order-filter-summary').textContent =
      summary + (sellerLabel ? ' · ' + sellerLabel : '');

    // Printed under the title of every section exported from here, so a downloaded sheet says which
    // slice of the file it came from rather than looking like the whole export.
    const range = (from || to)
      ? 'Date created ' + (fromVal || '…') + ' → ' + (toVal || '…') + ' · '
      : '';
    RPA.setExportContext('Late Shipment & Cancellation Report · ' + range +
      (sellerLabel ? sellerLabel + ' · ' : '') + summary);

    computeAndRender(filtered);
  }

  function render(data) {
    ROWS = data.rows || [];
    CARRIERS = data.carriers && data.carriers.length ? data.carriers : [{ n: '', i: false, v: [] }];
    SELLERS = [...new Set(ROWS.map(r => r.s).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'tr'));
    CANCELED_STATUS = data.canceledStatus || 'Canceled';
    REFUNDED_STATUS = data.refundedStatus || 'Refunded';
    RECEIVED_STATUS = data.receivedStatus || 'Received';
    REJECTED_STATUS = data.rejectedStatus || 'Rejected';
    PENDING_ACCEPTANCE_STATUS = data.pendingAcceptanceStatus || 'Pending acceptance';
    AUTO_RECEIVED_REASON = data.autoReceivedReason || 'Received automatically';
    CATEGORIES = data.categories || [''];
    BRANDS = data.brands || [''];
    CITIES = data.cities || [''];
    MISSING_COLUMNS = data.missingColumns || [];
    if (data.minSampleSize) MIN_SAMPLE_SIZE = data.minSampleSize;
    if (data.minLeadTimeSample) MIN_LEAD_TIME_SAMPLE = data.minLeadTimeSample;

    REASON_LABELS = {};
    (data.reasonLabels || []).forEach(r => { REASON_LABELS[r.code] = r; });

    // A short export is a normal case, not an error — say which sections lose data rather than
    // reciting fourteen column names.
    const banner = document.getElementById('order-missing-columns');
    if (banner) {
      banner.hidden = MISSING_COLUMNS.length === 0;
      if (MISSING_COLUMNS.length) {
        const affected = [
          ['Cancellation breakdown', ['Cancellation Request Status', 'Cancellation Request Payload',
            'Total canceled amount (including taxes)', 'Total refunded amount (including taxes)']],
          ['Lead time (SLA) analysis', ['Lead time to ship']],
          ['Category performance', ['Category label']],
          ['Delivery quality', ['Acceptance date', 'Order with invoice']],
          ['Data quality', ['Amount transferred to seller (including taxes)', 'Order with invoice',
            'Acceptance date', 'Cancellation Request Status']]
        ].filter(([, cols]) => cols.some(columnMissing)).map(([name]) => name);

        banner.innerHTML =
          '<strong>' + MISSING_COLUMNS.length + ' optional column' +
          (MISSING_COLUMNS.length === 1 ? '' : 's') + ' missing.</strong> ' +
          (affected.length
            ? 'Partly or fully unavailable: ' + affected.map(RPA.escapeHtml).join(', ') + '. '
            : '') +
          '<span title="' + RPA.escapeHtml(MISSING_COLUMNS.join(', ')) + '">Hover for the full list.</span>';
      }
    }

    document.getElementById('order-date-from').value = '';
    document.getElementById('order-date-to').value = '';
    document.getElementById('order-seller').value = '';
    RPA.resetDataTables();
    RPA.fillDatalist('order-seller-list', SELLERS);
    RPA.seedDateRange('order-date-from', 'order-date-to', ROWS.map(r => r.dc));
    RPA.stamp('order-stamp');

    document.getElementById('order-results').hidden = false;
    applyFilter();

    // The section index is built from the report itself, so it can only be built once the report
    // is on screen — and the entrance cascade plays once, on the first report of the session.
    RPA.initSectionNav('order-section-nav', 'order-results');
    RPA.revealResults('order-results');
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('order-drop', 'order-file');

    const fileInput = document.getElementById('order-file');
    const generateBtn = document.getElementById('order-generate');
    const excelBtn = document.getElementById('order-excel');

    function selectedFile() {
      const file = fileInput.files && fileInput.files[0];
      if (!file) {
        RPA.showError('order-alert', 'Please upload the orders Excel file (.xlsx).');
        return null;
      }
      RPA.clearError('order-alert');
      return file;
    }

    generateBtn.addEventListener('click', async function () {
      const file = selectedFile();
      if (!file) return;

      const form = new FormData();
      form.append('file', file);

      RPA.setBusy(generateBtn, true, 'Generating…');
      RPA.showSkeleton('order-skeleton', 'order-results');
      try {
        const data = await RPA.postJson('/api/order-report/data', form);
        render(data);
      } catch (err) {
        RPA.showError('order-alert', err.message);
      } finally {
        RPA.hideSkeleton('order-skeleton');
        RPA.setBusy(generateBtn, false);
      }
    });

    excelBtn.addEventListener('click', async function () {
      const file = selectedFile();
      if (!file) return;

      const form = new FormData();
      form.append('file', file);

      RPA.setBusy(excelBtn, true, 'Building…');
      try {
        await RPA.postDownload('/api/order-report/excel', form, 'Gec Kargolama ve Iptal Raporu.xlsx');
      } catch (err) {
        RPA.showError('order-alert', err.message);
      } finally {
        RPA.setBusy(excelBtn, false);
      }
    });

    document.getElementById('order-apply').addEventListener('click', applyFilter);
    document.getElementById('order-reset').addEventListener('click', function () {
      document.getElementById('order-date-from').value = '';
      document.getElementById('order-date-to').value = '';
      document.getElementById('order-seller').value = '';
      applyFilter();
    });

    // Picking from the datalist fires `change`, not `click`, so the dashboard follows the choice
    // without a trip to Apply; Enter in the box does the same.
    document.getElementById('order-seller').addEventListener('change', applyFilter);
    document.getElementById('order-seller').addEventListener('keydown', function (event) {
      if (event.key === 'Enter') applyFilter();
    });

    // Charts bake in theme colours, so redraw them when the theme flips.
    document.addEventListener('rpa:themechange', function () {
      if (!document.getElementById('order-results').hidden) applyFilter();
    });
  });

})(window.RPA);
