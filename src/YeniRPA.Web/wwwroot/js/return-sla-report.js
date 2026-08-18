/* =============================================================================
   Return SLA Report dashboard.

   All SLA arithmetic (elapsed days, slaMissed, pastWarning, isConfirmedReturn,
   refund time) happens server-side in ReturnSlaReportBuilder — this file only
   groups, filters and renders.

   Each row now carries how it resolved against the orders export: `matchState`
   is 'matched', 'matched-by-status' (a bare number that hit several full ones
   which all agree), 'ambiguous' or 'not-found'. Only the first two can carry an
   SLA verdict, so the last two are listed on their own for review rather than
   counted as breaches — a return whose order we cannot find has no status to be
   late against.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MATCHED = 'matched';
  const MATCHED_BY_STATUS = 'matched-by-status';
  const AMBIGUOUS = 'ambiguous';
  const NOT_FOUND = 'not-found';

  const isResolved = r => r.matchState === MATCHED || r.matchState === MATCHED_BY_STATUS;

  let ROWS = [];
  let PAYMENT_ROWS = [];

  // Set at upload time and appended to the filter summary, so a dashboard built from a single
  // template never looks like a complete one that happens to be short of rows.
  let MISSING_NOTE = '';

  const missingNote = (templateA, templateB) => {
    if (!templateA) return ' — template A was not uploaded, so return-request rows are not included';
    if (!templateB) return ' — template B (MP) was not uploaded, so MP rows are not included';
    return '';
  };

  // ---------------------------------------------------------------------------
  // Columns
  // ---------------------------------------------------------------------------

  /**
   * The verdict badge and the order's own Mirakl status, side by side. The badge alone hid the very
   * thing an operator checks by hand — "what does the orders file actually say about this order?"
   */
  const statusCell = r => {
    const verdict = r.isConfirmedReturn ? '<span class="badge green">Return completed</span>'
      : r.slaMissed ? '<span class="badge red">SLA breached</span>'
      : r.pastWarning ? '<span class="badge amber">10-day warning</span>'
      : r.matchState === NOT_FOUND ? '<span class="badge amber">Not in orders</span>'
      : r.matchState === AMBIGUOUS ? '<span class="badge amber">Ambiguous</span>'
      : '<span class="badge">Return open</span>';

    return verdict + ' <span class="cell-sub">' + RPA.escapeHtml(r.status || '-') + '</span>';
  };

  /**
   * The full Mirakl number the bare template number resolved to. A bare number can hit both halves
   * of a split order, so the count is shown when it did.
   */
  const orderCell = r => {
    const number = RPA.escapeHtml(r.orderNumber || '-');
    return r.matchCount > 1
      ? number + ' <span class="badge amber">' + r.matchCount + ' matches</span>'
      : number;
  };

  const baseColumns = [
    { label: 'Source', filter: 'select', render: r => RPA.escapeHtml(r.source) },
    { label: 'Order number', numeric: true, filter: 'text',
      value: r => r.orderNumber, render: r => orderCell(r) },
    { label: 'Source no', numeric: true, filter: 'text',
      value: r => r.sourceOrderNumber, render: r => RPA.escapeHtml(r.sourceOrderNumber || '-') },
    { label: 'Seller', filter: 'text', value: r => r.seller, render: r => RPA.escapeHtml(r.seller) },
    { label: 'Status', filter: 'text', value: r => r.status, render: r => statusCell(r) },
    { label: 'Shipped to seller', numeric: true, value: r => r.shippedToSellerDate || '',
      render: r => RPA.escapeHtml(r.shippedToSellerDate || '-') },
    { label: 'Elapsed', numeric: true, filter: 'number',
      value: r => (r.elapsedDays === null || r.elapsedDays === undefined ? null : r.elapsedDays),
      render: r => RPA.fmtDays(r.elapsedDays) },
    { label: 'Reason / detail', filter: 'text', render: r => RPA.escapeHtml(r.reason || '-') }
  ];

  // Why a row could not be given an SLA verdict — the whole point of the review list.
  const reviewColumns = baseColumns.slice(0, 4).concat([
    { label: 'Why', filter: 'select', value: r => reviewReason(r), render: r => RPA.escapeHtml(reviewReason(r)) },
    { label: 'Shipped to seller', numeric: true, value: r => r.shippedToSellerDate || '',
      render: r => RPA.escapeHtml(r.shippedToSellerDate || '-') },
    { label: 'Elapsed', numeric: true, filter: 'number',
      value: r => (r.elapsedDays === null || r.elapsedDays === undefined ? null : r.elapsedDays),
      render: r => RPA.fmtDays(r.elapsedDays) }
  ]);

  const reviewReason = r => r.matchState === NOT_FOUND
    ? 'Order number is not in the uploaded orders export'
    : 'Bare number matches ' + r.matchCount + ' orders whose statuses disagree';

  const paymentColumns = [
    { label: 'Order number', numeric: true, filter: 'text', value: r => r.orderNumber,
      render: r => RPA.escapeHtml(r.orderNumber) },
    { label: 'Seller', filter: 'text', value: r => r.seller, render: r => RPA.escapeHtml(r.seller) },
    { label: 'Status', filter: 'select', value: r => r.status, render: r => RPA.escapeHtml(r.status) },
    { label: 'Amount', numeric: true, filter: 'number', value: r => r.amount,
      render: r => RPA.fmtMoney(r.amount, r.currency) },
    { label: 'Order date', numeric: true, value: r => r.dateCreated, render: r => RPA.escapeHtml(r.dateCreated) },
    { label: 'Debit date', numeric: true, value: r => r.debitDate, render: r => RPA.escapeHtml(r.debitDate) },
    { label: 'Refund time', numeric: true, filter: 'number', value: r => r.paymentDays,
      render: r => RPA.fmtDays(r.paymentDays) }
  ];

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  function renderAll(rows, paymentRows) {
    const resolved = rows.filter(isResolved);
    const overdue = rows.filter(r => r.slaMissed);
    const warning = rows.filter(r => r.pastWarning);
    const confirmedReturns = rows.filter(r => r.isConfirmedReturn);
    const review = rows.filter(r => !isResolved(r));
    const open = resolved.filter(r => !r.isConfirmedReturn);

    RPA.renderKpis('return-kpis', [
      { group: 'Records' },
      ['Return records', RPA.fmtInt(rows.length), '', 'rows with a real tracking code'],
      ['Matched to an order', RPA.fmtInt(resolved.length), resolved.length === rows.length ? 'green' : '',
        RPA.fmtInt(review.length) + ' could not be resolved'],
      ['Refund payment records', RPA.fmtInt(paymentRows.length), '', 'from the orders file alone'],
      { group: 'Returns still open' },
      ['SLA breached', RPA.fmtInt(overdue.length), overdue.length ? 'red' : 'green',
        'more than ' + (rows.length && rows[0].slaDays ? rows[0].slaDays : 15) + ' days, still open'],
      ['10-day warning', RPA.fmtInt(warning.length), warning.length ? 'amber' : 'green',
        'between 10 and 15 days'],
      ['Open returns', RPA.fmtInt(open.length), '', 'matched, no closing status yet'],
      { group: 'Closed & unresolved' },
      ['Completed returns', RPA.fmtInt(confirmedReturns.length), 'green',
        'order refused / canceled / refunded / rejected'],
      ['Needs review', RPA.fmtInt(review.length), review.length ? 'amber' : 'green',
        'not in the export, or ambiguous']
    ], {
      exportRows: [
        ['Return records', RPA.fmtInt(rows.length)],
        ['Matched to an order', RPA.fmtInt(resolved.length)],
        ['SLA breached', RPA.fmtInt(overdue.length)],
        ['10-day warning', RPA.fmtInt(warning.length)],
        ['Open returns', RPA.fmtInt(open.length)],
        ['Completed returns', RPA.fmtInt(confirmedReturns.length)],
        ['Needs review', RPA.fmtInt(review.length)],
        ['Refund payment records', RPA.fmtInt(paymentRows.length)]
      ]
    });

    RPA.renderDataTable('overdue-wrap', overdue, baseColumns,
      'No SLA-breached returns — every return past 15 days has a closing status on its order.');
    RPA.renderDataTable('warning-wrap', warning, baseColumns, 'No open returns past the 10-day mark.');
    RPA.renderDataTable('review-wrap', review, reviewColumns,
      'Every return record was matched to an order in the export.');
    RPA.renderDataTable('payment-wrap', paymentRows, paymentColumns,
      'No canceled or returned orders with a known debit date.');
    RPA.renderDataTable('all-wrap', rows, baseColumns, 'No return records found.', { maxRows: 500 });

    // The four buckets partition the record set; if they ever stop adding up, the report is lying.
    const summary = document.getElementById('return-consistency');
    if (summary) {
      const accounted = confirmedReturns.length + overdue.length + warning.length +
        open.filter(r => !r.slaMissed && !r.pastWarning).length + review.length;
      summary.textContent = RPA.fmtInt(rows.length) + ' return records = ' +
        RPA.fmtInt(confirmedReturns.length) + ' completed + ' +
        RPA.fmtInt(overdue.length) + ' breached + ' +
        RPA.fmtInt(warning.length) + ' warned + ' +
        RPA.fmtInt(open.filter(r => !r.slaMissed && !r.pastWarning).length) + ' open within SLA + ' +
        RPA.fmtInt(review.length) + ' to review' +
        (accounted === rows.length ? '' : ' — MISMATCH, ' + RPA.fmtInt(accounted) + ' accounted for');
    }
  }

  function applyFilter() {
    const fromVal = document.getElementById('return-date-from').value;
    const toVal = document.getElementById('return-date-to').value;
    const from = fromVal ? new Date(fromVal + 'T00:00:00') : null;
    const to = toVal ? new Date(toVal + 'T23:59:59') : null;

    let filteredRows = ROWS;
    let filteredPayments = PAYMENT_ROWS;

    if (from || to) {
      filteredRows = ROWS.filter(r => {
        if (!r.shippedToSellerDate) return false;
        const d = new Date(r.shippedToSellerDate);
        if (from && d < from) return false;
        if (to && d > to) return false;
        return true;
      });
      filteredPayments = PAYMENT_ROWS.filter(r => {
        if (!r.dateCreated) return false;
        const d = new Date(r.dateCreated);
        if (from && d < from) return false;
        if (to && d > to) return false;
        return true;
      });
    }

    const summary = ((from || to)
      ? 'Showing ' + filteredRows.length.toLocaleString('en-US') + ' of ' +
        ROWS.length.toLocaleString('en-US') + ' return records'
      : 'Showing all ' + ROWS.length.toLocaleString('en-US') + ' return records') + MISSING_NOTE;

    document.getElementById('return-filter-summary').textContent = summary;

    // Printed under the title of every sheet exported from here, so a download says which slice of
    // the upload it came from.
    RPA.setExportContext('Return SLA Report · ' +
      ((from || to) ? 'Shipped ' + (fromVal || '…') + ' → ' + (toVal || '…') + ' · ' : '') + summary);

    renderAll(filteredRows, filteredPayments);
    RPA.syncExportButtons();
  }

  function render(data) {
    ROWS = data.rows || [];
    PAYMENT_ROWS = data.payments || [];

    document.getElementById('return-date-from').value = '';
    document.getElementById('return-date-to').value = '';
    RPA.resetDataTables();
    RPA.seedDateRange('return-date-from', 'return-date-to', ROWS.map(r => r.shippedToSellerDate));
    RPA.stamp('return-stamp');

    document.getElementById('return-results').hidden = false;
    applyFilter();

    RPA.initSectionNav('return-section-nav', 'return-results');
    RPA.revealResults('return-results');
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('return-orders-drop', 'return-orders-file');
    RPA.initDropzone('return-templateA-drop', 'return-templateA-file');
    RPA.initDropzone('return-templateB-drop', 'return-templateB-file');

    const generateBtn = document.getElementById('return-generate');

    generateBtn.addEventListener('click', async function () {
      const orders = document.getElementById('return-orders-file').files[0];
      const templateA = document.getElementById('return-templateA-file').files[0];
      const templateB = document.getElementById('return-templateB-file').files[0];

      if (!orders) {
        RPA.showError('return-alert', 'Please upload the orders export.');
        return;
      }
      // Either template may be left out — the report is then built from whichever one is present.
      if (!templateA && !templateB) {
        RPA.showError('return-alert',
          'Please upload at least one return template: A (Marketplace Iade & Degisim Talepleri) or B (NNNNNN-MP.csv).');
        return;
      }
      RPA.clearError('return-alert');

      const form = new FormData();
      form.append('orders', orders);
      if (templateA) form.append('templateA', templateA);
      if (templateB) form.append('templateB', templateB);

      MISSING_NOTE = missingNote(templateA, templateB);

      RPA.setBusy(generateBtn, true, 'Generating…');
      RPA.showSkeleton('return-skeleton', 'return-results');
      try {
        const data = await RPA.postJson('/api/return-sla-report/data', form);
        render(data);
      } catch (err) {
        RPA.showError('return-alert', err.message);
      } finally {
        RPA.hideSkeleton('return-skeleton');
        RPA.setBusy(generateBtn, false);
      }
    });

    document.getElementById('return-apply').addEventListener('click', applyFilter);
    document.getElementById('return-reset').addEventListener('click', function () {
      document.getElementById('return-date-from').value = '';
      document.getElementById('return-date-to').value = '';
      applyFilter();
    });
  });

})(window.RPA);
