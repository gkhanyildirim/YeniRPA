/* =============================================================================
   Late Order Warnings — overdue orders, grouped by seller, ready to message.

   Phase 1: everything up to and including the composed message text. Nothing is
   sent from here yet; each message carries a Copy button so the operator can
   paste it into WhatsApp by hand. The automated send path lands in a later phase
   and will reuse the same rendered bodies, byte for byte.

   The prepare step returns rows and the messages step renders text from them, so
   editing the template re-posts a few KB rather than the ~13 MB export again.
   Sending drives WhatsApp Web in a separate Chrome window on the server side;
   progress arrives over the shared /api/automation/events stream.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'late-orders';

  let activated = false;
  let stream = null;       // EventSource, once the panel has been visited
  let total = 0;
  let running = false;

  // The prepared payload, held here so a template edit can re-render without a re-upload.
  let lastData = null;
  let lastMessages = [];

  // What the server says "Reset to default" should restore.
  let defaultTemplate = '';
  let defaultOrderLineTemplate = '';

  function el(id) { return document.getElementById(id); }

  // ---------------------------------------------------------------------------
  // WhatsApp session
  // ---------------------------------------------------------------------------

  async function refreshStatus() {
    const badge = el('lo-session-badge');

    let status;
    try {
      const response = await fetch('/api/late-orders/status');
      if (!response.ok) throw new Error();
      status = await response.json();
    } catch (e) {
      badge.className = 'badge red';
      badge.textContent = 'Status unavailable';
      return;
    }

    el('lo-profile-path').textContent = status.profilePath || '';

    // signedIn is null until something has actually probed the page — "we have a profile" and "the
    // session is live" are different claims and the badge must not conflate them.
    if (status.signedIn === true) {
      badge.className = 'badge green';
      badge.textContent = 'Signed in';
    } else if (status.signedIn === false) {
      badge.className = 'badge red';
      badge.textContent = 'Signed out — scan the QR code';
    } else if (status.hasProfile) {
      badge.className = 'badge amber';
      badge.textContent = 'Profile saved — not checked yet';
    } else {
      badge.className = 'badge amber';
      badge.textContent = 'No profile — sign in required';
    }

    setRunning(status.isRunning, status.runningModule);
  }

  /** Idempotent: the run state arrives from the POST, from /status and from the event stream. */
  function setRunning(isRunning, runningModule) {
    running = !!isRunning;

    const send = el('lo-send');
    RPA.setBusy(send, running, 'Running…');
    send.disabled = running || lastMessages.length === 0;
    if (running && runningModule && runningModule !== MODULE) {
      send.title = 'Another automation run (' + runningModule + ') is using the browser.';
    } else {
      send.removeAttribute('title');
    }

    // Wiping the session out from under a running batch would fail every remaining group.
    el('lo-clear-session').disabled = running;
    if (running) el('lo-run').hidden = false;
  }

  /** Runs a session button's request with its own busy state, reporting failures in the alert. */
  async function runSessionAction(buttonId, busyLabel, url, onSuccess) {
    const button = el(buttonId);
    RPA.clearError('lo-session-alert');
    RPA.setBusy(button, true, busyLabel);
    try {
      const result = await postNoBody(url);
      if (onSuccess) onSuccess(result);
    } catch (err) {
      RPA.showError('lo-session-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      await refreshStatus();
    }
  }

  // ---------------------------------------------------------------------------
  // Run log
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('lo-console');
    // Follow the tail only while the operator is already at the bottom — scrolling back through a
    // long run must not be yanked away by the next line.
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('lo-progress-fill').style.width = percent + '%';
    el('lo-progress').setAttribute('aria-valuenow', String(percent));
    el('lo-progress-text').textContent = completed + ' / ' + total;
  }

  // Only this module's events are rendered here. The bus is shared with Create Return and its log
  // lines carry no module of their own, so `mine` is latched on `started` and gates the rest.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('lo-run').hidden = false;
        el('lo-console').textContent = '';
        el('lo-progress').classList.remove('is-done');
        setProgress(0);
        setRunning(true, MODULE);
        break;

      case 'log':
        if (mine) appendLog(event.message);
        break;

      case 'progress':
        if (!mine) return;
        total = event.total;
        setProgress(event.completed);
        break;

      case 'done':
        if (!mine) return;
        appendLog('');
        appendLog('Finished. Processed: ' + event.processed + ' · Failed: ' + event.failed.length);
        if (event.failed.length) appendLog('Failed groups:\n  ' + event.failed.join('\n  '));
        el('lo-progress').classList.add('is-done');
        setRunning(false);
        refreshStatus();
        break;
    }
  }

  function connect() {
    if (stream) return;

    stream = new EventSource('/api/automation/events');
    stream.addEventListener('message', function (message) {
      let payload;
      try {
        payload = JSON.parse(message.data);
      } catch (e) {
        return;
      }
      handleEvent(payload);
    });

    // EventSource reconnects on its own and the server replays the current run's log onto the new
    // connection, so a dropped stream needs no recovery here.
    stream.addEventListener('error', function () { });
  }

  // ---------------------------------------------------------------------------
  // Seller -> WhatsApp group mapping
  // ---------------------------------------------------------------------------

  function mappingRowHtml(entry) {
    return '<tr>' +
      '<td><input type="text" class="map-id" value="' + RPA.escapeHtml(entry.sellerId || '') + '" aria-label="Seller ID" /></td>' +
      '<td><input type="text" class="map-name" value="' + RPA.escapeHtml(entry.sellerName || '') + '" aria-label="Seller name" /></td>' +
      '<td><input type="text" class="map-group" value="' + RPA.escapeHtml(entry.groupName || '') + '" aria-label="WhatsApp group" /></td>' +
      '<td class="num"><button type="button" class="btn btn-ghost btn-sm map-remove" aria-label="Remove row">Remove</button></td>' +
      '</tr>';
  }

  /** The search box and "no group yet" toggle over the table. Set up on DOMContentLoaded. */
  let mappingFilter = null;

  function renderMapping(entries) {
    const body = el('lo-mapping-body');
    body.innerHTML = (entries || []).map(mappingRowHtml).join('');

    // Re-applied rather than reset: a list arriving from the server must not silently drop the
    // filter the operator is working under. This calls updateMappingCount through onChange.
    if (mappingFilter) mappingFilter.apply();
    else updateMappingCount();
  }

  /**
   * Counts what would actually be saved, not what is on screen. A blank row is dropped by the server
   * (nothing to match a seller on), so counting <tr> elements produced "1 seller(s) mapped" beside a
   * "0 entries" save confirmation.
   */
  function updateMappingCount() {
    const entries = collectMapping();
    const withGroup = entries.filter(e => e.groupName).length;
    const body = el('lo-mapping-body');
    const shown = body.querySelectorAll('tr:not(.is-filtered-out)').length;
    const hidden = body.querySelectorAll('tr').length - shown;

    if (!entries.length) {
      el('lo-mapping-count').textContent = 'No sellers mapped yet';
      return;
    }

    // The hidden count is spelled out because everything else on this card — the save, the export —
    // still covers those rows. A filtered table that only said "12 sellers" would read as a mapping
    // that had lost the rest.
    el('lo-mapping-count').textContent =
      entries.length.toLocaleString('en-US') + ' seller(s) · ' +
      withGroup.toLocaleString('en-US') + ' with a group' +
      (hidden ? ' · ' + shown.toLocaleString('en-US') + ' shown' : '') +
      (hidden && !shown ? ' — no rows match' : '');
  }

  /** Reads the table back out. Blank rows are dropped; a seller with no group is kept. */
  function collectMapping() {
    return Array.from(el('lo-mapping-body').querySelectorAll('tr')).map(function (row) {
      return {
        sellerId: row.querySelector('.map-id').value.trim(),
        sellerName: row.querySelector('.map-name').value.trim(),
        groupName: row.querySelector('.map-group').value.trim()
      };
    }).filter(e => e.sellerId || e.sellerName);
  }

  function renderMappingWarnings(warnings) {
    const box = el('lo-mapping-warnings');
    if (!warnings || !warnings.length) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }
    box.hidden = false;
    box.innerHTML = warnings
      .map(w => '<span class="badge amber">' + RPA.escapeHtml(w) + '</span>')
      .join(' ');
  }

  function addMappingRow(sellerId, sellerName, groupName) {
    // A blank row matches no search term, so it would be added and hidden in the same breath. The
    // "no group yet" toggle is left alone — a new row satisfies it.
    if (mappingFilter) mappingFilter.clearSearch();

    el('lo-mapping-body').insertAdjacentHTML('beforeend', mappingRowHtml({
      sellerId: sellerId || '',
      sellerName: sellerName || '',
      groupName: groupName || ''
    }));

    if (mappingFilter) mappingFilter.apply();
    else updateMappingCount();

    const rows = el('lo-mapping-body').querySelectorAll('tr');
    const added = rows[rows.length - 1];
    added.scrollIntoView({ block: 'center', behavior: 'smooth' });
    added.querySelector('.map-group').focus();
  }

  async function loadMapping() {
    let data;
    try {
      const response = await fetch('/api/late-orders/mapping');
      if (!response.ok) throw new Error('Request failed with status ' + response.status + '.');
      data = await response.json();
    } catch (err) {
      RPA.showError('lo-mapping-alert', 'The mapping could not be loaded: ' + err.message);
      return;
    }

    defaultTemplate = data.defaultTemplate || '';
    defaultOrderLineTemplate = data.defaultOrderLineTemplate || '';

    renderMapping(data.entries);
    renderMappingWarnings(data.warnings);

    el('lo-template').value = data.template || '';
    el('lo-line-template').value = data.orderLineTemplate || '';
    el('lo-mapping-path').textContent = data.path || '';
    el('lo-mapping-updated').textContent = data.updatedUtc ? 'Last saved ' + data.updatedUtc : 'Never saved';

    renderPlaceholders(data.placeholders || []);
  }

  function renderPlaceholders(placeholders) {
    el('lo-placeholders').innerHTML = placeholders
      .map(p => '<code>' + RPA.escapeHtml(p) + '</code>')
      .join(' ');
  }

  async function saveMapping() {
    const button = el('lo-map-save');
    RPA.clearError('lo-mapping-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      const result = await sendJsonMethod('PUT', '/api/late-orders/mapping', {
        entries: collectMapping(),
        template: el('lo-template').value,
        orderLineTemplate: el('lo-line-template').value
      });
      renderMappingWarnings(result.warnings);
      el('lo-mapping-updated').textContent = 'Saved just now · ' + result.saved + ' entries';
    } catch (err) {
      RPA.showError('lo-mapping-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  async function importMapping(file) {
    const button = el('lo-map-import');
    RPA.clearError('lo-mapping-alert');
    RPA.setBusy(button, true, 'Reading…');
    try {
      const form = new FormData();
      form.append('file', file);
      const result = await RPA.postJson('/api/late-orders/mapping/import', form);

      renderMapping(result.entries);
      el('lo-mapping-updated').textContent =
        'Imported: ' + result.added + ' added, ' + result.updated + ' updated, ' +
        result.skipped + ' unchanged — not saved yet';
    } catch (err) {
      RPA.showError('lo-mapping-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      el('lo-map-file').value = '';
    }
  }

  // ---------------------------------------------------------------------------
  // Prepare
  // ---------------------------------------------------------------------------

  const FUNNEL_COLUMNS = [
    { label: 'Where the rows went', render: f => RPA.escapeHtml(f.label) },
    { label: 'Rows', render: f => RPA.fmtInt(f.count), numeric: true, value: f => f.count }
  ];

  const ORDER_COLUMNS = [
    { label: 'Seller', render: r => RPA.escapeHtml(r.sellerName) },
    { label: 'Order number', render: r => RPA.escapeHtml(r.orderNumber), numeric: true },
    { label: 'Status', render: r => '<span class="badge">' + RPA.escapeHtml(r.status) + '</span>' },
    { label: 'Deadline (as exported)', render: r => RPA.escapeHtml(r.deadlineRaw), numeric: true },
    { label: 'Deadline + offset', render: r => RPA.escapeHtml(r.deadlineEffective), numeric: true },
    { label: 'Late by', render: r => RPA.escapeHtml(formatLate(r)), numeric: true, value: r => r.hoursLate },
    {
      label: 'WhatsApp group',
      render: r => r.groupName
        ? RPA.escapeHtml(r.groupName)
        : '<span class="badge amber">' + RPA.escapeHtml(r.mappingProblem || 'unmapped') + '</span>'
    }
  ];

  const UNMAPPED_COLUMNS = [
    { label: 'Seller ID', render: s => RPA.escapeHtml(s.sellerId || '-'), numeric: true },
    { label: 'Seller', render: s => RPA.escapeHtml(s.sellerName) },
    { label: 'Overdue orders', render: s => RPA.fmtInt(s.orderCount), numeric: true, value: s => s.orderCount },
    { label: 'Worst delay', render: s => RPA.escapeHtml(s.maxDaysLate + ' day(s)'), numeric: true, value: s => s.maxDaysLate },
    { label: 'Why', render: s => '<span class="badge amber">' + RPA.escapeHtml(s.mappingProblem || '') + '</span>' },
    {
      label: '',
      render: s => '<button type="button" class="btn btn-ghost btn-sm lo-add-map"' +
        ' data-seller-id="' + RPA.escapeHtml(s.sellerId || '') + '"' +
        ' data-seller-name="' + RPA.escapeHtml(s.sellerName) + '">Add to mapping</button>'
    }
  ];

  const REVIEW_COLUMNS = [
    { label: 'Order number', render: r => RPA.escapeHtml(r.orderNumber), numeric: true },
    { label: 'Seller', render: r => RPA.escapeHtml(r.seller) },
    { label: 'Status', render: r => RPA.escapeHtml(r.status || '-') },
    { label: 'Deadline (as exported)', render: r => RPA.escapeHtml(r.deadlineRaw || '-'), numeric: true },
    { label: 'Why it was set aside', render: r => '<span class="badge amber">' + RPA.escapeHtml(r.reason) + '</span>' }
  ];

  function formatLate(order) {
    return order.daysLate >= 1
      ? order.daysLate + ' day(s)'
      : Math.round(order.hoursLate) + ' hour(s)';
  }

  /** The funnel is a set of terminal buckets, so it reads as label/count rather than as survivors. */
  function funnelRows(funnel) {
    return [
      { label: 'Rows in the file', count: funnel.rowsInFile },
      { label: 'Already shipped', count: funnel.alreadyShipped },
      { label: 'Status not chaseable', count: funnel.statusNotChaseable },
      { label: 'No shipping deadline', count: funnel.noDeadline },
      { label: 'Deadline could not be read', count: funnel.unreadableDeadline },
      { label: 'Not yet late', count: funnel.notYetLate },
      { label: 'Overdue rows', count: funnel.overdueRows },
      { label: 'Overdue orders (rows collapsed)', count: funnel.overdueOrders },
      { label: 'Sellers with overdue orders', count: funnel.sellers },
      { label: 'Sellers with a WhatsApp group', count: funnel.mappedSellers },
      { label: 'Sellers with no group', count: funnel.unmappedSellers }
    ];
  }

  /** Flattens sellers to one row per order for the review table. */
  function orderRows(sellers) {
    const rows = [];
    sellers.forEach(function (seller) {
      seller.orders.forEach(function (order) {
        rows.push(Object.assign({}, order, {
          sellerName: seller.sellerName,
          groupName: seller.groupName,
          mappingProblem: seller.mappingProblem
        }));
      });
    });
    return rows;
  }

  function renderWarnings(warnings) {
    const card = el('lo-warnings');
    if (!warnings || !warnings.length) {
      card.hidden = true;
      return;
    }
    card.hidden = false;
    el('lo-warnings-list').innerHTML = warnings
      .map(w => '<li>' + RPA.escapeHtml(w) + '</li>')
      .join('');
  }

  function renderPrepared(data) {
    lastData = data;

    RPA.setExportContext('Reference time ' + data.referenceTime + ' · deadline offset ' + data.offsetHours + 'h');

    renderWarnings(data.warnings);

    const unmapped = data.sellers.filter(s => !s.groupName);
    el('lo-unmapped').hidden = unmapped.length === 0;
    el('lo-unmapped-summary').textContent = unmapped.length
      ? unmapped.length.toLocaleString('en-US') + ' seller(s) with overdue orders have no WhatsApp group'
      : '';
    RPA.renderTable('lo-unmapped-wrap', unmapped, UNMAPPED_COLUMNS, 'Every seller with overdue orders is mapped.');

    RPA.renderTable('lo-funnel-wrap', funnelRows(data.funnel), FUNNEL_COLUMNS, 'Nothing to report.');
    RPA.renderTable('lo-orders-wrap', orderRows(data.sellers), ORDER_COLUMNS,
      'No order is overdue at this reference time. Check the deadline offset, or the file may only hold shipped orders.');
    RPA.renderTable('lo-review-wrap', data.review, REVIEW_COLUMNS,
      'Nothing was set aside — every unshipped row had a readable deadline and a chaseable status.');

    RPA.syncExportButtons();

    el('lo-reference').textContent = data.referenceTime;
    el('lo-prepared').hidden = false;
    RPA.stamp('lo-stamp');

    return renderMessages();
  }

  async function prepare() {
    const file = el('lo-file').files[0];
    if (!file) {
      RPA.showError('lo-prepare-alert', 'Pick the Mirakl orders export first.');
      return;
    }
    RPA.clearError('lo-prepare-alert');

    const form = new FormData();
    form.append('file', file);
    form.append('offsetHours', el('lo-offset').value || '0');

    const button = el('lo-prepare');
    RPA.setBusy(button, true, 'Working…');
    RPA.showSkeleton('lo-prepare-skeleton', 'lo-prepared');
    try {
      await renderPrepared(await RPA.postJson('/api/late-orders/prepare', form));
    } catch (err) {
      RPA.showError('lo-prepare-alert', err.message);
    } finally {
      RPA.hideSkeleton('lo-prepare-skeleton');
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Messages
  // ---------------------------------------------------------------------------

  function messageCardHtml(message, index) {
    return '<div class="msg-card">' +
      '<div class="msg-head">' +
        '<span class="badge green">' + RPA.escapeHtml(message.groupName) + '</span>' +
        '<span class="msg-meta">' + RPA.escapeHtml(message.sellerName) +
          ' · ' + message.orderCount + ' order(s)' +
          // Two accounts of one company sharing a group is normal here, but it is worth seeing: the
          // body below lists both accounts' order numbers.
          (message.accountCount > 1 ? ' · <span class="badge amber">' + message.accountCount + ' accounts merged</span>' : '') +
          (message.truncated ? ' · <span class="badge amber">truncated</span>' : '') +
        '</span>' +
        '<button type="button" class="btn btn-ghost btn-sm lo-copy" data-index="' + index + '">Copy</button>' +
      '</div>' +
      '<pre class="msg-body">' + RPA.escapeHtml(message.body) + '</pre>' +
    '</div>';
  }

  async function renderMessages() {
    if (!lastData) return;

    RPA.clearError('lo-messages-alert');
    try {
      const result = await RPA.sendJson('/api/late-orders/messages', {
        sellers: lastData.sellers,
        referenceTime: lastData.referenceTime,
        template: el('lo-template').value,
        orderLineTemplate: el('lo-line-template').value
      });

      lastMessages = result.messages || [];
      el('lo-messages').innerHTML = lastMessages.length
        ? lastMessages.map(messageCardHtml).join('')
        : '<div class="empty-state">No message to compose — every overdue seller is unmapped, or nothing is overdue.</div>';

      el('lo-messages-summary').textContent = lastMessages.length
        ? lastMessages.length.toLocaleString('en-US') + ' message(s) ready'
        : '';
      el('lo-messages-export').disabled = lastMessages.length === 0;
      el('lo-send').disabled = running || lastMessages.length === 0;

      if (result.warnings && result.warnings.length) {
        RPA.showError('lo-messages-alert', result.warnings.join(' '));
      }
    } catch (err) {
      RPA.showError('lo-messages-alert', err.message);
    }
  }

  /** navigator.clipboard needs a secure context; localhost is one, but keep a fallback anyway. */
  async function copyText(text) {
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

  // ---------------------------------------------------------------------------
  // Send
  // ---------------------------------------------------------------------------

  /**
   * The last checkpoint before something irreversible. It names the destinations rather than just
   * counting them, because reading a group name is the only way to notice a wrong mapping — the full
   * list is on the cards above, so a long run shows the first twelve here and says so.
   */
  function confirmSend(dryRun) {
    const names = lastMessages.map(m => m.groupName);
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
        Math.max(1, Math.round(names.length * 9 / 60)) + ' minute(s); Create Return cannot run during it.';

    return window.confirm(heading + '\n\n  ' + shown.join('\n  ') + tail + slotWarning);
  }

  async function send() {
    if (!lastMessages.length) return;

    const dryRun = el('lo-dry-run').checked;
    if (!confirmSend(dryRun)) return;

    RPA.clearError('lo-messages-alert');

    // Opened before the POST so the first events of the run cannot be missed.
    connect();
    setRunning(true, MODULE);
    el('lo-run').hidden = false;

    try {
      await RPA.sendJson('/api/late-orders/send', {
        dryRun: dryRun,
        messages: lastMessages.map(m => ({
          groupName: m.groupName,
          sellerId: m.sellerId,
          sellerName: m.sellerName,
          body: m.body
        }))
      });
    } catch (err) {
      RPA.showError('lo-messages-alert', err.message);
      setRunning(false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  /** POST with no body. The shared helpers all expect a payload in at least one direction. */
  async function postNoBody(url) {
    const response = await fetch(url, { method: 'POST' });
    if (response.ok) {
      const text = await response.text();
      if (!text) return null;
      try { return JSON.parse(text); } catch (e) { return null; }
    }

    const text = await response.text();
    let message = text;
    try {
      const parsed = JSON.parse(text);
      if (parsed && parsed.error) message = parsed.error;
      else if (parsed && parsed.title) message = parsed.title;
    } catch (e) { /* not JSON — the raw body is the best message available */ }
    throw new Error(message || ('Request failed with status ' + response.status + '.'));
  }

  /** RPA.sendJson is POST-only; the mapping save is a PUT. */
  async function sendJsonMethod(method, url, payload) {
    const response = await fetch(url, {
      method: method,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (response.ok) return response.json();

    const text = await response.text();
    let message = text;
    try {
      const parsed = JSON.parse(text);
      if (parsed && parsed.error) message = parsed.error;
      else if (parsed && parsed.title) message = parsed.title;
    } catch (e) { /* not JSON — the raw body is the best message available */ }
    throw new Error(message || ('Request failed with status ' + response.status + '.'));
  }

  function activate() {
    if (activated) return;
    activated = true;
    loadMapping();
    connect();
    refreshStatus();
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('lo-drop', 'lo-file');

    el('lo-prepare').addEventListener('click', prepare);
    el('lo-send').addEventListener('click', send);

    el('lo-login').addEventListener('click', function () {
      // Chrome has to be launched before the window can appear, so this is not instant.
      runSessionAction('lo-login', 'Opening…', '/api/late-orders/login');
    });

    el('lo-check-session').addEventListener('click', function () {
      runSessionAction('lo-check-session', 'Checking…', '/api/late-orders/check-session');
    });

    el('lo-clear-session').addEventListener('click', function () {
      if (!window.confirm('Delete the saved WhatsApp profile? You will have to scan the QR code again.')) return;
      runSessionAction('lo-clear-session', 'Clearing…', '/api/late-orders/clear-session', function (result) {
        if (result && result.message) RPA.showError('lo-session-alert', result.message);
      });
    });
    el('lo-map-save').addEventListener('click', saveMapping);
    el('lo-map-add').addEventListener('click', () => addMappingRow('', '', ''));

    mappingFilter = RPA.initRowFilter('lo-mapping-body', {
      searchId: 'lo-map-search',
      pendingId: 'lo-map-pending',
      pendingSelector: '.map-group',
      onChange: updateMappingCount
    });

    el('lo-map-import').addEventListener('click', () => el('lo-map-file').click());
    el('lo-map-file').addEventListener('change', function () {
      if (this.files && this.files[0]) importMapping(this.files[0]);
    });

    el('lo-map-export').addEventListener('click', async function () {
      const button = el('lo-map-export');
      RPA.clearError('lo-mapping-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/late-orders/mapping/excel',
          { entries: collectMapping() }, 'seller-groups.xlsx');
      } catch (err) {
        RPA.showError('lo-mapping-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    // Removing a row must not silently drop unsaved edits elsewhere, so the table is never re-rendered
    // on remove — the row is taken out in place.
    el('lo-mapping-body').addEventListener('click', function (event) {
      const button = event.target.closest('.map-remove');
      if (!button) return;
      button.closest('tr').remove();
      updateMappingCount();
    });

    // Keeps the count honest while the operator is still filling rows in, rather than only after a
    // row is added or removed.
    el('lo-mapping-body').addEventListener('input', updateMappingCount);

    el('lo-template-reset').addEventListener('click', function () {
      el('lo-template').value = defaultTemplate;
      el('lo-line-template').value = defaultOrderLineTemplate;
      renderMessages();
    });

    el('lo-render').addEventListener('click', renderMessages);

    el('lo-messages-export').addEventListener('click', async function () {
      const button = el('lo-messages-export');
      RPA.clearError('lo-messages-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/late-orders/messages/excel',
          { messages: lastMessages }, 'late-order-messages.xlsx');
      } catch (err) {
        RPA.showError('lo-messages-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    // Delegated: both tables are re-rendered on every prepare, so per-row listeners would be lost.
    el('panel-late-orders').addEventListener('click', async function (event) {
      const add = event.target.closest('.lo-add-map');
      if (add) {
        addMappingRow(add.dataset.sellerId, add.dataset.sellerName, '');
        return;
      }

      const copy = event.target.closest('.lo-copy');
      if (copy) {
        const message = lastMessages[Number(copy.dataset.index)];
        if (!message) return;
        const ok = await copyText(message.body);
        copy.textContent = ok ? 'Copied' : 'Copy failed';
        setTimeout(() => { copy.textContent = 'Copy'; }, 1600);
      }
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by
    // the time the listener below exists. Check the tab directly instead of waiting for a repeat.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-late-orders').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
