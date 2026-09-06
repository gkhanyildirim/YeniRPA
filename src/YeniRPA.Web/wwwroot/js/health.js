/* =============================================================================
   System Health — a standalone page (not a Home/Index tab) reading the
   SystemLog rows SystemLogActionFilter and AutomationJobBus write.

   Self-contained on purpose: this page does not load app.js. That file's
   DOMContentLoaded handler wires up the report shell — the rail, the tab
   list, the theme toggle — none of which exists here, so pulling it in would
   mean auditing every one of its startup calls for "does this still no-op
   safely with #shell/#rail missing" instead of just not depending on it.
   The couple of helpers this page needs (setBusy) are small enough to keep
   local rather than factor app.js's shell-specific code into a shared file
   for one page's sake.

   Every fetch here reads the { success, message, data } envelope new
   endpoints in this app use (see CLAUDE.md) — this page was written after
   that rule existed, so it never has to translate an older { error } shape.
   ============================================================================= */

(function () {
  'use strict';

  const PAGE_SIZE = 25;

  let page = 1;
  let totalCount = 0;

  function el(id) { return document.getElementById(id); }

  /** Same shape as app.js's RPA.setBusy, kept local — see the file header. */
  function setBusy(button, busy, busyLabel) {
    if (!button) return;
    const text = button.querySelector('.btn-text');
    if (busy) {
      button.dataset.idleLabel = text.textContent;
      if (busyLabel) text.textContent = busyLabel;
      button.classList.add('is-busy');
      button.disabled = true;
    } else {
      if (button.dataset.idleLabel) text.textContent = button.dataset.idleLabel;
      button.classList.remove('is-busy');
      button.disabled = false;
    }
  }

  async function getJson(url) {
    const response = await fetch(url);
    const body = await response.json();
    if (!response.ok || !body || body.success !== true) {
      throw new Error((body && body.message) || ('Request failed with status ' + response.status + '.'));
    }
    return body.data;
  }

  function formatTime(iso) {
    const date = new Date(iso);
    if (isNaN(date.getTime())) return iso;
    return date.toLocaleString('tr-TR', {
      day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit'
    });
  }

  function statusBadge(status) {
    const cls = status === 'Success' ? 'green' : 'red';
    const label = status === 'Success' ? 'Başarılı' : 'Hatalı';
    return '<span class="badge ' + cls + '">' + label + '</span>';
  }

  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text == null ? '' : String(text);
    return div.innerHTML;
  }

  // ---------------------------------------------------------------------------
  // Summary tiles
  // ---------------------------------------------------------------------------

  async function loadSummary() {
    try {
      const summary = await getJson('/Health/Summary');
      el('health-kpi-total').textContent = summary.total.toLocaleString('tr-TR');
      el('health-kpi-success').textContent = summary.success.toLocaleString('tr-TR');
      el('health-kpi-error').textContent = summary.error.toLocaleString('tr-TR');
      el('health-kpi-rate').textContent = summary.total > 0 ? summary.successRatePercent + '%' : '—';
    } catch (e) {
      // The table below will already be showing the same failure; the tiles just stay at their
      // last known value rather than each popping their own alert.
    }
  }

  async function loadCategories() {
    try {
      const categories = await getJson('/Health/Categories');
      const select = el('health-filter-category');
      const current = select.value;
      select.querySelectorAll('option:not(:first-child)').forEach(o => o.remove());
      categories.forEach(function (name) {
        const option = document.createElement('option');
        option.value = name;
        option.textContent = name;
        select.appendChild(option);
      });
      select.value = current;
    } catch (e) { /* the "Tümü" option alone still lets the table load */ }
  }

  // ---------------------------------------------------------------------------
  // Log table
  // ---------------------------------------------------------------------------

  function rowsUrl() {
    const params = new URLSearchParams();
    const category = el('health-filter-category').value;
    const status = el('health-filter-status').value;
    const search = el('health-filter-search').value.trim();

    if (category) params.set('category', category);
    if (status) params.set('status', status);
    if (search) params.set('search', search);
    params.set('page', String(page));
    params.set('pageSize', String(PAGE_SIZE));

    return '/Health/Logs?' + params.toString();
  }

  function renderRows(items) {
    const body = el('health-log-body');
    body.innerHTML = '';

    if (items.length === 0) {
      body.innerHTML = '<tr><td colspan="6" class="empty-cell">Kayıt bulunamadı.</td></tr>';
      return;
    }

    items.forEach(function (row) {
      const tr = document.createElement('tr');
      const hasDetail = row.detail && row.detail.trim().length > 0;

      tr.innerHTML =
        '<td class="mono">' + formatTime(row.timestampUtc) + '</td>' +
        '<td>' + escapeHtml(row.category) + '</td>' +
        '<td>' + escapeHtml(row.operation) + '</td>' +
        '<td>' + statusBadge(row.status) + '</td>' +
        '<td class="num mono">' + row.durationMs.toLocaleString('tr-TR') + '</td>' +
        '<td>' + (hasDetail
          ? '<button type="button" class="btn btn-ghost btn-sm" data-detail-index>Görüntüle</button>'
          : '<span class="cell-sub">—</span>') + '</td>';

      if (hasDetail) {
        tr.querySelector('[data-detail-index]').addEventListener('click', function () {
          openDetail(row);
        });
      }

      body.appendChild(tr);
    });
  }

  function openDetail(row) {
    el('health-detail-title').textContent = row.category + ' · ' + row.operation;
    el('health-detail-meta').textContent =
      formatTime(row.timestampUtc) + ' · ' + row.status + ' · ' + row.durationMs + ' ms';
    el('health-detail-body').textContent = row.detail || '';
    el('health-detail-dialog').showModal();
  }

  async function loadLogs() {
    const button = el('health-refresh');
    setBusy(button, true, 'Yenileniyor…');

    try {
      const result = await getJson(rowsUrl());
      totalCount = result.totalCount;
      renderRows(result.items);

      const lastPage = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
      el('health-count').textContent = totalCount.toLocaleString('tr-TR') + ' kayıt';
      el('health-page-indicator').textContent = 'Sayfa ' + page + ' / ' + lastPage;
      el('health-prev').disabled = page <= 1;
      el('health-next').disabled = page >= lastPage;
    } catch (e) {
      el('health-log-body').innerHTML =
        '<tr><td colspan="6" class="empty-cell">Yüklenemedi: ' + escapeHtml(e.message) + '</td></tr>';
    } finally {
      setBusy(button, false);
    }
  }

  function refreshAll() {
    page = 1;
    loadSummary();
    loadLogs();
  }

  document.addEventListener('DOMContentLoaded', function () {
    loadCategories();
    refreshAll();

    el('health-refresh').addEventListener('click', refreshAll);
    el('health-filter-category').addEventListener('change', function () { page = 1; loadLogs(); });
    el('health-filter-status').addEventListener('change', function () { page = 1; loadLogs(); });

    let searchTimer = null;
    el('health-filter-search').addEventListener('input', function () {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(function () { page = 1; loadLogs(); }, 350);
    });

    el('health-prev').addEventListener('click', function () {
      if (page > 1) { page -= 1; loadLogs(); }
    });
    el('health-next').addEventListener('click', function () {
      if (page * PAGE_SIZE < totalCount) { page += 1; loadLogs(); }
    });
  });

})();
