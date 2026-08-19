/* =============================================================================
   Create Return — Mirakl browser automation.

   Ported from the RPA project's Create Return module. The run itself happens on
   the server in a real browser window; this file starts it and renders what it
   reports back. Progress arrives over Server-Sent Events rather than SignalR —
   AutomationJobBus explains why.

   The stream is opened the first time the panel is shown, not on page load, so
   someone who only ever uses the report tabs never holds a connection open.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'create-return';

  let stream = null;       // EventSource, once the panel has been visited
  let activated = false;
  let total = 0;

  // The prepared list, held here so "Download Excel" and "Start with this list" need neither a
  // re-upload of the four exports nor a second pass over them.
  let preparedRows = [];

  function el(id) { return document.getElementById(id); }

  // ---------------------------------------------------------------------------
  // Progress + console
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('cr-console');
    // Follow the tail only while the operator is already at the bottom — scrolling back through a
    // long run must not be yanked away by the next line.
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('cr-progress-fill').style.width = percent + '%';
    el('cr-progress').setAttribute('aria-valuenow', String(percent));
    el('cr-progress-text').textContent = completed + ' / ' + total;
  }

  /** Idempotent: the run state arrives from several places (POST, status, events). */
  function setRunning(running) {
    const button = el('cr-run-list');
    if (running !== button.classList.contains('is-busy')) {
      RPA.setBusy(button, running, 'Running…');
    }
    // setBusy re-enables the button when a run ends, so the empty-list case is applied after it.
    button.disabled = running || preparedRows.length === 0;
    // Wiping the session out from under a running batch would fail every remaining order.
    el('cr-clear-session').disabled = running;
  }

  // ---------------------------------------------------------------------------
  // Event stream
  // ---------------------------------------------------------------------------

  // AutomationJobBus is shared with the Late Order Warnings module and its log/progress/done events
  // carry no module of their own, so without this latch a WhatsApp run's output lands in this console.
  // Deliberately solved here rather than by adding a module field to AutomationJobBus.Log, which would
  // change a signature CreateReturnRunner calls fifteen times for a cosmetic problem.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        // Also the point a reconnected or reloaded page resets to before the replayed log arrives.
        total = event.total;
        el('cr-run').hidden = false;
        el('cr-console').textContent = '';
        el('cr-progress').classList.remove('is-done');
        setProgress(0);
        setRunning(true);
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
        if (event.failed.length) appendLog('Failed orders:\n  ' + event.failed.join('\n  '));
        el('cr-progress').classList.add('is-done');
        setRunning(false);
        el('cr-stamp').textContent = 'Last run ' + new Date().toLocaleString('en-US', {
          year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
        });
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

    // EventSource reconnects on its own, and the server replays the current run's log onto the new
    // connection, so a dropped stream needs no recovery here.
    stream.addEventListener('error', function () { });
  }

  // ---------------------------------------------------------------------------
  // Requests
  // ---------------------------------------------------------------------------

  /** POST with no body. The shared helpers all expect a payload in at least one direction. */
  async function send(url) {
    const response = await fetch(url, { method: 'POST' });
    if (response.ok) return;

    const text = await response.text();
    let message = text;
    try {
      const parsed = JSON.parse(text);
      if (parsed && parsed.error) message = parsed.error;
      else if (parsed && parsed.title) message = parsed.title;
    } catch (e) { /* not JSON — the raw body is the best message available */ }

    throw new Error(message || ('Request failed with status ' + response.status + '.'));
  }

  async function refreshStatus() {
    const badge = el('cr-session-badge');

    let status;
    try {
      const response = await fetch('/api/automation/status');
      if (!response.ok) throw new Error();
      status = await response.json();
    } catch (e) {
      badge.className = 'badge red';
      badge.textContent = 'Status unavailable';
      return;
    }

    badge.className = 'badge ' + (status.hasSession ? 'green' : 'amber');
    badge.textContent = status.hasSession
      ? (status.browserReady ? 'Session saved · browser ready' : 'Session saved')
      : 'No session · sign in required';

    setRunning(status.isRunning);
    if (status.isRunning) el('cr-run').hidden = false;
  }

  /** Runs a session button's request with its own busy state, reporting failures in the alert. */
  async function runSessionAction(buttonId, busyLabel, url, onSuccess) {
    const button = el(buttonId);
    RPA.clearError('cr-alert');
    RPA.setBusy(button, true, busyLabel);
    try {
      await send(url);
      if (onSuccess) onSuccess();
    } catch (err) {
      RPA.showError('cr-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      await refreshStatus();
    }
  }

  // ---------------------------------------------------------------------------
  // Prepare the list
  // ---------------------------------------------------------------------------

  const READY_COLUMNS = [
    { label: 'Source', render: r => RPA.escapeHtml(r.source) },
    { label: 'Order number', render: r => RPA.escapeHtml(r.orderNumber), numeric: true },
    { label: 'Tracking number', render: r => RPA.escapeHtml(r.trackingNumber), numeric: true },
    { label: 'Seller', render: r => RPA.escapeHtml(r.seller || '-') },
    { label: 'Type / state', render: r => '<span class="badge">' + RPA.escapeHtml(r.typeOrState || '-') + '</span>' },
    { label: 'Request date', render: r => RPA.escapeHtml(r.requestDate || '-'), numeric: true }
  ];

  const EXCLUDED_COLUMNS = [
    { label: 'Source', render: r => RPA.escapeHtml(r.source) },
    { label: 'Order number', render: r => RPA.escapeHtml(r.sourceOrderNo), numeric: true },
    { label: 'Tracking number', render: r => RPA.escapeHtml(r.trackingNumber || '-'), numeric: true },
    { label: 'Seller', render: r => RPA.escapeHtml(r.seller || '-') },
    { label: 'Type / state', render: r => RPA.escapeHtml(r.typeOrState || '-') },
    { label: 'Request date', render: r => RPA.escapeHtml(r.requestDate || '-'), numeric: true },
    { label: 'Why it was dropped', render: r => '<span class="badge amber">' + RPA.escapeHtml(r.reason) + '</span>' }
  ];

  const FUNNEL_COLUMNS = [
    { label: 'Source', render: f => RPA.escapeHtml(f.source) },
    { label: 'In file', render: f => RPA.fmtInt(f.total), numeric: true, value: f => f.total },
    { label: 'Tracking code', render: f => RPA.fmtInt(f.withTracking), numeric: true, value: f => f.withTracking },
    { label: 'Date range', render: f => RPA.fmtInt(f.inDateRange), numeric: true, value: f => f.inDateRange },
    { label: 'Not duplicated', render: f => RPA.fmtInt(f.afterDuplicates), numeric: true, value: f => f.afterDuplicates },
    { label: 'Request type', render: f => RPA.fmtInt(f.afterRequestType), numeric: true, value: f => f.afterRequestType },
    { label: 'Not cancelled', render: f => RPA.fmtInt(f.afterState), numeric: true, value: f => f.afterState },
    { label: 'No return yet', render: f => RPA.fmtInt(f.afterExistingReturns), numeric: true, value: f => f.afterExistingReturns },
    { label: 'Ready', render: f => '<strong>' + RPA.fmtInt(f.ready) + '</strong>', numeric: true, value: f => f.ready }
  ];

  function renderPrepared(data) {
    preparedRows = data.rows || [];

    RPA.renderTable('cr-funnel-wrap', data.funnels || [], FUNNEL_COLUMNS, 'Nothing to report.');
    RPA.renderTable('cr-ready-wrap', preparedRows, READY_COLUMNS,
      'No order came through all the filters. Widen the date range, or check that the right files were picked.');
    RPA.renderTable('cr-excluded-wrap', data.excluded || [], EXCLUDED_COLUMNS,
      'Nothing was dropped after the tracking-code and date filters.');
    RPA.syncExportButtons();

    el('cr-ready-summary').textContent = preparedRows.length
      ? preparedRows.length.toLocaleString('en-US') + ' return(s) ready'
      : '';
    el('cr-download').disabled = preparedRows.length === 0;
    el('cr-run-list').disabled = preparedRows.length === 0;

    el('cr-prepared').hidden = false;
  }

  /** Only the two fields the run needs travel back to the server. */
  function listPayload() {
    return {
      rows: preparedRows.map(r => ({ orderNumber: r.orderNumber, trackingNumber: r.trackingNumber }))
    };
  }

  async function prepare() {
    const files = {
      templateA: el('cr-templateA-file').files[0],
      templateB: el('cr-templateB-file').files[0],
      returns: el('cr-returns-file').files[0],
      orders: el('cr-orders-file').files[0]
    };

    const missing = Object.keys(files).filter(key => !files[key]);
    if (missing.length) {
      RPA.showError('cr-prepare-alert',
        'Pick all four files: return template A, return template B (MP), the returns export and the orders export.');
      return;
    }
    RPA.clearError('cr-prepare-alert');

    const form = new FormData();
    Object.keys(files).forEach(key => form.append(key, files[key]));
    form.append('from', el('cr-from').value);
    form.append('to', el('cr-to').value);
    form.append('returnsOnly', el('cr-returns-only').checked ? 'true' : 'false');

    const button = el('cr-prepare');
    RPA.setBusy(button, true, 'Working…');
    RPA.showSkeleton('cr-prepare-skeleton', 'cr-prepared');
    try {
      renderPrepared(await RPA.postJson('/api/create-return/prepare', form));
    } catch (err) {
      RPA.showError('cr-prepare-alert', err.message);
    } finally {
      RPA.hideSkeleton('cr-prepare-skeleton');
      RPA.setBusy(button, false);
    }
  }

  /** Defaults the range to the last month, which is what the manual process always used. */
  function seedDateRange() {
    const to = new Date();
    const from = new Date();
    from.setMonth(from.getMonth() - 1);

    const iso = d => d.getFullYear() + '-' +
      String(d.getMonth() + 1).padStart(2, '0') + '-' +
      String(d.getDate()).padStart(2, '0');

    el('cr-from').value = iso(from);
    el('cr-to').value = iso(to);
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  function activate() {
    if (activated) return;
    activated = true;
    connect();
    refreshStatus();
  }

  document.addEventListener('DOMContentLoaded', function () {
    ['cr-templateA', 'cr-templateB', 'cr-returns', 'cr-orders'].forEach(function (id) {
      RPA.initDropzone(id + '-drop', id + '-file');
    });
    seedDateRange();

    el('cr-prepare').addEventListener('click', prepare);

    el('cr-download').addEventListener('click', async function () {
      const button = el('cr-download');
      RPA.clearError('cr-prepare-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/create-return/list/excel', listPayload(), 'create-return.xlsx');
      } catch (err) {
        RPA.showError('cr-prepare-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    el('cr-run-list').addEventListener('click', async function () {
      if (!preparedRows.length) return;
      if (!window.confirm('File ' + preparedRows.length + ' return(s) on Mirakl? This writes to the marketplace and cannot be undone from here.'))
        return;

      RPA.clearError('cr-prepare-alert');

      // Opened before the POST so the first events of the run cannot be missed.
      connect();
      setRunning(true);
      el('cr-run').hidden = false;

      try {
        await RPA.sendJson('/api/create-return/start-list', listPayload());
      } catch (err) {
        RPA.showError('cr-prepare-alert', err.message);
        setRunning(false);
      }
    });

    el('cr-login').addEventListener('click', function () {
      // Chrome has to be launched before the window can appear, so this is not instant.
      runSessionAction('cr-login', 'Opening…', '/api/automation/login', function () {
        el('cr-save-session').disabled = false;
      });
    });

    el('cr-save-session').addEventListener('click', function () {
      runSessionAction('cr-save-session', 'Saving…', '/api/automation/save-session', function () {
        el('cr-save-session').disabled = true;
      });
    });

    el('cr-clear-session').addEventListener('click', function () {
      runSessionAction('cr-clear-session', 'Clearing…', '/api/automation/clear-session');
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by
    // the time the listener below exists. Check the tab directly instead of waiting for a repeat.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-create-return').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
