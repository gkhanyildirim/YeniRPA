/* =============================================================================
   Return SLA Report dashboard.

   All SLA arithmetic (elapsed days, slaMissed, pastWarning, isConfirmedReturn,
   refund time) happens server-side in ReturnSlaReportBuilder — this file only
   groups, filters and renders. The filters and the six KPIs are a direct port of
   the old server-generated dashboard, translated to English.

   Note that pastWarning is deliberately NOT gated on isConfirmedReturn, matching
   the original: a completed return sitting at 12 days still counts as a warning
   row, but the green "Return completed" badge wins in the status column.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const UNMATCHED_STATUS = 'Not matched in orders file';

  let ROWS = [];
  let PAYMENT_ROWS = [];

  // ---------------------------------------------------------------------------
  // Columns
  // ---------------------------------------------------------------------------

  const statusBadge = r => {
    if (r.isConfirmedReturn) return '<span class="badge green">Return completed</span>';
    if (r.slaMissed) return '<span class="badge red">SLA breached</span>';
    if (r.pastWarning) return '<span class="badge amber">10-day warning</span>';
    return '<span class="badge">' + RPA.escapeHtml(r.status || '-') + '</span>';
  };

  const baseColumns = [
    { label: 'Source', render: r => RPA.escapeHtml(r.source) },
    { label: 'Order number', render: r => RPA.escapeHtml(r.orderNumber), numeric: true },
    { label: 'Seller', render: r => RPA.escapeHtml(r.seller) },
    { label: 'Status', render: r => statusBadge(r) },
    { label: 'Shipped to seller', render: r => RPA.escapeHtml(r.shippedToSellerDate || '-'), numeric: true },
    { label: 'Elapsed', render: r => RPA.fmtDays(r.elapsedDays), numeric: true },
    { label: 'Reason / detail', render: r => RPA.escapeHtml(r.reason || '-') }
  ];

  const paymentColumns = [
    { label: 'Order number', render: r => RPA.escapeHtml(r.orderNumber), numeric: true },
    { label: 'Seller', render: r => RPA.escapeHtml(r.seller) },
    { label: 'Status', render: r => RPA.escapeHtml(r.status) },
    { label: 'Amount', render: r => RPA.fmtMoney(r.amount, r.currency), numeric: true },
    { label: 'Order date', render: r => RPA.escapeHtml(r.dateCreated), numeric: true },
    { label: 'Debit date', render: r => RPA.escapeHtml(r.debitDate), numeric: true },
    { label: 'Refund time', render: r => RPA.fmtDays(r.paymentDays), numeric: true }
  ];

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  function renderAll(rows, paymentRows) {
    const overdue = rows.filter(r => r.slaMissed);
    const warning = rows.filter(r => r.pastWarning);
    const confirmedReturns = rows.filter(r => r.isConfirmedReturn);
    const notMatched = rows.filter(r => r.status === UNMATCHED_STATUS);

    RPA.renderKpis('return-kpis', [
      ['Total return records', RPA.fmtInt(rows.length), ''],
      ['SLA breached', RPA.fmtInt(overdue.length), 'red'],
      ['10-day warning', RPA.fmtInt(warning.length), 'amber'],
      ['Completed returns', RPA.fmtInt(confirmedReturns.length), 'green'],
      ['Unmatched records', RPA.fmtInt(notMatched.length), 'amber'],
      ['Refund payment records', RPA.fmtInt(paymentRows.length), '']
    ]);

    RPA.renderTable('overdue-wrap', overdue, baseColumns, 'No SLA-breached orders found.');
    RPA.renderTable('warning-wrap', warning, baseColumns, 'No orders past the 10-day mark.');
    RPA.renderTable('payment-wrap', paymentRows, paymentColumns,
      'No canceled or returned orders with a known debit date.');
    RPA.renderTable('all-wrap', rows, baseColumns, 'No matched return records found.');
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

    document.getElementById('return-filter-summary').textContent = (from || to)
      ? 'Showing ' + filteredRows.length.toLocaleString('en-US') + ' of ' +
        ROWS.length.toLocaleString('en-US') + ' return records'
      : 'Showing all ' + ROWS.length.toLocaleString('en-US') + ' return records';

    renderAll(filteredRows, filteredPayments);
  }

  function render(data) {
    ROWS = data.rows || [];
    PAYMENT_ROWS = data.payments || [];

    document.getElementById('return-date-from').value = '';
    document.getElementById('return-date-to').value = '';
    RPA.seedDateRange('return-date-from', 'return-date-to', ROWS.map(r => r.shippedToSellerDate));
    RPA.stamp('return-stamp');

    document.getElementById('return-results').hidden = false;
    applyFilter();
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

      if (!orders || !templateA || !templateB) {
        RPA.showError('return-alert',
          'Please upload all three files: the orders export, return template A and return template B.');
        return;
      }
      RPA.clearError('return-alert');

      const form = new FormData();
      form.append('orders', orders);
      form.append('templateA', templateA);
      form.append('templateB', templateB);

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
