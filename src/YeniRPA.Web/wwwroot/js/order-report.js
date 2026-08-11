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
     sc  shipping company

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
  let INTEGRATED_KEYWORDS = [];
  let CANCELED_STATUS = 'Canceled';
  let REFUNDED_STATUS = 'Refunded';
  let RECEIVED_STATUS = 'Received';
  let REJECTED_STATUS = 'Rejected';
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

  function isIntegratedCarrier(company) {
    if (!company) return false;
    const c = company.toLowerCase();
    return INTEGRATED_KEYWORDS.some(k => c.includes(k.toLowerCase()));
  }

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
   * A chart exports as the two-column table it was drawn from, so a card whose only content is a
   * canvas still has something to download.
   */
  function registerChartExport(id, labelHeader, valueHeader, labels, data) {
    RPA.registerExport(id, {
      columns: [{ label: labelHeader }, { label: valueHeader, numeric: true }],
      rows: labels.map((label, i) => [label, data[i]])
    });
  }

  function hBar(id, labels, data, color, labelHeader, valueHeader) {
    registerChartExport(id, labelHeader, valueHeader, labels, data);

    const p = RPA.palette();
    charts[id] = new Chart(document.getElementById(id), {
      type: 'bar',
      data: { labels, datasets: [{ data, backgroundColor: color, borderRadius: 4, maxBarThickness: 26 }] },
      options: {
        indexAxis: 'y', maintainAspectRatio: false, responsive: true,
        plugins: { legend: { display: false } },
        scales: {
          x: { beginAtZero: true, grid: { color: p.line }, border: { display: false } },
          y: { grid: { display: false }, border: { display: false } }
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
    const total = rows.length;
    const carriers = [...groupBy(rows, r => r.sc || '').entries()]
      .map(([carrier, list]) => ({
        carrier,
        count: list.length,
        share: total ? list.length / total : 0,
        integrated: isIntegratedCarrier(carrier)
      }))
      .sort((a, b) => b.count - a.count);

    RPA.renderTable('carrier-volume-wrap', carriers, [
      { label: 'Carrier', render: x => (x.carrier ? RPA.escapeHtml(x.carrier) : 'Not recorded') },
      { label: 'Order lines', numeric: true, value: x => x.count, render: x => RPA.fmtInt(x.count) },
      { label: 'Share', numeric: true, value: x => +(x.share * 100).toFixed(1),
        render: x => RPA.fmtPct(x.share) },
      { label: 'Delivery reporting', render: x => {
          if (!x.carrier) return '—';
          return x.integrated
            ? '<span class="badge green">Integrated</span>'
            : '<span class="badge amber">Manual</span>';
        } }
    ], 'No order lines in the selected range.');
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

    const shipDurations = rows.filter(r => r.dc && r.sh).map(r => (new Date(r.sh) - new Date(r.dc)) / 3.6e6);
    const avgHoursToShip = shipDurations.length ? shipDurations.reduce((a, b) => a + b, 0) / shipDurations.length : 0;

    const receivedRows = rows.filter(r => r.sh && r.rd);
    const integratedDurations = receivedRows.filter(r => isIntegratedCarrier(r.sc)).map(r => (new Date(r.rd) - new Date(r.sh)) / 3.6e6);
    const manualDurations = receivedRows.filter(r => !isIntegratedCarrier(r.sc)).map(r => (new Date(r.rd) - new Date(r.sh)) / 3.6e6);
    const avgHoursReceiveIntegrated = avg(integratedDurations);
    const avgHoursReceiveManual = avg(manualDurations);

    const currencyCounts = {};
    rows.forEach(r => { if (r.cur) currencyCounts[r.cur] = (currencyCounts[r.cur] || 0) + 1; });
    const sortedCurrencies = Object.entries(currencyCounts).sort((a, b) => b[1] - a[1]);
    const primaryCurrency = sortedCurrencies.length ? sortedCurrencies[0][0] : '';

    RPA.renderKpis('order-kpis', [
      ['Total order lines', RPA.fmtInt(totalLines), ''],
      ['Received orders', RPA.fmtInt(received), 'green'],
      ['Received rate', RPA.fmtPct(receivedRate), 'green'],
      ['Rejected orders', RPA.fmtInt(rejected), 'red'],
      ['Rejection rate', RPA.fmtPct(rejectedRate), 'red'],
      ['Late shipped', RPA.fmtInt(lateShipped), 'red'],
      ['Late shipment rate', RPA.fmtPct(lateRate), 'red'],
      ['Canceled orders', RPA.fmtInt(canceled), 'red'],
      ['Cancellation rate', RPA.fmtPct(cancelRate), 'red'],
      ['Avg. hours to ship', RPA.fmtHours(avgHoursToShip), ''],
      ['Avg. hours to receive (integrated)', RPA.fmtHours(avgHoursReceiveIntegrated), 'green'],
      ['Avg. hours to receive (manual)', RPA.fmtHours(avgHoursReceiveManual), 'red']
    ]);

    // Status distribution
    const statusMap = {};
    rows.forEach(r => { if (r.st) statusMap[r.st] = (statusMap[r.st] || 0) + 1; });
    const statusEntries = Object.entries(statusMap).sort((a, b) => b[1] - a[1]);

    const top5Late = [...groupBy(shippedRows, r => r.s).entries()]
      .map(([seller, list]) => ({ seller, count: list.filter(r => new Date(r.sh) > new Date(r.sd)).length }))
      .filter(x => x.count > 0).sort((a, b) => b.count - a.count).slice(0, 5);

    const top5Canceled = [...groupBy(rows, r => r.s).entries()]
      .map(([seller, list]) => ({ seller, count: list.filter(r => r.st === CANCELED_STATUS).length }))
      .filter(x => x.count > 0).sort((a, b) => b.count - a.count).slice(0, 5);

    const carrierGroups = [...groupBy(receivedRows.filter(r => r.sc), r => r.sc).entries()]
      .map(([carrier, list]) => ({
        carrier, count: list.length,
        avgHours: avg(list.map(r => (new Date(r.rd) - new Date(r.sh)) / 3.6e6)),
        integrated: isIntegratedCarrier(carrier)
      }))
      .sort((a, b) => b.count - a.count).slice(0, 8)
      .sort((a, b) => a.avgHours - b.avgHours);

    const trendMap = {};
    rows.forEach(r => { if (r.dc) { const d = r.dc.slice(0, 10); trendMap[d] = (trendMap[d] || 0) + 1; } });
    const trendEntries = Object.entries(trendMap).sort((a, b) => a[0].localeCompare(b[0]));

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
          label: 'Orders',
          data: trendEntries.map(e => e[1]),
          borderColor: p.accent,
          backgroundColor: RPA.alpha(p.accent, .16),
          fill: true, tension: .3, pointRadius: 0, pointHoverRadius: 4, borderWidth: 2
        }]
      },
      options: {
        maintainAspectRatio: false, responsive: true,
        plugins: { legend: { display: false } },
        interaction: { mode: 'index', intersect: false },
        scales: {
          y: { beginAtZero: true, grid: { color: p.line }, border: { display: false } },
          x: { grid: { display: false }, border: { display: false }, ticks: { maxRotation: 0, autoSkipPadding: 16 } }
        }
      }
    });

    registerChartExport('statusChart', 'Status', 'Order lines',
      statusEntries.map(e => e[0]), statusEntries.map(e => e[1]));

    charts.statusChart = new Chart(document.getElementById('statusChart'), {
      type: 'doughnut',
      data: {
        labels: statusEntries.map(e => e[0]),
        datasets: [{
          data: statusEntries.map(e => e[1]),
          backgroundColor: [p.accent, '#2563EB', p.green, p.red, p.amber, '#7C3AED', '#0EA5E9', '#DB2777'],
          borderColor: p.surface,
          borderWidth: 2
        }]
      },
      options: {
        maintainAspectRatio: false, responsive: true, cutout: '58%',
        plugins: { legend: { position: 'right', labels: { boxWidth: 10, boxHeight: 10, font: { size: 11 } } } }
      }
    });

    hBar('carrierChart', carrierGroups.map(c => c.carrier), carrierGroups.map(c => +c.avgHours.toFixed(1)),
      carrierGroups.map(c => (c.integrated ? p.accent : p.red)), 'Carrier', 'Avg. hours to receive');
    renderCarrierVolume(rows);
    hBar('lateChart', top5Late.map(x => x.seller), top5Late.map(x => x.count), p.red,
      'Seller', 'Late shipped lines');
    hBar('cancelChart', top5Canceled.map(x => x.seller), top5Canceled.map(x => x.count), p.accent,
      'Seller', 'Canceled lines');

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

    const shown = byOutcome.filter(x => x.count > 0);
    registerChartExport('cancelClassChart', 'Outcome', 'Order lines',
      shown.map(x => x.outcome.label), shown.map(x => x.count));

    charts.cancelClassChart = new Chart(document.getElementById('cancelClassChart'), {
      type: 'doughnut',
      data: {
        labels: shown.map(x => x.outcome.label),
        datasets: [{
          data: shown.map(x => x.count),
          backgroundColor: shown.map(x =>
            x.outcome.tone === 'red' ? p.red :
            x.outcome.tone === 'amber' ? p.amber :
            x.outcome.tone === 'green' ? p.green : p.accent),
          borderColor: p.surface,
          borderWidth: 2
        }]
      },
      options: {
        maintainAspectRatio: false, responsive: true, cutout: '58%',
        plugins: { legend: { position: 'right', labels: { boxWidth: 10, boxHeight: 10, font: { size: 11 } } } }
      }
    });

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

    // Detail list — everything that did not complete cleanly, worst money first.
    const detail = rows
      .filter(r => outcomeOf(r) !== OUTCOME_CLEAN)
      .map(r => ({ r, lost: (r.can || 0) + (r.ref || 0) }))
      .sort((a, b) => b.lost - a.lost)
      .slice(0, 200);

    RPA.renderTable('cancel-detail-wrap', detail, [
      { label: 'Order', render: x => RPA.escapeHtml(x.r.ord || '—') },
      { label: 'Seller', render: x => RPA.escapeHtml(x.r.s) },
      { label: 'Outcome', render: x => {
          const o = OUTCOMES.find(v => v.id === outcomeOf(x.r));
          return '<span class="badge ' + o.tone + '">' + RPA.escapeHtml(o.label) + '</span>';
        } },
      { label: 'Category', render: x => RPA.escapeHtml(label(CATEGORIES, x.r.ci)) },
      { label: 'Reason', render: x => {
          if (!x.r.crr) return '—';
          const meta = REASON_LABELS[x.r.crr];
          return RPA.escapeHtml(meta ? meta.label : x.r.crr);
        } },
      { label: 'Already shipped?', render: x => x.r.sh
          ? '<span class="badge amber">Yes — return leg cost</span>'
          : 'No' }
    ], 'Every order line completed normally in the selected range.');
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
      // Equal gains are broken by shipped lines rather than by revenue, so the ranking is explained
      // by a column that is actually on screen.
      .sort((a, b) => b.gain - a.gain || b.count - a.count);

    const promiseRange = x => (x.minLt === x.maxLt ? x.minLt + ' d' : x.minLt + '–' + x.maxLt + ' d');

    RPA.renderTable('lt-opportunity-wrap', opportunities, [
      { label: 'Seller', render: x => RPA.escapeHtml(x.seller) },
      { label: 'Promised (avg.)', numeric: true, value: x => +x.promised.toFixed(1),
        render: x => x.promised.toFixed(1) + ' d' },
      { label: 'Promise range', numeric: true, value: promiseRange, render: promiseRange },
      { label: 'Shipped lines', numeric: true, value: x => x.count, render: x => RPA.fmtInt(x.count) },
      { label: 'Avg. to ship', numeric: true, value: x => +x.avgHours.toFixed(1),
        render: x => RPA.fmtHours(x.avgHours) },
      { label: '90th pct.', numeric: true, value: x => +x.p90.toFixed(1), render: x => RPA.fmtHours(x.p90) },
      { label: 'SLA usage', numeric: true, value: x => +(x.utilisation * 100).toFixed(1),
        render: x => '<span class="badge green">' + RPA.fmtPct(x.utilisation) + '</span>' },
      { label: 'Suggested', numeric: true, value: x => x.suggested, render: x => x.suggested + ' d' },
      { label: 'Days to gain', numeric: true, value: x => +x.gain.toFixed(1),
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
      topRevenue.map(c => +c.revenue.toFixed(2)), p.accent,
      'Category', 'Revenue (' + (cur || 'amount') + ')');

    // Ranked by cancellation rate, but only where there is enough volume for the rate to mean
    // something — one cancellation out of one line is not a 100% problem category.
    const topCancel = byCategory
      .filter(c => c.lines >= MIN_SAMPLE_SIZE && c.canceled > 0)
      .map(c => ({ name: c.name, rate: c.canceled / c.lines }))
      .sort((a, b) => b.rate - a.rate).slice(0, 8);
    hBar('categoryCancelChart', topCancel.map(c => c.name),
      topCancel.map(c => +(c.rate * 100).toFixed(1)), p.red,
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
    renderDeliveryQuality(rows, cur);
    renderCancellationBreakdown(rows, cur, p);
    renderLeadTime(rows, cur);
    renderCategories(rows, cur, p);
    renderDataQuality(rows, cur);
  }

  function applyFilter() {
    const fromVal = document.getElementById('order-date-from').value;
    const toVal = document.getElementById('order-date-to').value;
    const from = fromVal ? new Date(fromVal + 'T00:00:00') : null;
    const to = toVal ? new Date(toVal + 'T23:59:59') : null;

    let filtered = ROWS;
    if (from || to) {
      filtered = ROWS.filter(r => {
        if (!r.dc) return false;
        const d = new Date(r.dc);
        if (from && d < from) return false;
        if (to && d > to) return false;
        return true;
      });
    }

    const summary = (from || to)
      ? 'Showing ' + filtered.length.toLocaleString('en-US') + ' of ' + ROWS.length.toLocaleString('en-US') + ' order lines'
      : 'Showing all ' + ROWS.length.toLocaleString('en-US') + ' order lines';
    document.getElementById('order-filter-summary').textContent = summary;

    // Printed under the title of every section exported from here, so a downloaded sheet says which
    // slice of the file it came from rather than looking like the whole export.
    const range = (from || to)
      ? 'Date created ' + (fromVal || '…') + ' → ' + (toVal || '…') + ' · '
      : '';
    RPA.setExportContext('Late Shipment & Cancellation Report · ' + range + summary);

    computeAndRender(filtered);
  }

  function render(data) {
    ROWS = data.rows || [];
    INTEGRATED_KEYWORDS = data.carrierKeywords || [];
    CANCELED_STATUS = data.canceledStatus || 'Canceled';
    REFUNDED_STATUS = data.refundedStatus || 'Refunded';
    RECEIVED_STATUS = data.receivedStatus || 'Received';
    REJECTED_STATUS = data.rejectedStatus || 'Rejected';
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
    RPA.seedDateRange('order-date-from', 'order-date-to', ROWS.map(r => r.dc));
    RPA.stamp('order-stamp');

    document.getElementById('order-results').hidden = false;
    applyFilter();
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
      applyFilter();
    });

    // Charts bake in theme colours, so redraw them when the theme flips.
    document.addEventListener('rpa:themechange', function () {
      if (!document.getElementById('order-results').hidden) applyFilter();
    });
  });

})(window.RPA);
