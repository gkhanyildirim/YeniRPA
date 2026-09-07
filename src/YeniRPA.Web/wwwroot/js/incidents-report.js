/* =============================================================================
   Incidents report.

   The per-row work — lifecycle, ages, who owes the next reply, data-quality
   flags — is done server-side in IncidentsReportBuilder. This file holds the
   flat rows it sent and does every grouping here, on purpose: the seller,
   reason and product scorecards have to answer for whatever the filter bar is
   currently narrowing to, and a scorecard computed on the server would be
   frozen at the whole upload.

   Two uploads feed it, because the Mirakl incident panel cannot export open and
   closed incidents together. Either file alone still produces a report; the
   sections that need the other one hide themselves rather than printing an
   empty table.

   Two rules run through the whole file:

     * "Waiting on us" means the SELLER said it is solved — never that a customer
       is waiting for an answer. While an incident is open the thread is between
       the customer and the seller; our work is verifying and closing what the
       seller has already resolved.
     * Thresholds are never restated here. Every day count comes off the payload
       so the dashboard and the builder cannot drift apart.

   There is deliberately no money anywhere: the amount column describes the order
   rather than the incident, and this is a queue rather than a financial report.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const OPEN = 'open';
  const RESOLVED = 'resolved';
  const CLOSED = 'closed';

  const AGE_BUCKETS = [
    { label: '0 – 1 d', min: 0, max: 2 },
    { label: '2 – 3 d', min: 2, max: 4 },
    { label: '4 – 7 d', min: 4, max: 8 },
    { label: '8 – 14 d', min: 8, max: 15 },
    { label: '15 – 30 d', min: 15, max: 31 },
    { label: '30 d +', min: 31, max: Infinity }
  ];

  const US = 'us';

  const WAITING_LABELS = {
    us: 'On us',
    seller: 'On the seller',
    customer: 'On the customer',
    'operator-acted': 'Operator acted last',
    none: 'Closed'
  };

  const ACTOR_LABELS = {
    customer: 'Customer',
    internal: 'Operator',
    automation: 'Automation',
    seller: 'Seller'
  };

  // A thread this long has stopped being a question. Mirrors IncidentsReportBuilder.HotThreadMessages;
  // it is the one threshold the payload does not carry, because it shapes a section rather than a verdict.
  const HOT_THREAD_MESSAGES = 8;

  // The whole upload, kept so the filter can re-derive without another request.
  let ROWS = [];
  let META = { warningDays: 7, breachDays: 14, staleDays: 3, minSampleSize: 3, closedFrom: '' };

  const charts = {};

  // ---------------------------------------------------------------------------
  // Small statistics
  // ---------------------------------------------------------------------------

  const numbers = (rows, pick) => rows.map(pick).filter(v => v !== null && v !== undefined);

  const avg = list => (list.length ? list.reduce((sum, v) => sum + v, 0) / list.length : null);

  /** Nearest-rank percentile: q = .5 is the median, q = .9 the p90. */
  function percentile(list, q) {
    if (!list.length) return null;
    const sorted = [...list].sort((a, b) => a - b);
    const index = Math.min(sorted.length - 1, Math.max(0, Math.ceil(q * sorted.length) - 1));
    return sorted[index];
  }

  const distinct = (rows, pick) => new Set(rows.map(pick).filter(Boolean)).size;

  /** The most frequent non-empty value of a field, for the "top reason"-style columns. */
  function topValue(rows, pick) {
    const counts = new Map();
    rows.forEach(r => {
      const v = pick(r);
      if (v) counts.set(v, (counts.get(v) || 0) + 1);
    });
    let best = '';
    let bestCount = 0;
    counts.forEach((count, value) => {
      if (count > bestCount) { best = value; bestCount = count; }
    });
    return best ? best + ' (' + bestCount + ')' : '-';
  }

  /** Groups rows by a key, dropping the rows whose key is empty. */
  function groupBy(rows, pick) {
    const groups = new Map();
    rows.forEach(r => {
      const key = pick(r);
      if (!key) return;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(r);
    });
    return groups;
  }

  const isOpenish = r => r.lifecycle !== CLOSED;
  const round1 = v => (v === null || v === undefined ? null : Math.round(v * 10) / 10);

  // ---------------------------------------------------------------------------
  // Cells
  // ---------------------------------------------------------------------------

  /**
   * Age with a verdict attached. The thresholds come off the payload, so this badge and the
   * builder's own idea of a breach can never disagree.
   */
  function ageCell(r) {
    if (r.ageDays === null || r.ageDays === undefined) return '-';
    const text = r.ageDays.toFixed(1) + ' d';
    if (r.ageDays >= META.breachDays) return '<span class="badge red">' + text + '</span>';
    if (r.ageDays >= META.warningDays) return '<span class="badge amber">' + text + '</span>';
    return '<span class="badge green">' + text + '</span>';
  }

  function silenceCell(r) {
    if (r.silenceDays === null || r.silenceDays === undefined) return '-';
    const text = r.silenceDays.toFixed(1) + ' d';
    return r.silenceDays >= META.staleDays && isOpenish(r)
      ? '<span class="badge amber">' + text + '</span>'
      : text;
  }

  function lifecycleCell(r) {
    if (r.lifecycle === CLOSED) return '<span class="badge green">Closed</span>';
    if (r.lifecycle === RESOLVED) return '<span class="badge amber">On us</span>';
    return '<span class="badge amber">Open</span>';
  }

  // The operator's own Mirakl order page. Order numbers here always come straight off the export,
  // unchanged, so the same string that identifies the row also addresses it on Mirakl.
  const ORDER_BASE_URL = 'https://mediamarktsaturn.mirakl.net/mmp/operator/order/';

  function orderCell(r) {
    if (!r.orderNumber) return '<span class="badge amber">No order number</span>';
    return '<a class="order-link" href="' + ORDER_BASE_URL + encodeURIComponent(r.orderNumber) +
      '" target="_blank" rel="noopener">' + RPA.escapeHtml(r.orderNumber) + '</a>';
  }

  function messagesCell(r) {
    return r.messageCount >= HOT_THREAD_MESSAGES
      ? '<span class="badge red">' + RPA.fmtInt(r.messageCount) + '</span>'
      : RPA.fmtInt(r.messageCount);
  }

  const waitingLabel = r => WAITING_LABELS[r.waitingOn] || '-';
  const actorLabel = kind => ACTOR_LABELS[kind] || '-';

  // ---------------------------------------------------------------------------
  // Column sets
  // ---------------------------------------------------------------------------

  const reviewColumns = [
    { label: 'Problem', filter: 'select', value: r => r.issues.join(' · '), render: r => RPA.escapeHtml(r.issues.join(' · ')) },
    { label: 'From', filter: 'select', value: r => (r.source === OPEN ? 'Open export' : 'Closed export'), render: r => RPA.escapeHtml(r.source === OPEN ? 'Open export' : 'Closed export') },
    { label: 'Order', filter: 'text', value: r => r.orderNumber, render: orderCell },
    { label: 'Seller', filter: 'select', value: r => r.seller, render: r => RPA.escapeHtml(r.seller || '-') },
    { label: 'Status', filter: 'select', value: r => r.status, render: r => RPA.escapeHtml(r.status || '-') },
    { label: 'Opened', value: r => r.openedOn || '', render: r => RPA.escapeHtml(r.openedOn || '-') },
    { label: 'Closed', value: r => r.closedOn || '', render: r => RPA.escapeHtml(r.closedOn || '-') },
    { label: 'Customer', filter: 'text', value: r => r.customerName, render: r => RPA.escapeHtml(r.customerName || '-') }
  ];

  // ---------------------------------------------------------------------------
  // Aggregations
  //
  // Each returns plain objects and a matching column set; every column carries a `value` so the
  // Excel export receives real numbers instead of the formatted text on screen.
  // ---------------------------------------------------------------------------

  function sellerScorecard(rows) {
    const out = [];
    groupBy(rows, r => r.seller).forEach((list, seller) => {
      const open = list.filter(r => r.lifecycle === OPEN);
      const closed = list.filter(r => r.lifecycle === CLOSED);
      const resolutions = numbers(closed, r => r.resolutionDays);
      const ages = numbers(open, r => r.ageDays);

      out.push({
        seller,
        total: list.length,
        open: open.length,
        resolved: list.filter(r => r.lifecycle === RESOLVED).length,
        closed: closed.length,
        closeRate: list.length ? closed.length / list.length : 0,
        // This seller's incidents now sitting in OUR queue — they said it was solved and we have
        // not closed it yet. Counted per seller because a seller who resolves in bulk floods it.
        onUs: list.filter(r => r.waitingOn === US).length,
        breached: open.filter(r => r.ageDays !== null && r.ageDays >= META.breachDays).length,
        stale: list.filter(r => isOpenish(r) && r.silenceDays !== null && r.silenceDays >= META.staleDays).length,
        avgAge: round1(avg(ages)),
        oldestOpen: ages.length ? round1(Math.max.apply(null, ages)) : null,
        avgMessages: round1(avg(numbers(list, r => r.messageCount))),
        avgResolution: round1(avg(resolutions)),
        p90Resolution: round1(percentile(resolutions, .9)),
        topReason: topValue(list, r => r.reason)
      });
    });

    return out.sort((a, b) => b.open - a.open || b.total - a.total);
  }

  const sellerColumns = [
    { label: 'Seller', filter: 'text', value: r => r.seller, render: r => RPA.escapeHtml(r.seller) },
    { label: 'Incidents', numeric: true, filter: 'number', value: r => r.total, render: r => RPA.fmtInt(r.total) },
    { label: 'Open', numeric: true, filter: 'number', value: r => r.open, render: r => RPA.fmtInt(r.open) },
    { label: 'Resolved', numeric: true, value: r => r.resolved, render: r => RPA.fmtInt(r.resolved) },
    { label: 'Closed', numeric: true, value: r => r.closed, render: r => RPA.fmtInt(r.closed) },
    { label: 'Close rate', numeric: true, value: r => +(r.closeRate * 100).toFixed(1), render: r => RPA.fmtPct(r.closeRate) },
    {
      label: 'On us', numeric: true, filter: 'number', value: r => r.onUs,
      render: r => (r.onUs ? '<span class="badge amber">' + RPA.fmtInt(r.onUs) + '</span>' : '0')
    },
    {
      label: 'Breached', numeric: true, filter: 'number', value: r => r.breached,
      render: r => (r.breached ? '<span class="badge red">' + RPA.fmtInt(r.breached) + '</span>' : '0')
    },
    { label: 'Stale', numeric: true, value: r => r.stale, render: r => RPA.fmtInt(r.stale) },
    { label: 'Avg age', numeric: true, value: r => r.avgAge, render: r => RPA.fmtDays(r.avgAge) },
    { label: 'Oldest open', numeric: true, value: r => r.oldestOpen, render: r => RPA.fmtDays(r.oldestOpen) },
    { label: 'Avg msgs', numeric: true, value: r => r.avgMessages, render: r => (r.avgMessages === null ? '-' : r.avgMessages.toFixed(1)) },
    { label: 'Avg days to close', numeric: true, value: r => r.avgResolution, render: r => RPA.fmtDays(r.avgResolution) },
    { label: 'p90 days to close', numeric: true, value: r => r.p90Resolution, render: r => RPA.fmtDays(r.p90Resolution) },
    { label: 'Top reason', filter: 'text', value: r => r.topReason, render: r => RPA.escapeHtml(r.topReason) }
  ];

  function reasonBreakdown(rows) {
    const total = rows.length;
    const out = [];
    groupBy(rows, r => r.reason).forEach((list, reason) => {
      const closed = list.filter(r => r.lifecycle === CLOSED);
      out.push({
        reason,
        total: list.length,
        share: total ? list.length / total : 0,
        open: list.filter(r => r.lifecycle === OPEN).length,
        closed: closed.length,
        avgAge: round1(avg(numbers(list.filter(isOpenish), r => r.ageDays))),
        avgResolution: round1(avg(numbers(closed, r => r.resolutionDays))),
        avgMessages: round1(avg(numbers(list, r => r.messageCount))),
        avgLag: round1(avg(numbers(list, r => r.orderToIncidentDays))),
        onUs: list.filter(r => r.waitingOn === US).length,
        topSeller: topValue(list, r => r.seller)
      });
    });
    return out.sort((a, b) => b.total - a.total);
  }

  const reasonColumns = [
    { label: 'Reason', filter: 'text', value: r => r.reason, render: r => RPA.escapeHtml(r.reason) },
    { label: 'Incidents', numeric: true, value: r => r.total, render: r => RPA.fmtInt(r.total) },
    { label: 'Share', numeric: true, value: r => +(r.share * 100).toFixed(1), render: r => RPA.fmtPct(r.share) },
    { label: 'Open', numeric: true, value: r => r.open, render: r => RPA.fmtInt(r.open) },
    { label: 'Closed', numeric: true, value: r => r.closed, render: r => RPA.fmtInt(r.closed) },
    { label: 'Avg age', numeric: true, value: r => r.avgAge, render: r => RPA.fmtDays(r.avgAge) },
    { label: 'Avg days to close', numeric: true, value: r => r.avgResolution, render: r => RPA.fmtDays(r.avgResolution) },
    { label: 'Avg msgs', numeric: true, value: r => r.avgMessages, render: r => (r.avgMessages === null ? '-' : r.avgMessages.toFixed(1)) },
    { label: 'Order to incident', numeric: true, value: r => r.avgLag, render: r => RPA.fmtDays(r.avgLag) },
    { label: 'On us', numeric: true, value: r => r.onUs, render: r => RPA.fmtInt(r.onUs) },
    { label: 'Top seller', filter: 'text', value: r => r.topSeller, render: r => RPA.escapeHtml(r.topSeller) }
  ];

  function closingBreakdown(closedRows) {
    const total = closedRows.length;
    const out = [];
    groupBy(closedRows, r => r.closingReason).forEach((list, reason) => {
      const resolutions = numbers(list, r => r.resolutionDays);
      out.push({
        reason,
        total: list.length,
        share: total ? list.length / total : 0,
        byOperator: list.filter(r => r.closedByKind === 'internal' || r.closedByKind === 'automation').length,
        bySeller: list.filter(r => r.closedByKind === 'seller').length,
        avgResolution: round1(avg(resolutions)),
        medianResolution: round1(percentile(resolutions, .5)),
        avgMessages: round1(avg(numbers(list, r => r.messageCount))),
        sellers: distinct(list, r => r.seller)
      });
    });
    return out.sort((a, b) => b.total - a.total);
  }

  const closingColumns = [
    { label: 'Closing reason', filter: 'text', value: r => r.reason, render: r => RPA.escapeHtml(r.reason) },
    { label: 'Incidents', numeric: true, value: r => r.total, render: r => RPA.fmtInt(r.total) },
    { label: 'Share', numeric: true, value: r => +(r.share * 100).toFixed(1), render: r => RPA.fmtPct(r.share) },
    { label: 'Closed by operator', numeric: true, value: r => r.byOperator, render: r => RPA.fmtInt(r.byOperator) },
    { label: 'Closed by seller', numeric: true, value: r => r.bySeller, render: r => RPA.fmtInt(r.bySeller) },
    { label: 'Avg days to close', numeric: true, value: r => r.avgResolution, render: r => RPA.fmtDays(r.avgResolution) },
    { label: 'Median days', numeric: true, value: r => r.medianResolution, render: r => RPA.fmtDays(r.medianResolution) },
    { label: 'Avg msgs', numeric: true, value: r => r.avgMessages, render: r => (r.avgMessages === null ? '-' : r.avgMessages.toFixed(1)) },
    { label: 'Sellers', numeric: true, value: r => r.sellers, render: r => RPA.fmtInt(r.sellers) }
  ];

  /**
   * Days-to-close by seller. Sellers below the minimum sample are dropped rather than ranked: one
   * incident closed in an hour does not make a seller the fastest on the marketplace.
   */
  function resolutionSpeed(closedRows) {
    const out = [];
    groupBy(closedRows, r => r.seller).forEach((list, seller) => {
      const resolutions = numbers(list, r => r.resolutionDays);
      if (resolutions.length < META.minSampleSize) return;
      out.push({
        seller,
        closed: resolutions.length,
        avg: round1(avg(resolutions)),
        median: round1(percentile(resolutions, .5)),
        p90: round1(percentile(resolutions, .9)),
        fastest: round1(Math.min.apply(null, resolutions)),
        slowest: round1(Math.max.apply(null, resolutions)),
        canceled: list.filter(r => (r.closingReason || '').toLowerCase() === 'canceled').length,
        topClosing: topValue(list, r => r.closingReason)
      });
    });
    return out.sort((a, b) => b.avg - a.avg);
  }

  const speedColumns = [
    { label: 'Seller', filter: 'text', value: r => r.seller, render: r => RPA.escapeHtml(r.seller) },
    { label: 'Closed', numeric: true, value: r => r.closed, render: r => RPA.fmtInt(r.closed) },
    { label: 'Avg days', numeric: true, value: r => r.avg, render: r => RPA.fmtDays(r.avg) },
    { label: 'Median days', numeric: true, value: r => r.median, render: r => RPA.fmtDays(r.median) },
    { label: 'p90 days', numeric: true, value: r => r.p90, render: r => RPA.fmtDays(r.p90) },
    { label: 'Fastest', numeric: true, value: r => r.fastest, render: r => RPA.fmtDays(r.fastest) },
    { label: 'Slowest', numeric: true, value: r => r.slowest, render: r => RPA.fmtDays(r.slowest) },
    {
      label: 'Canceled', numeric: true, value: r => r.canceled,
      render: r => (r.canceled ? '<span class="badge red">' + RPA.fmtInt(r.canceled) + '</span>' : '0')
    },
    { label: 'Usual outcome', filter: 'text', value: r => r.topClosing, render: r => RPA.escapeHtml(r.topClosing) }
  ];

  function productHotspots(rows) {
    const out = [];
    groupBy(rows, r => r.productSku || r.product).forEach((list, sku) => {
      if (list.length < 2) return;
      out.push({
        sku,
        product: list[0].product || '-',
        total: list.length,
        open: list.filter(isOpenish).length,
        sellers: distinct(list, r => r.seller),
        customers: distinct(list, r => r.customerName),
        onUs: list.filter(r => r.waitingOn === US).length,
        topReason: topValue(list, r => r.reason)
      });
    });
    return out.sort((a, b) => b.total - a.total || b.open - a.open);
  }

  const productColumns = [
    { label: 'SKU', filter: 'text', value: r => r.sku, render: r => RPA.escapeHtml(r.sku) },
    { label: 'Product', filter: 'text', value: r => r.product, render: r => RPA.escapeHtml(r.product) },
    { label: 'Incidents', numeric: true, value: r => r.total, render: r => RPA.fmtInt(r.total) },
    { label: 'Still open', numeric: true, value: r => r.open, render: r => RPA.fmtInt(r.open) },
    { label: 'On us', numeric: true, value: r => r.onUs, render: r => RPA.fmtInt(r.onUs) },
    { label: 'Sellers', numeric: true, value: r => r.sellers, render: r => RPA.fmtInt(r.sellers) },
    { label: 'Customers', numeric: true, value: r => r.customers, render: r => RPA.fmtInt(r.customers) },
    { label: 'Top reason', filter: 'text', value: r => r.topReason, render: r => RPA.escapeHtml(r.topReason) }
  ];

  /**
   * One row per account that appears anywhere on an incident. A single incident can credit three
   * different accounts — one opened it, one acted last, one closed it — so the columns are counted
   * separately rather than summed into a single "touched" figure that would mean nothing.
   */
  function workload(rows) {
    const accounts = new Map();

    function slot(user, kind) {
      const key = user.trim();
      if (!key) return null;
      if (!accounts.has(key)) {
        accounts.set(key, { user: key, kind: actorLabel(kind), opened: 0, closed: 0, lastTouched: 0, openHeld: 0, silences: [] });
      }
      return accounts.get(key);
    }

    rows.forEach(r => {
      const opener = slot(r.openedByUser, r.openedByKind);
      if (opener) opener.opened += 1;

      const closer = slot(r.closedByUser, r.closedByKind);
      if (closer) closer.closed += 1;

      const actor = slot(r.lastActionByUser, r.lastActorKind);
      if (actor) {
        actor.lastTouched += 1;
        if (isOpenish(r)) {
          actor.openHeld += 1;
          if (r.silenceDays !== null && r.silenceDays !== undefined) actor.silences.push(r.silenceDays);
        }
      }
    });

    return Array.from(accounts.values())
      .map(a => ({
        user: a.user,
        kind: a.kind,
        opened: a.opened,
        closed: a.closed,
        lastTouched: a.lastTouched,
        openHeld: a.openHeld,
        avgSilence: round1(avg(a.silences)),
        worstSilence: a.silences.length ? round1(Math.max.apply(null, a.silences)) : null
      }))
      .sort((a, b) => b.lastTouched - a.lastTouched || b.opened - a.opened);
  }

  const workloadColumns = [
    { label: 'Account', filter: 'text', value: r => r.user, render: r => RPA.escapeHtml(r.user) },
    { label: 'Kind', filter: 'select', value: r => r.kind, render: r => RPA.escapeHtml(r.kind) },
    { label: 'Opened', numeric: true, value: r => r.opened, render: r => RPA.fmtInt(r.opened) },
    { label: 'Closed', numeric: true, value: r => r.closed, render: r => RPA.fmtInt(r.closed) },
    { label: 'Acted last on', numeric: true, value: r => r.lastTouched, render: r => RPA.fmtInt(r.lastTouched) },
    { label: 'Still open', numeric: true, value: r => r.openHeld, render: r => RPA.fmtInt(r.openHeld) },
    { label: 'Avg silence', numeric: true, value: r => r.avgSilence, render: r => RPA.fmtDays(r.avgSilence) },
    { label: 'Worst silence', numeric: true, value: r => r.worstSilence, render: r => RPA.fmtDays(r.worstSilence) }
  ];

  /**
   * Opened and closed per day. Oldest first here; reversed for display.
   *
   * The running figure is the backlog's *movement* across the filtered window, started from zero —
   * not the real backlog, which would need the open count on the day before the window and the
   * export does not carry it. Named for what it is, so nobody reads day one as an empty queue.
   */
  function dailyTrend(rows) {
    const days = new Map();

    function day(key) {
      if (!days.has(key)) days.set(key, { day: key, opened: 0, closed: 0 });
      return days.get(key);
    }

    rows.forEach(r => {
      if (r.openedDay) day(r.openedDay).opened += 1;
      if (r.closedDay) day(r.closedDay).closed += 1;
    });

    const ordered = Array.from(days.values()).sort((a, b) => (a.day < b.day ? -1 : 1));
    let running = 0;
    ordered.forEach(d => {
      d.net = d.opened - d.closed;
      running += d.net;
      d.cumulative = running;
    });
    return ordered;
  }

  const dailyColumns = [
    { label: 'Day', filter: 'text', value: r => r.day, render: r => RPA.escapeHtml(r.day) },
    { label: 'Opened', numeric: true, value: r => r.opened, render: r => RPA.fmtInt(r.opened) },
    { label: 'Closed', numeric: true, value: r => r.closed, render: r => RPA.fmtInt(r.closed) },
    {
      label: 'Net', numeric: true, value: r => r.net,
      render: r => (r.net > 0
        ? '<span class="badge red">+' + RPA.fmtInt(r.net) + '</span>'
        : (r.net < 0 ? '<span class="badge green">' + RPA.fmtInt(r.net) + '</span>' : '0'))
    },
    { label: 'Backlog change so far', numeric: true, value: r => r.cumulative, render: r => RPA.fmtInt(r.cumulative) }
  ];

  // ---------------------------------------------------------------------------
  // Charts
  // ---------------------------------------------------------------------------

  function destroyCharts() {
    Object.keys(charts).forEach(id => {
      if (charts[id]) { charts[id].destroy(); charts[id] = null; }
    });
  }

  /** A chart exports as the table it was drawn from, so a card holding only a canvas can still download. */
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

  function vBar(id, labels, data, colors, labelHeader, valueHeader) {
    registerChartExport(id, labelHeader, valueHeader, labels, data);
    const p = RPA.palette();

    charts[id] = new Chart(document.getElementById(id), {
      type: 'bar',
      data: { labels, datasets: [{ data, backgroundColor: colors, borderRadius: 4, maxBarThickness: 48 }] },
      options: {
        maintainAspectRatio: false, responsive: true,
        plugins: { legend: { display: false } },
        scales: {
          y: { beginAtZero: true, grid: { color: p.line }, border: { display: false }, ticks: { maxTicksLimit: 5 } },
          x: { grid: { display: false }, border: { display: false } }
        }
      }
    });
  }

  /** Doughnut with the total in the hole and an HTML legend under it. `slices` are { label, value, color }. */
  function doughnut(id, slices, options) {
    const opts = options || {};
    const p = RPA.palette();
    const shown = slices.filter(s => s.value > 0);
    const colors = shown.map((s, i) => s.color || p.series[i % p.series.length]);
    const total = shown.reduce((sum, s) => sum + s.value, 0);

    registerChartExport(id, opts.labelHeader || 'Label', opts.valueHeader || 'Incidents',
      shown.map(s => s.label), shown.map(s => s.value));

    if (opts.legend) {
      RPA.chartLegend(opts.legend, shown.map((s, i) => ({
        label: s.label,
        color: colors[i],
        value: RPA.fmtInt(s.value) + ' · ' + RPA.fmtPct(total ? s.value / total : 0)
      })));
    }

    charts[id] = new Chart(document.getElementById(id), {
      type: 'doughnut',
      data: {
        labels: shown.map(s => s.label),
        datasets: [{
          data: shown.map(s => s.value),
          backgroundColor: colors,
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
          rpaDoughnutCenter: { value: RPA.fmtInt(total), label: opts.totalLabel },
          tooltip: {
            callbacks: {
              label: ctx => ' ' + RPA.fmtInt(ctx.parsed) + ' · ' + RPA.fmtPct(total ? ctx.parsed / total : 0)
            }
          }
        }
      }
    });
  }

  function trendChart(trend) {
    const p = RPA.palette();
    const labels = trend.map(d => d.day);

    RPA.registerExport('inc-trend-chart', {
      columns: [{ label: 'Day' }, { label: 'Opened', numeric: true }, { label: 'Closed', numeric: true }],
      rows: trend.map(d => [d.day, d.opened, d.closed])
    });

    RPA.chartLegend('inc-trend-chart-legend', [
      { label: 'Opened', color: p.series[0], value: RPA.fmtInt(trend.reduce((s, d) => s + d.opened, 0)) },
      { label: 'Closed', color: p.markGood, value: RPA.fmtInt(trend.reduce((s, d) => s + d.closed, 0)) }
    ]);

    charts['inc-trend-chart'] = new Chart(document.getElementById('inc-trend-chart'), {
      type: 'line',
      data: {
        labels: labels.map(d => d.slice(5)),
        datasets: [
          {
            label: 'Opened', data: trend.map(d => d.opened),
            borderColor: p.series[0], backgroundColor: RPA.alpha(p.series[0], .18),
            fill: true, tension: .32, pointRadius: 0, pointHoverRadius: 4, borderWidth: 2
          },
          {
            label: 'Closed', data: trend.map(d => d.closed),
            borderColor: p.markGood, backgroundColor: RPA.alpha(p.markGood, .12),
            fill: true, tension: .32, pointRadius: 0, pointHoverRadius: 4, borderWidth: 2
          }
        ]
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
  }

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  // A generic product icon — the export carries no image URL for any incident, so every card shows
  // the same placeholder rather than pretending to have art it doesn't.
  const PRODUCT_PLACEHOLDER_SVG =
    '<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" ' +
    'stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
    '<path d="M21 8 12 3 3 8v8l9 5 9-5V8Z"/><path d="M3 8l9 5 9-5"/><path d="M12 13v8"/></svg>';

  /** One incident, as a row-card: pill + reason, seller/order/date meta, product, and a right-side metric. */
  function incidentCardHtml(r, opts) {
    return '<div class="incident-card">' +
      '<div class="incident-card-thumb">' + PRODUCT_PLACEHOLDER_SVG + '</div>' +
      '<div class="incident-card-body">' +
        '<div class="incident-card-top">' +
          lifecycleCell(r) +
          '<span class="incident-card-reason">' + RPA.escapeHtml(r.reason || '-') + '</span>' +
        '</div>' +
        '<div class="incident-card-meta">' +
          '<span>' + RPA.escapeHtml(r.seller || '-') + '</span>' +
          '<span>' + orderCell(r) + '</span>' +
          '<span>' + RPA.escapeHtml(r.openedOn || '-') + '</span>' +
        '</div>' +
        '<div class="incident-card-product">' +
          RPA.escapeHtml(r.product || '-') +
          (r.productSku ? '<span class="cell-sub">SKU ' + RPA.escapeHtml(r.productSku) + '</span>' : '') +
        '</div>' +
      '</div>' +
      '<div class="incident-card-right">' + opts.rightRender(r) + '</div>' +
    '</div>';
  }

  /**
   * Renders one of the four incident queues as row-cards instead of a filterable table — this is the
   * one part of the report meant to be scanned incident-by-incident, so it borrows Mirakl's own
   * incident-panel layout rather than the report's usual grid. The Excel export still works: it is
   * registered from the same rows against a small fixed column set, independent of what is on screen.
   */
  function renderIncidentQueue(wrapperId, rows, opts, emptyMessage) {
    const wrap = document.getElementById(wrapperId);
    if (!wrap) return;

    const exportColumns = [
      { label: 'Opened', value: r => r.openedOn || '' },
      { label: opts.rightLabel, numeric: true, value: opts.rightValue },
      { label: 'Order', value: r => r.orderNumber },
      { label: 'Seller', value: r => r.seller },
      { label: 'Reason', value: r => r.reason }
    ].concat(opts.extraColumns || []).concat([
      { label: 'Product', value: r => r.product },
      { label: 'Product SKU', value: r => r.productSku },
      { label: 'Customer', value: r => r.customerName }
    ]);

    RPA.registerExport(wrapperId, rows.length ? {
      columns: exportColumns,
      rows: rows.map(r => exportColumns.map(c => c.value(r)))
    } : null);

    if (!rows.length) {
      wrap.innerHTML = '<div class="empty-state">' + RPA.escapeHtml(emptyMessage) + '</div>';
      wrap.style.border = 'none';
      RPA.syncExportButtons();
      return;
    }

    wrap.style.border = '';
    wrap.innerHTML = '<div class="incident-card-list">' + rows.map(r => incidentCardHtml(r, opts)).join('') + '</div>';
    RPA.syncExportButtons();
  }

  /** Hides one card without touching the heading above it — for a card that shares a section. */
  function showCard(wrapperId, show) {
    const wrap = document.getElementById(wrapperId);
    const card = wrap ? wrap.closest('.card') : null;
    if (card) card.hidden = !show;
  }

  function renderAll(rows) {
    const open = rows.filter(r => r.lifecycle === OPEN);
    const resolved = rows.filter(r => r.lifecycle === RESOLVED);
    const closed = rows.filter(r => r.lifecycle === CLOSED);
    const openish = rows.filter(isOpenish);

    // Our actual worklist: the seller says it is solved and we have not verified and closed it.
    // Identical to `resolved` by construction — named separately because it is what the panel means.
    const onUs = rows.filter(r => r.waitingOn === US);
    const onUsAges = numbers(onUs, r => r.ageDays);

    const breached = open.filter(r => r.ageDays !== null && r.ageDays >= META.breachDays);
    const warning = open.filter(r => r.ageDays !== null && r.ageDays >= META.warningDays && r.ageDays < META.breachDays);
    const stale = openish.filter(r => r.silenceDays !== null && r.silenceDays >= META.staleDays);
    const hot = openish.filter(r => r.messageCount >= HOT_THREAD_MESSAGES);

    const ages = numbers(open, r => r.ageDays);
    const resolutions = numbers(closed, r => r.resolutionDays);
    const lags = numbers(rows, r => r.orderToIncidentDays);
    const trend = dailyTrend(rows);

    // A long thread has stopped being a question. It is the only escalation signal left now that the
    // order value is gone — and it was always the better of the two.
    const escalation = openish.filter(r => r.messageCount >= HOT_THREAD_MESSAGES);

    // ----- Hero and KPIs -----

    RPA.renderHero('inc-hero', [
      { value: RPA.fmtInt(onUs.length), label: 'Waiting on us', context: 'the seller says it is resolved', tone: onUs.length ? 'amber' : 'green' },
      { value: RPA.fmtInt(open.length), label: 'Open incidents', context: RPA.fmtInt(rows.length) + ' in the filter' },
      { value: ages.length ? avg(ages).toFixed(1) + ' d' : '-', label: 'Average age of an open incident', context: 'oldest ' + (ages.length ? Math.max.apply(null, ages).toFixed(1) + ' d' : '-') },
      { value: RPA.fmtInt(breached.length), label: 'Past ' + META.breachDays + ' days', context: 'open and breached', tone: breached.length ? 'red' : 'green' }
    ], { spark: trend.map(d => d.opened), sparkLabel: 'Opened per day', sparkRange: trend.length ? trend[0].day + ' → ' + trend[trend.length - 1].day : '' });

    RPA.renderKpis('inc-kpis', [
      { group: 'Volume' },
      ['Incidents', RPA.fmtInt(rows.length), '', RPA.fmtInt(distinct(rows, r => r.orderNumber)) + ' distinct orders'],
      ['Open', RPA.fmtInt(open.length), '', 'between the customer and the seller'],
      ['Waiting on us', RPA.fmtInt(onUs.length), onUs.length ? 'amber' : 'green', 'seller resolved it, we have not closed it'],
      ['Closed', RPA.fmtInt(closed.length), 'green'],
      ['Sellers involved', RPA.fmtInt(distinct(rows, r => r.seller)), ''],
      ['Customers involved', RPA.fmtInt(distinct(rows, r => r.customerName)), ''],
      ['Products involved', RPA.fmtInt(distinct(rows, r => r.productSku || r.product)), ''],

      { group: 'Speed' },
      ['Average age', ages.length ? avg(ages).toFixed(1) + ' d' : '-', '', 'open incidents'],
      ['Median age', ages.length ? percentile(ages, .5).toFixed(1) + ' d' : '-', ''],
      ['Oldest open', ages.length ? Math.max.apply(null, ages).toFixed(1) + ' d' : '-', ages.length && Math.max.apply(null, ages) >= META.breachDays ? 'red' : ''],
      ['Average days to close', resolutions.length ? avg(resolutions).toFixed(1) + ' d' : '-', '', RPA.fmtInt(resolutions.length) + ' closed incidents'],
      ['Median days to close', resolutions.length ? percentile(resolutions, .5).toFixed(1) + ' d' : '-', ''],
      ['p90 days to close', resolutions.length ? percentile(resolutions, .9).toFixed(1) + ' d' : '-', '', 'nine in ten close within this'],
      ['Order to incident', lags.length ? avg(lags).toFixed(1) + ' d' : '-', '', 'how long the order lasted before it went wrong'],
      ['Average messages', rows.length ? avg(numbers(rows, r => r.messageCount)).toFixed(1) : '-', '', 'per incident'],

      { group: 'Attention' },
      ['Past ' + META.breachDays + ' days', RPA.fmtInt(breached.length), breached.length ? 'red' : 'green', 'open and breached'],
      ['Past ' + META.warningDays + ' days', RPA.fmtInt(warning.length), warning.length ? 'amber' : 'green', 'open, approaching the breach'],
      ['Silent ' + META.staleDays + '+ days', RPA.fmtInt(stale.length), stale.length ? 'amber' : 'green', 'nobody has touched them'],
      ['Oldest waiting on us', onUsAges.length ? Math.max.apply(null, onUsAges).toFixed(1) + ' d' : '-',
        onUsAges.length && Math.max.apply(null, onUsAges) >= META.breachDays ? 'red' : '', 'longest unverified resolution'],
      ['Long threads', RPA.fmtInt(hot.length), hot.length ? 'red' : '', HOT_THREAD_MESSAGES + '+ messages, still open'],
      ['Cancellation rate', closed.length ? RPA.fmtPct(closed.filter(r => (r.closingReason || '').toLowerCase() === 'canceled').length / closed.length) : '-',
        'red', 'of closed incidents ended as a cancellation'],
      ['Rows needing review', RPA.fmtInt(rows.filter(r => r.issues.length).length), rows.filter(r => r.issues.length).length ? 'amber' : 'green']
    ]);

    // ----- Charts -----

    destroyCharts();
    RPA.applyChartDefaults();
    const p = RPA.palette();

    trendChart(trend);

    doughnut('inc-lifecycle-chart', [
      { label: 'Open', value: open.length, color: p.markWarning },
      { label: 'Waiting on us', value: resolved.length, color: p.series[0] },
      { label: 'Closed', value: closed.length, color: p.markGood }
    ], { labelHeader: 'Lifecycle', totalLabel: 'incidents', legend: 'inc-lifecycle-chart-legend' });

    const reasonRows = reasonBreakdown(rows);
    hBar('inc-reason-chart', reasonRows.map(r => r.reason), reasonRows.map(r => r.total),
      p.series[1], 'Reason', 'Incidents');

    // Our own slice leads and wears the alert colour: it is the only one of the four the team can act on.
    const waitingColors = {
      us: p.markCritical, seller: p.series[1], customer: p.series[0], 'operator-acted': p.series[6]
    };
    const waitingCounts = [US, 'seller', 'customer', 'operator-acted'].map(key => ({
      label: WAITING_LABELS[key],
      value: openish.filter(r => r.waitingOn === key).length,
      color: waitingColors[key]
    }));
    doughnut('inc-waiting-chart', waitingCounts, { labelHeader: 'Waiting on', totalLabel: 'not closed', legend: 'inc-waiting-chart-legend' });
    showCard('inc-waiting-chart', openish.length > 0);

    const bucketCounts = AGE_BUCKETS.map(b => open.filter(r => r.ageDays !== null && r.ageDays >= b.min && r.ageDays < b.max).length);
    const bucketColors = AGE_BUCKETS.map(b =>
      (b.min >= META.breachDays ? p.markCritical : (b.min >= META.warningDays ? p.markWarning : p.markGood)));
    vBar('inc-age-chart', AGE_BUCKETS.map(b => b.label), bucketCounts, bucketColors, 'Age', 'Open incidents');

    // ----- Action lists -----

    document.getElementById('inc-breach-sub').textContent =
      'Open for more than ' + META.breachDays + ' days, still unresolved';
    document.getElementById('inc-stale-sub').textContent =
      'No message, no status change and no closure for ' + META.staleDays + ' days or more';
    document.getElementById('inc-escalation-sub').textContent =
      'Threads that have run to ' + HOT_THREAD_MESSAGES + ' messages or more and are still not closed';
    document.getElementById('inc-us-sub').textContent =
      'The seller has marked these resolved — they stay here until we verify and close them';

    const byAge = (a, b) => (b.ageDays || 0) - (a.ageDays || 0);
    const bySilence = (a, b) => (b.silenceDays || 0) - (a.silenceDays || 0);

    const waitingColumn = { label: 'Waiting on', value: waitingLabel };
    const resolvedAsColumns = [
      { label: 'Resolved as', value: r => r.closingReason },
      { label: 'Status', value: r => r.status }
    ];

    renderIncidentQueue('inc-breach-wrap', [...breached].sort(byAge),
      { rightLabel: 'Age', rightValue: r => r.ageDays, rightRender: ageCell, extraColumns: [waitingColumn] },
      'No open incident is past ' + META.breachDays + ' days.');
    renderIncidentQueue('inc-us-wrap', [...onUs].sort(byAge),
      { rightLabel: 'Age', rightValue: r => r.ageDays, rightRender: ageCell, extraColumns: resolvedAsColumns },
      'Nothing is waiting on us — no seller has an unverified resolution open.');
    renderIncidentQueue('inc-stale-wrap', [...stale].sort(bySilence),
      { rightLabel: 'Silent', rightValue: r => r.silenceDays, rightRender: silenceCell, extraColumns: [waitingColumn] },
      'Every open incident has been touched within the last ' + META.staleDays + ' days.');
    renderIncidentQueue('inc-escalation-wrap', [...escalation].sort(byAge),
      { rightLabel: 'Msgs', rightValue: r => r.messageCount, rightRender: messagesCell, extraColumns: [waitingColumn] },
      'No incident has run to ' + HOT_THREAD_MESSAGES + ' messages.');

    // ----- Scorecards -----

    RPA.renderDataTable('inc-seller-wrap', sellerScorecard(rows), sellerColumns, 'No sellers in the filter.');
    RPA.renderDataTable('inc-reason-wrap', reasonRows, reasonColumns, 'No reasons in the filter.');
    RPA.renderDataTable('inc-closing-wrap', closingBreakdown(closed), closingColumns, 'No closed incidents in the filter.');

    const speed = resolutionSpeed(closed);
    document.getElementById('inc-speed-sub').textContent =
      'Sellers with at least ' + META.minSampleSize + ' closed incidents, slowest first — ' +
      'a seller below that is left out rather than ranked on one case';
    RPA.renderDataTable('inc-speed-wrap', speed, speedColumns,
      'No seller has closed ' + META.minSampleSize + ' incidents in the filter.');

    const products = productHotspots(rows);
    RPA.renderDataTable('inc-product-wrap', products, productColumns, 'No product carries more than one incident.');

    RPA.renderDataTable('inc-actor-wrap', workload(rows), workloadColumns, 'No named accounts in the filter.');
    RPA.renderDataTable('inc-daily-wrap', [...trend].reverse(), dailyColumns, 'No dated incidents in the filter.');

    const review = rows.filter(r => r.issues.length);
    RPA.renderDataTable('inc-review-wrap', review, reviewColumns, 'Every row read cleanly.');

    // ----- Sections that only exist when their data does -----

    RPA.toggleSection('inc-sec-aging', 'inc-breach-wrap', open.length > 0);
    showCard('inc-age-chart', open.length > 0);
    // Kept visible whenever anything is unclosed: an empty queue is the good news worth stating.
    RPA.toggleSection('inc-sec-us', 'inc-us-wrap', openish.length > 0);
    RPA.toggleSection('inc-sec-stale', 'inc-stale-wrap', openish.length > 0);
    RPA.toggleSection('inc-sec-escalation', 'inc-escalation-wrap', openish.length > 0);
    RPA.toggleSection('inc-sec-closing', 'inc-closing-wrap', closed.length > 0);
    RPA.toggleSection('inc-sec-speed', 'inc-speed-wrap', closed.length > 0);
    RPA.toggleSection('inc-sec-product', 'inc-product-wrap', products.length > 0);
    RPA.toggleSection('inc-sec-review', 'inc-review-wrap', review.length > 0);

    document.getElementById('inc-consistency').textContent =
      RPA.fmtInt(rows.length) + ' incidents = ' + RPA.fmtInt(open.length) + ' open + ' +
      RPA.fmtInt(onUs.length) + ' waiting on us + ' + RPA.fmtInt(closed.length) + ' closed · ' +
      RPA.fmtInt(review.length) + ' need review';
  }

  // ---------------------------------------------------------------------------
  // Filtering
  // ---------------------------------------------------------------------------

  /** Fills a <select> with distinct values, keeping the "all" option first. */
  function fillSelect(id, values, allLabel) {
    const select = document.getElementById(id);
    const options = ['<option value="">' + RPA.escapeHtml(allLabel) + '</option>'];

    Array.from(new Set(values.filter(Boolean)))
      .sort((a, b) => a.localeCompare(b, 'tr'))
      .forEach(v => {
        options.push('<option value="' + RPA.escapeHtml(v) + '">' + RPA.escapeHtml(v) + '</option>');
      });

    select.innerHTML = options.join('');
    select.value = '';
  }

  function value(id) { return document.getElementById(id).value; }

  function applyFilter() {
    const openFrom = value('inc-open-from');
    const openTo = value('inc-open-to');
    const closedFrom = value('inc-closed-from');
    const lifecycle = value('inc-lifecycle');
    const status = value('inc-status');
    const reason = value('inc-reason');
    const seller = value('inc-seller');
    const waiting = value('inc-waiting');
    const term = RPA.fold(document.getElementById('inc-search').value.trim());

    // Rows the closed-from bound alone removed. Counted last, after every other filter has had its
    // say, so the figure describes what is missing from the view on screen rather than from the whole
    // upload — "555 hidden" beside a single seller's three incidents would read as that seller's.
    let hiddenByCutoff = 0;

    const filtered = ROWS.filter(r => {
      if (openFrom && (!r.openedDay || r.openedDay < openFrom)) return false;
      if (openTo && (!r.openedDay || r.openedDay > openTo)) return false;

      if (lifecycle && r.lifecycle !== lifecycle) return false;
      if (status && r.status !== status) return false;
      if (reason && r.reason !== reason) return false;
      if (seller && r.seller !== seller) return false;
      if (waiting && r.waitingOn !== waiting) return false;

      if (term) {
        const haystack = RPA.fold([
          r.orderNumber, r.customerName, r.seller, r.product, r.productSku,
          r.openedByUser, r.closedByUser, r.lastActionByUser, r.reason, r.closingReason
        ].join(' '));
        if (haystack.indexOf(term) === -1) return false;
      }

      // The bound applies to closed rows only: an open incident has no closing date and must not be
      // dropped for failing to carry one.
      if (closedFrom && r.lifecycle === CLOSED && (!r.closedDay || r.closedDay < closedFrom)) {
        hiddenByCutoff += 1;
        return false;
      }

      return true;
    });

    const parts = [];
    if (openFrom || openTo) parts.push('opened ' + (openFrom || '…') + ' → ' + (openTo || '…'));
    if (closedFrom) parts.push('closed from ' + closedFrom);
    if (lifecycle) parts.push('lifecycle: ' + lifecycle);
    if (status) parts.push('status: ' + status);
    if (reason) parts.push('reason: ' + reason);
    if (seller) parts.push('seller: ' + seller);
    if (waiting) parts.push('waiting on: ' + (WAITING_LABELS[waiting] || waiting));
    if (term) parts.push('search: "' + document.getElementById('inc-search').value.trim() + '"');

    const context = parts.length ? parts.join(' · ') : 'No filter';

    document.getElementById('inc-filter-summary').textContent =
      RPA.fmtInt(filtered.length) + ' / ' + RPA.fmtInt(ROWS.length) + ' incidents — ' + context +
      (hiddenByCutoff ? ' · ' + RPA.fmtInt(hiddenByCutoff) + ' closed before the cutoff are not shown' : '');

    RPA.setExportContext('Incidents Report · ' + context);

    renderAll(filtered);
    RPA.syncExportButtons();
    RPA.initSectionNav('inc-section-nav', 'inc-results');
  }

  // ---------------------------------------------------------------------------
  // Once per upload
  // ---------------------------------------------------------------------------

  function render(data) {
    ROWS = data.rows || [];
    META = {
      warningDays: data.warningDays,
      breachDays: data.breachDays,
      staleDays: data.staleDays,
      minSampleSize: data.minSampleSize,
      closedFrom: data.closedFrom || '',
      // Only the WhatsApp warnings section reads this — it stamps the messages it renders.
      referenceTime: data.referenceTime || ''
    };

    RPA.resetDataTables();

    // A new upload invalidates whatever chase list was prepared from the previous one. Cleared rather
    // than left on screen, because the messages below it would still quote the old ages.
    resetWarnings();

    fillSelect('inc-lifecycle', [OPEN, RESOLVED, CLOSED], 'All');
    fillSelect('inc-status', ROWS.map(r => r.status), 'All statuses');
    fillSelect('inc-reason', ROWS.map(r => r.reason), 'All reasons');
    fillSelect('inc-seller', ROWS.map(r => r.seller), 'All sellers');

    // Waiting-on is a fixed vocabulary with labels of its own, so it is filled by hand. Ours leads.
    document.getElementById('inc-waiting').innerHTML =
      '<option value="">Anyone</option>' +
      [US, 'seller', 'customer', 'operator-acted'].map(key =>
        '<option value="' + key + '">' + RPA.escapeHtml(WAITING_LABELS[key]) + '</option>').join('');

    document.getElementById('inc-open-from').value = '';
    document.getElementById('inc-open-to').value = '';
    document.getElementById('inc-search').value = '';

    // The closed export is a full history dump; the builder's cutoff is pre-filled rather than
    // applied server-side, so the older incidents stay one date change away.
    document.getElementById('inc-closed-from').value = META.closedFrom;

    RPA.seedDateRange('inc-open-from', 'inc-open-to', ROWS.map(r => r.openedDay));

    const notes = (data.warnings || []).slice();
    notes.push('Read ' + RPA.fmtInt(data.openFileRows) + ' open and ' + RPA.fmtInt(data.closedFileRows) +
      ' closed rows. Ages measured against ' + data.referenceTime +
      (data.dataAsOf ? '; newest action in the export ' + data.dataAsOf : '') + '.');
    document.getElementById('inc-warnings').textContent = notes.join(' ');

    RPA.stamp('inc-stamp');
    document.getElementById('inc-results').hidden = false;
    applyFilter();

    RPA.revealResults('inc-results');
  }

  // ===========================================================================
  // WhatsApp warnings
  //
  // The one part of this panel that acts rather than reports: it chases sellers who have left an
  // incident unanswered. Everything above this line derives figures from ROWS in the browser; from
  // here down the server owns the decisions, because the outcome is a message to an external party
  // that cannot be recalled.
  //
  // Three boundaries are deliberate and should not be "simplified" away:
  //
  //   1. The eligibility rule lives in IncidentWarningBuilder, not here. This file only projects the
  //      rows and posts them. Age is recomputed there against a fresh clock rather than reusing
  //      r.ageDays, which was frozen when the export was uploaded.
  //   2. It reads ROWS, never the filtered set. A send list quietly narrowed by a forgotten date
  //      filter is a seller who is never chased, and nobody would notice.
  //   3. Rendering is driven from prepare() and never from renderAll()/applyFilter(), which re-run on
  //      every keystroke in the search box. Hanging this off them would wipe approved message text
  //      mid-review.
  //
  // The seller → group mapping and the WhatsApp sign-in are shared with Late Order Warnings and are
  // owned by that panel. The badge here is read-only for that reason: both modules drive one Chrome
  // profile, and a second "Clear session" button could wipe it out from under a running batch.
  // ---------------------------------------------------------------------------

  /** The bus/SSE module name. NOT the panel's own module key, which is 'incidents'. */
  const WA_MODULE = 'incident-warnings';

  let waPrepared = null;      // the last IncidentWarningData, so a template edit needs no re-prepare
  let waMessages = [];
  let waStream = null;        // EventSource, opened once the panel has been visited
  let waActivated = false;
  let waRunning = false;
  let waTotal = 0;
  let waMine = false;         // latched on `started`; the bus is shared with every other module
  let waDefaultTemplate = '';
  let waDefaultLineTemplate = '';

  function waEl(id) { return document.getElementById(id); }

  /**
   * The { success, message, data } envelope every /api/incident-warnings endpoint returns.
   *
   * RPA.sendJson cannot be used: it only throws on a non-2xx status and its error reader looks for
   * `error`, never `message`. Same shape and same reason as the helper in settings.js.
   */
  async function waJson(method, url, payload) {
    const init = { method: method };
    if (payload !== undefined) {
      init.headers = { 'Content-Type': 'application/json' };
      init.body = JSON.stringify(payload);
    }

    const response = await fetch(url, init);

    let body;
    try {
      body = await response.json();
    } catch (e) {
      throw new Error('Request failed with status ' + response.status + '.');
    }

    if (!body || body.success !== true) {
      throw new Error((body && body.message) || ('Request failed with status ' + response.status + '.'));
    }

    return body.data;
  }

  // ----- Session badge (read-only) -----

  async function waRefreshStatus() {
    const badge = waEl('inc-wa-session-badge');
    const note = waEl('inc-wa-session-note');

    let status;
    try {
      status = await waJson('GET', '/api/incident-warnings/status');
    } catch (e) {
      badge.className = 'badge red';
      badge.textContent = 'Status unavailable';
      note.textContent = '';
      return;
    }

    // signedIn stays null until something has actually probed the page — "a profile exists" and "the
    // session is live" are different claims and the badge must not conflate them.
    if (status.signedIn === true) {
      badge.className = 'badge green';
      badge.textContent = 'Signed in';
      note.textContent = '';
    } else if (status.signedIn === false) {
      badge.className = 'badge red';
      badge.textContent = 'Signed out';
      note.textContent = 'Scan the QR code from the Late Order Warnings tab — both modules share one WhatsApp profile.';
    } else if (status.hasProfile) {
      badge.className = 'badge amber';
      badge.textContent = 'Profile saved — not checked yet';
      note.textContent = 'Check the session from the Late Order Warnings tab.';
    } else {
      badge.className = 'badge amber';
      badge.textContent = 'No profile — sign in required';
      note.textContent = 'Sign in from the Late Order Warnings tab — both modules share one WhatsApp profile.';
    }

    waSetRunning(status.isRunning, status.runningModule);
  }

  /** Idempotent: the run state arrives from the POST, from /status and from the event stream. */
  function waSetRunning(isRunning, runningModule) {
    waRunning = !!isRunning;

    const send = waEl('inc-wa-send');
    RPA.setBusy(send, waRunning, 'Running…');
    send.disabled = waRunning || waMessages.length === 0;

    if (waRunning && runningModule && runningModule !== WA_MODULE) {
      send.title = 'Another automation run (' + runningModule + ') is using the browser.';
    } else {
      send.removeAttribute('title');
    }

    if (waRunning) waEl('inc-wa-run').hidden = false;
  }

  // ----- Run log -----

  function waAppendLog(message) {
    const box = waEl('inc-wa-console');
    // Follow the tail only while the operator is already at the bottom — scrolling back through a
    // long run must not be yanked away by the next line.
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function waSetProgress(completed) {
    const percent = waTotal > 0 ? Math.round((completed / waTotal) * 100) : 0;
    waEl('inc-wa-progress-fill').style.width = percent + '%';
    waEl('inc-wa-progress').setAttribute('aria-valuenow', String(percent));
    waEl('inc-wa-progress-text').textContent = completed + ' / ' + waTotal;
  }

  /**
   * The bus is shared with Create Return, Product Status and Late Order Warnings, and only the
   * `started` frame names a module — the log lines that follow carry none. So `waMine` is latched
   * there and gates everything after it, or this console would fill with another module's run.
   */
  function waHandleEvent(event) {
    switch (event.type) {
      case 'started':
        waMine = event.module === WA_MODULE;
        if (!waMine) return;
        waTotal = event.total;
        waEl('inc-wa-run').hidden = false;
        waEl('inc-wa-console').textContent = '';
        waEl('inc-wa-progress').classList.remove('is-done');
        waSetProgress(0);
        waSetRunning(true, WA_MODULE);
        break;

      case 'log':
        if (waMine) waAppendLog(event.message);
        break;

      case 'progress':
        if (!waMine) return;
        waTotal = event.total;
        waSetProgress(event.completed);
        break;

      case 'done':
        if (!waMine) return;
        waAppendLog('');
        waAppendLog('Finished. Processed: ' + event.processed + ' · Failed: ' + event.failed.length);
        if (event.failed.length) waAppendLog('Failed groups:\n  ' + event.failed.join('\n  '));
        waEl('inc-wa-progress').classList.add('is-done');
        waSetRunning(false);
        waRefreshStatus();
        break;
    }
  }

  function waConnect() {
    if (waStream) return;

    waStream = new EventSource('/api/automation/events');
    waStream.addEventListener('message', function (message) {
      let payload;
      try {
        payload = JSON.parse(message.data);
      } catch (e) {
        return;
      }
      waHandleEvent(payload);
    });

    // EventSource reconnects on its own and the server replays the current run's log onto the new
    // connection, so a dropped stream needs no recovery here.
    waStream.addEventListener('error', function () { });
  }

  // ----- Tables -----

  const waIncidentColumns = [
    { label: 'Age', numeric: true, value: r => r.ageDays, render: r => '<span class="badge red">' + r.ageDays.toFixed(1) + ' d</span>' },
    { label: 'Opened', value: r => r.openedOn, render: r => RPA.escapeHtml(r.openedOn) },
    { label: 'Order', value: r => r.orderNumber, render: r => RPA.escapeHtml(r.orderNumber || '-') },
    { label: 'Seller', value: r => r.seller, render: r => RPA.escapeHtml(r.seller || '-') },
    { label: 'WhatsApp group', value: r => r.groupName || '', render: r => r.groupName ? RPA.escapeHtml(r.groupName) : '<span class="badge amber">no group</span>' },
    { label: 'Reason', value: r => r.reason, render: r => RPA.escapeHtml(r.reason || '-') },
    { label: 'Status', value: r => r.status, render: r => RPA.escapeHtml(r.status || '-') }
  ];

  const waUnmappedColumns = [
    { label: 'Seller', value: r => r.sellerName, render: r => RPA.escapeHtml(r.sellerName) },
    { label: 'Incidents', numeric: true, value: r => r.incidentCount, render: r => RPA.fmtInt(r.incidentCount) },
    { label: 'Oldest', numeric: true, value: r => r.maxAgeDays, render: r => r.maxAgeDays.toFixed(1) + ' d' },
    {
      label: 'Why', value: r => r.mappingProblem || '',
      render: r => (r.mappingConflict ? '<span class="badge red">conflict</span> ' : '') +
        RPA.escapeHtml(r.mappingProblem || '')
    }
  ];

  const waFunnelColumns = [
    { label: 'Stage', value: r => r.stage, render: r => RPA.escapeHtml(r.stage) },
    { label: 'Incidents', numeric: true, value: r => r.count, render: r => RPA.fmtInt(r.count) },
    { label: 'What it means', value: r => r.note, render: r => RPA.escapeHtml(r.note) }
  ];

  const waReviewColumns = [
    { label: 'Order', value: r => r.orderNumber, render: r => RPA.escapeHtml(r.orderNumber || '-') },
    { label: 'Seller', value: r => r.seller, render: r => RPA.escapeHtml(r.seller || '-') },
    { label: 'Opened on (raw)', value: r => r.openedOn, render: r => RPA.escapeHtml(r.openedOn || '(blank)') },
    { label: 'Why', value: r => r.reason, render: r => RPA.escapeHtml(r.reason) }
  ];

  /** One flat row per incident, for the table and its Excel export. */
  function waIncidentRows(sellers) {
    const rows = [];
    sellers.forEach(seller => {
      seller.incidents.forEach(incident => {
        rows.push({
          ageDays: incident.ageDays,
          openedOn: incident.openedOn,
          orderNumber: incident.orderNumber,
          seller: seller.sellerName,
          groupName: seller.groupName,
          reason: incident.reason,
          status: incident.status
        });
      });
    });
    return rows.sort((a, b) => b.ageDays - a.ageDays);
  }

  function waFunnelRows(data) {
    const f = data.funnel;
    return [
      { stage: 'Incidents in the report', count: f.rowsIn, note: 'Every row from both uploads, before any rule is applied' },
      { stage: 'Closed', count: f.closed, note: 'Already finished' },
      { stage: 'Resolved — waiting on us', count: f.resolvedAwaitingUs, note: 'The seller answered; the verification and closure are ours, so they are not chased' },
      { stage: 'Open, but not the seller\'s turn', count: f.waitingOnOther, note: 'The seller already replied, or the thread is with us' },
      { stage: 'Opened-on date unreadable', count: f.noOpenedDate, note: 'Age unknown — set aside for review, never assumed to be old' },
      { stage: 'Open less than ' + data.thresholdDays + ' day(s)', count: f.belowThreshold, note: 'Under the chase threshold' },
      { stage: 'To chase', count: f.eligible, note: 'Open, ' + data.thresholdDays + '+ days old, and the customer spoke last' },
      { stage: 'Sellers', count: f.sellers, note: 'Distinct sellers behind those incidents' },
      { stage: '— with a WhatsApp group', count: f.mappedSellers, note: 'Can be messaged' },
      { stage: '— with no group', count: f.unmappedSellers, note: 'Add them on the Late Order Warnings tab, by seller name' },
      { stage: '— name mapped twice', count: f.nameConflictSellers, note: 'Ambiguous: the incident export has no seller id to break the tie. Remove the duplicate row' }
    ];
  }

  // ----- Prepare -----

  function resetWarnings() {
    waPrepared = null;
    waMessages = [];
    const prepared = waEl('inc-wa-prepared');
    if (prepared) prepared.hidden = true;
    const send = waEl('inc-wa-send');
    if (send) send.disabled = true;
    const exportBtn = waEl('inc-wa-messages-export');
    if (exportBtn) exportBtn.disabled = true;
  }

  function waRenderPrepared(data) {
    waPrepared = data;

    const notes = (data.warnings || []).slice();
    waEl('inc-wa-warnings').hidden = notes.length === 0;
    waEl('inc-wa-warnings-list').innerHTML = notes.map(w => '<li>' + RPA.escapeHtml(w) + '</li>').join('');

    const unmapped = data.sellers.filter(s => !s.groupName);
    waEl('inc-wa-unmapped').hidden = unmapped.length === 0;
    waEl('inc-wa-unmapped-sub').textContent = unmapped.length
      ? RPA.fmtInt(unmapped.length) + ' seller(s) have chaseable incidents but no WhatsApp group. ' +
        'The incident export carries no seller id, so the mapping is matched on the seller name — ' +
        'a mapping row entered by id alone cannot match here.'
      : '';
    RPA.renderTable('inc-wa-unmapped-wrap', unmapped, waUnmappedColumns, 'Every seller to chase is mapped.');

    RPA.renderTable('inc-wa-incidents-wrap', waIncidentRows(data.sellers), waIncidentColumns,
      'No incident has been open that long with the seller still to reply. Lower the day threshold to widen it.');
    RPA.renderTable('inc-wa-funnel-wrap', waFunnelRows(data), waFunnelColumns, 'Nothing to report.');
    RPA.renderTable('inc-wa-review-wrap', data.review, waReviewColumns,
      'Nothing was set aside — every open incident had a readable opened-on date.');

    RPA.syncExportButtons();

    waEl('inc-wa-threshold').value = data.thresholdDays;
    waEl('inc-wa-prepared').hidden = false;

    return waRenderMessages();
  }

  async function waPrepare() {
    if (!ROWS.length) {
      RPA.showError('inc-wa-prepare-alert', 'Generate the dashboard first — there are no incidents to check.');
      return;
    }
    RPA.clearError('inc-wa-prepare-alert');

    const button = waEl('inc-wa-prepare');
    RPA.setBusy(button, true, 'Working…');
    try {
      // ROWS, not the filtered set — see the boundary note at the top of this region. Projected down
      // to the seven fields the rule and the message need: the customer's name and the product they
      // complained about have no business on this request.
      const rows = ROWS.map(r => ({
        seller: r.seller,
        orderNumber: r.orderNumber,
        openedOn: r.openedOn,
        lifecycle: r.lifecycle,
        waitingOn: r.waitingOn,
        reason: r.reason,
        status: r.status
      }));

      const data = await waJson('POST', '/api/incident-warnings/prepare', {
        rows: rows,
        thresholdDays: Number(waEl('inc-wa-threshold').value) || 2
      });

      await waRenderPrepared(data);
    } catch (err) {
      RPA.showError('inc-wa-prepare-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ----- Messages -----

  function waMessageCardHtml(message, index) {
    return '<div class="msg-card">' +
      '<div class="msg-head">' +
        '<span class="badge green">' + RPA.escapeHtml(message.groupName) + '</span>' +
        '<span class="msg-meta">' + RPA.escapeHtml(message.sellerName) +
          ' · ' + message.orderCount + ' incident(s)' +
          (message.accountCount > 1 ? ' · <span class="badge amber">' + message.accountCount + ' sellers merged</span>' : '') +
          (message.truncated ? ' · <span class="badge amber">truncated</span>' : '') +
          // Flagged here rather than at Send: finding out a message is too long after approving it is
          // the wrong moment, and the template is still editable at this point.
          (message.overLimit ? ' · <span class="badge red">over the character limit</span>' : '') +
        '</span>' +
        '<button type="button" class="btn btn-ghost btn-sm inc-wa-copy" data-index="' + index + '">Copy</button>' +
      '</div>' +
      '<pre class="msg-body">' + RPA.escapeHtml(message.body) + '</pre>' +
    '</div>';
  }

  async function waRenderMessages() {
    if (!waPrepared) return;

    RPA.clearError('inc-wa-messages-alert');
    try {
      const result = await waJson('POST', '/api/incident-warnings/messages', {
        sellers: waPrepared.sellers,
        referenceTime: waPrepared.referenceTime,
        template: waEl('inc-wa-template').value,
        lineTemplate: waEl('inc-wa-line-template').value
      });

      waMessages = result.messages || [];
      waEl('inc-wa-messages').innerHTML = waMessages.length
        ? waMessages.map(waMessageCardHtml).join('')
        : '<div class="empty-state">No message to compose — every seller to chase is unmapped, or nothing is past the threshold.</div>';

      waEl('inc-wa-messages-summary').textContent = waMessages.length
        ? RPA.fmtInt(waMessages.length) + ' message(s) ready'
        : '';
      waEl('inc-wa-messages-export').disabled = waMessages.length === 0;
      waEl('inc-wa-send').disabled = waRunning || waMessages.length === 0;

      if (result.warnings && result.warnings.length) {
        RPA.showError('inc-wa-messages-alert', result.warnings.join(' '));
      }
    } catch (err) {
      RPA.showError('inc-wa-messages-alert', err.message);
    }
  }

  // ----- Settings -----

  async function waLoadSettings() {
    try {
      const settings = await waJson('GET', '/api/incident-warnings/settings');

      waDefaultTemplate = settings.defaultTemplate;
      waDefaultLineTemplate = settings.defaultLineTemplate;

      waEl('inc-wa-template').value = settings.template;
      waEl('inc-wa-line-template').value = settings.lineTemplate;
      waEl('inc-wa-threshold').value = settings.thresholdDays;
      waEl('inc-wa-threshold').min = settings.minThresholdDays;
      waEl('inc-wa-threshold').max = settings.maxThresholdDays;
      waEl('inc-wa-placeholders').textContent = settings.placeholders.join('  ');
      waEl('inc-wa-settings-note').textContent = settings.updatedUtc ? 'Saved ' + settings.updatedUtc : '';
    } catch (err) {
      RPA.showError('inc-wa-prepare-alert', err.message);
    }
  }

  async function waSaveSettings() {
    const button = waEl('inc-wa-save-settings');
    RPA.clearError('inc-wa-messages-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      await waJson('PUT', '/api/incident-warnings/settings', {
        template: waEl('inc-wa-template').value,
        lineTemplate: waEl('inc-wa-line-template').value,
        thresholdDays: Number(waEl('inc-wa-threshold').value) || 2
      });
      await waLoadSettings();
    } catch (err) {
      RPA.showError('inc-wa-messages-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ----- Send -----

  /**
   * The last checkpoint before something irreversible. It names the destinations rather than just
   * counting them, because reading a group name is the only way to notice a wrong mapping — and with
   * no seller id in this export, the mapping was matched on a name.
   */
  function waConfirmSend(dryRun) {
    const names = waMessages.map(m => m.groupName);
    const shown = names.slice(0, 12);
    const rest = names.length - shown.length;

    const heading = dryRun
      ? 'DRY RUN — open ' + names.length + ' group(s), compose and verify, but send nothing?'
      : 'SEND ' + names.length + ' message(s) for real? A WhatsApp message cannot be recalled.';

    const tail = rest > 0
      ? '\n  …and ' + rest + ' more (all of them are listed on the cards above)'
      : '';

    const slotWarning = dryRun
      ? ''
      : '\n\nThis holds the automation slot for roughly ' +
        Math.max(1, Math.round(names.length * 9 / 60)) + ' minute(s); no other automation can run during it.';

    return window.confirm(heading + '\n\n  ' + shown.join('\n  ') + tail + slotWarning);
  }

  async function waSend() {
    if (!waMessages.length) return;

    const dryRun = waEl('inc-wa-dry-run').checked;
    if (!waConfirmSend(dryRun)) return;

    RPA.clearError('inc-wa-messages-alert');

    // Opened before the POST so the first events of the run cannot be missed.
    waConnect();
    waSetRunning(true, WA_MODULE);
    waEl('inc-wa-run').hidden = false;

    try {
      await waJson('POST', '/api/incident-warnings/send', {
        dryRun: dryRun,
        messages: waMessages.map(m => ({
          groupName: m.groupName,
          sellerName: m.sellerName,
          body: m.body
        }))
      });
    } catch (err) {
      RPA.showError('inc-wa-messages-alert', err.message);
      waSetRunning(false);
    }
  }

  /** navigator.clipboard needs a secure context; localhost is one, but keep a fallback anyway. */
  async function waCopyText(text) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch (e) {
      const scratch = document.createElement('textarea');
      scratch.value = text;
      scratch.setAttribute('readonly', '');
      scratch.style.position = 'fixed';
      scratch.style.opacity = '0';
      document.body.appendChild(scratch);
      scratch.select();
      let ok = false;
      try { ok = document.execCommand('copy'); } catch (e2) { ok = false; }
      scratch.remove();
      return ok;
    }
  }

  function waActivate() {
    if (waActivated) return;
    waActivated = true;
    waLoadSettings();
    waConnect();
    waRefreshStatus();
  }

  function waWire() {
    waEl('inc-wa-prepare').addEventListener('click', waPrepare);
    waEl('inc-wa-render').addEventListener('click', waRenderMessages);
    waEl('inc-wa-save-settings').addEventListener('click', waSaveSettings);
    waEl('inc-wa-send').addEventListener('click', waSend);

    waEl('inc-wa-template-reset').addEventListener('click', function () {
      waEl('inc-wa-template').value = waDefaultTemplate;
      waEl('inc-wa-line-template').value = waDefaultLineTemplate;
      waRenderMessages();
    });

    waEl('inc-wa-messages-export').addEventListener('click', async function () {
      const button = waEl('inc-wa-messages-export');
      RPA.clearError('inc-wa-messages-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/incident-warnings/messages/excel',
          { messages: waMessages }, 'incident-warnings.xlsx');
      } catch (err) {
        RPA.showError('inc-wa-messages-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    // Delegated: the cards are re-rendered on every render, so per-card listeners would be lost.
    document.getElementById('panel-incidents').addEventListener('click', async function (event) {
      const copy = event.target.closest('.inc-wa-copy');
      if (!copy) return;

      const message = waMessages[Number(copy.dataset.index)];
      if (!message) return;

      const ok = await waCopyText(message.body);
      copy.textContent = ok ? 'Copied' : 'Copy failed';
      setTimeout(() => { copy.textContent = 'Copy'; }, 1600);
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by the
    // time the listener below exists. Check the tab directly instead of waiting for a repeat.
    //
    // The panel's module key is 'incidents'; WA_MODULE above is the automation bus's name for the
    // run this section starts. They are not interchangeable.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === 'incidents') waActivate();
    });

    if (document.getElementById('tab-incidents').getAttribute('aria-selected') === 'true') waActivate();
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  document.addEventListener('DOMContentLoaded', function () {
    waWire();

    RPA.initDropzone('inc-open-drop', 'inc-open-file');
    RPA.initDropzone('inc-closed-drop', 'inc-closed-file');

    const generateBtn = document.getElementById('inc-generate');

    generateBtn.addEventListener('click', async function () {
      const openFile = document.getElementById('inc-open-file').files[0];
      const closedFile = document.getElementById('inc-closed-file').files[0];

      if (!openFile && !closedFile) {
        RPA.showError('inc-alert', 'Please upload at least one incident export — open, closed, or both.');
        return;
      }
      RPA.clearError('inc-alert');

      const form = new FormData();
      if (openFile) form.append('openIncidents', openFile);
      if (closedFile) form.append('closedIncidents', closedFile);

      RPA.setBusy(generateBtn, true, 'Generating…');
      RPA.showSkeleton('inc-skeleton', 'inc-results');
      try {
        const data = await RPA.postJson('/api/incidents-report/data', form);
        render(data);
      } catch (err) {
        RPA.showError('inc-alert', err.message);
      } finally {
        RPA.hideSkeleton('inc-skeleton');
        RPA.setBusy(generateBtn, false);
      }
    });

    ['inc-open-from', 'inc-open-to', 'inc-closed-from', 'inc-lifecycle', 'inc-status', 'inc-reason',
      'inc-seller', 'inc-waiting'].forEach(id => {
        document.getElementById(id).addEventListener('change', applyFilter);
      });
    document.getElementById('inc-search').addEventListener('input', applyFilter);

    document.getElementById('inc-reset').addEventListener('click', function () {
      ['inc-open-from', 'inc-open-to', 'inc-lifecycle', 'inc-status', 'inc-reason', 'inc-seller',
        'inc-waiting', 'inc-search'].forEach(id => { document.getElementById(id).value = ''; });
      // Reset restores the cutoff rather than clearing it: showing a year of closed history is a
      // deliberate act, not what "reset" should hand back.
      document.getElementById('inc-closed-from').value = META.closedFrom;
      applyFilter();
    });

    // Chart colours are resolved from CSS custom properties at draw time, so a theme change needs a
    // full re-render rather than an update.
    document.addEventListener('rpa:themechange', function () {
      if (ROWS.length && !document.getElementById('inc-results').hidden) applyFilter();
    });
  });

})(window.RPA);
