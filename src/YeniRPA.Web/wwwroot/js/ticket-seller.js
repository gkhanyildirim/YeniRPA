/* =============================================================================
   Ticket → Seller lookup.

   The matching itself happens server-side in TicketSellerBuilder; this file only
   fills the filter dropdowns from whatever came back, narrows the row set, and
   renders. Filtering is client-side so the operator can switch queue or seller
   without re-uploading two files.

   Only HQ cases ever arrive here — the queue restriction is applied by the
   builder, not by the queue dropdown below, which just picks between the HQ
   queues themselves.

   One ticket can produce several rows — a customer order that split across
   sellers is reported once per seller rather than resolved down to a guess — so
   every count here that means "cases" is taken over distinct ticket numbers, not
   over rows.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MATCHED = 'matched';
  const NO_ORDER = 'no-order';
  const NOT_FOUND = 'not-found';

  let ROWS = [];
  let TICKET_COUNT = 0;

  // Appended to the filter summary so a short dashboard never looks like a short upload — the
  // non-HQ cases are dropped server-side and this is the only trace of them.
  let OTHER_QUEUE_NOTE = '';

  // ---------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------

  const distinctTickets = rows => new Set(rows.map(r => r.ticketNo)).size;

  const orderCell = r => {
    const number = RPA.escapeHtml(r.orderNumber || r.sourceOrderNo || '-');
    // A split order is the whole reason a ticket appears twice; say so on the row
    // itself so the repetition never reads as a duplicate.
    return r.matchCount > 1
      ? number + ' <span class="badge amber">' + r.matchCount + ' satıcı</span>'
      : number;
  };

  const sellerCell = r => {
    if (r.matchState === MATCHED) return RPA.escapeHtml(r.seller || '(satıcı adı yok)');
    return '-';
  };

  const stateBadge = r => {
    if (r.matchState === MATCHED) return '<span class="badge green">Eşleşti</span>';
    if (r.matchState === NOT_FOUND) return '<span class="badge amber">Orders\'ta yok</span>';
    return '<span class="badge">Sipariş no yok</span>';
  };

  const columns = [
    { label: 'Vaka No', render: r => RPA.escapeHtml(r.ticketNo), numeric: true },
    { label: 'Sipariş No', render: r => orderCell(r), numeric: true },
    { label: 'Konu', render: r => RPA.escapeHtml(r.subject || '-') },
    { label: 'Satıcı', render: r => sellerCell(r) },
    { label: 'Kuyruk', render: r => RPA.escapeHtml(r.queue || '-') },
    { label: 'Durum', render: r => stateBadge(r) }
  ];

  // ---------------------------------------------------------------------------
  // Filters
  // ---------------------------------------------------------------------------

  /** Fills a <select> with the distinct values of one field, keeping the "all" option first. */
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

  function applyFilter() {
    const queue = document.getElementById('ts-queue').value;
    const seller = document.getElementById('ts-seller').value;
    const term = document.getElementById('ts-search').value.trim().toLowerCase();

    const filtered = ROWS.filter(r => {
      if (queue && r.queue !== queue) return false;
      if (seller && r.seller !== seller) return false;
      if (term) {
        const haystack = [r.ticketNo, r.sourceOrderNo, r.orderNumber, r.subject, r.seller]
          .join(' ').toLowerCase();
        if (haystack.indexOf(term) === -1) return false;
      }
      return true;
    });

    const parts = [];
    if (queue) parts.push('kuyruk: ' + queue);
    if (seller) parts.push('satıcı: ' + seller);
    if (term) parts.push('arama: "' + term + '"');

    const context = parts.length ? parts.join(' · ') : 'Filtre yok';
    document.getElementById('ts-filter-summary').textContent =
      RPA.fmtInt(distinctTickets(filtered)) + ' / ' + RPA.fmtInt(TICKET_COUNT) + ' HQ vakası · ' +
      RPA.fmtInt(filtered.length) + ' satır — ' + context + OTHER_QUEUE_NOTE;
    RPA.setExportContext(context);

    renderAll(filtered);
  }

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  function renderAll(rows) {
    const matched = rows.filter(r => r.matchState === MATCHED);
    const notFound = rows.filter(r => r.matchState === NOT_FOUND);
    const noOrder = rows.filter(r => r.matchState === NO_ORDER);
    const split = matched.filter(r => r.matchCount > 1);

    RPA.renderKpis('ts-kpis', [
      ['HQ vakası', RPA.fmtInt(distinctTickets(rows)), ''],
      ['Satıcısı bulunan', RPA.fmtInt(distinctTickets(matched)), 'green'],
      ['Orders\'ta bulunamayan', RPA.fmtInt(distinctTickets(notFound)), 'amber'],
      ['Sipariş no\'suz', RPA.fmtInt(distinctTickets(noOrder)), ''],
      ['Birden fazla satıcıya bölünen', RPA.fmtInt(distinctTickets(split)), 'amber'],
      ['Farklı satıcı', RPA.fmtInt(new Set(matched.map(r => r.seller).filter(Boolean)).size), '']
    ]);

    RPA.renderTable('ts-all-wrap', rows, columns, 'Filtreye uyan HQ vakası yok.');
    RPA.renderTable('ts-open-wrap', notFound.concat(noOrder), columns,
      'Her HQ vakasının siparişi orders dosyasında bulundu.');

    RPA.syncExportButtons();
  }

  function render(data) {
    ROWS = data.rows || [];
    TICKET_COUNT = data.ticketCount || 0;
    OTHER_QUEUE_NOTE = data.otherQueueCount
      ? ' · ' + RPA.fmtInt(data.otherQueueCount) + ' vaka HQ dışı kuyrukta olduğu için listelenmedi'
      : '';

    fillSelect('ts-queue', ROWS.map(r => r.queue), 'Tüm HQ kuyrukları');
    fillSelect('ts-seller', ROWS.filter(r => r.matchState === MATCHED).map(r => r.seller), 'Tüm satıcılar');
    document.getElementById('ts-search').value = '';

    RPA.stamp('ts-stamp');
    document.getElementById('ts-results').hidden = false;
    applyFilter();
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('ts-tickets-drop', 'ts-tickets-file');
    RPA.initDropzone('ts-orders-drop', 'ts-orders-file');

    const generateBtn = document.getElementById('ts-generate');

    generateBtn.addEventListener('click', async function () {
      const tickets = document.getElementById('ts-tickets-file').files[0];
      const orders = document.getElementById('ts-orders-file').files[0];

      if (!tickets) {
        RPA.showError('ts-alert', 'Lütfen vaka listesini yükleyin.');
        return;
      }
      if (!orders) {
        RPA.showError('ts-alert', 'Lütfen orders export dosyasını yükleyin.');
        return;
      }
      RPA.clearError('ts-alert');

      const form = new FormData();
      form.append('tickets', tickets);
      form.append('orders', orders);

      RPA.setBusy(generateBtn, true, 'Eşleştiriliyor…');
      RPA.showSkeleton('ts-skeleton', 'ts-results');
      try {
        const data = await RPA.postJson('/api/ticket-seller/data', form);
        render(data);
      } catch (err) {
        RPA.showError('ts-alert', err.message);
      } finally {
        RPA.hideSkeleton('ts-skeleton');
        RPA.setBusy(generateBtn, false);
      }
    });

    ['ts-queue', 'ts-seller'].forEach(id => {
      document.getElementById(id).addEventListener('change', applyFilter);
    });
    document.getElementById('ts-search').addEventListener('input', applyFilter);
    document.getElementById('ts-reset').addEventListener('click', function () {
      document.getElementById('ts-queue').value = '';
      document.getElementById('ts-seller').value = '';
      document.getElementById('ts-search').value = '';
      applyFilter();
    });
  });

})(window.RPA);
