/* =============================================================================
   Mark as Received — Mirakl browser automation.

   Ported from the RPA project's Mark as Received module. Simpler than Create
   Return: the input is nothing more than a list of order IDs, so there is no
   prepare/review step — paste or upload, confirm, run. Session and the event
   stream work exactly like create-return.js, because both drive the same
   Mirakl browser and share its login.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'mark-received';

  let stream = null;       // EventSource, once the panel has been visited
  let activated = false;
  let total = 0;

  function el(id) { return document.getElementById(id); }

  // ---------------------------------------------------------------------------
  // Order-id parsing (client-side mirror of the server's parse — used only for
  // the live count readout and the confirm-dialog wording, never trusted as
  // the source of truth for what actually runs)
  // ---------------------------------------------------------------------------

  function parseOrderIds(text) {
    const seen = new Set();
    const ids = [];
    (text || '').split('\n').forEach(function (line) {
      const trimmed = line.trim();
      if (!trimmed || trimmed[0] === '#') return;
      if (seen.has(trimmed)) return;
      seen.add(trimmed);
      ids.push(trimmed);
    });
    return ids;
  }

  // ---------------------------------------------------------------------------
  // Progress + console
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('mr-console');
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('mr-progress-fill').style.width = percent + '%';
    el('mr-progress').setAttribute('aria-valuenow', String(percent));
    el('mr-progress-text').textContent = completed + ' / ' + total;
  }

  /** Idempotent: the run state arrives from several places (POST, status, events). */
  function setRunning(running) {
    const button = el('mr-start');
    if (running !== button.classList.contains('is-busy')) {
      RPA.setBusy(button, running, 'Running…');
    }
    // Wiping the session out from under a running batch would fail every remaining order.
    el('mr-clear-session').disabled = running;
  }

  // ---------------------------------------------------------------------------
  // Event stream
  // ---------------------------------------------------------------------------

  // AutomationJobBus is shared with Create Return and Late Order Warnings, and its log/progress/done
  // events carry no module of their own, so without this latch another module's run would print here.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('mr-run').hidden = false;
        el('mr-console').textContent = '';
        el('mr-progress').classList.remove('is-done');
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
        el('mr-progress').classList.add('is-done');
        setRunning(false);
        el('mr-stamp').textContent = 'Last run ' + new Date().toLocaleString('en-US', {
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
    const badge = el('mr-session-badge');

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
    if (status.isRunning) el('mr-run').hidden = false;
  }

  /** Runs a session button's request with its own busy state, reporting failures in the alert. */
  async function runSessionAction(buttonId, busyLabel, url, onSuccess) {
    const button = el(buttonId);
    RPA.clearError('mr-alert');
    RPA.setBusy(button, true, busyLabel);
    try {
      await send(url);
      if (onSuccess) onSuccess();
    } catch (err) {
      RPA.showError('mr-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      await refreshStatus();
    }
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
    RPA.initDropzone('mr-drop', 'mr-file');

    let fileText = ''; // cached so the count readout does not re-read the file on every keystroke

    function updateCount() {
      const fileChosen = el('mr-file').files.length > 0;
      const text = fileChosen ? fileText : el('mr-orders-text').value;
      const count = parseOrderIds(text).length;
      el('mr-order-count').textContent = count
        ? count + ' order ID(s)' + (fileChosen ? ' from file' : '')
        : '';
      el('mr-start').disabled = count === 0;
    }

    el('mr-file').addEventListener('change', async function () {
      const file = el('mr-file').files[0];
      fileText = file ? await file.text() : '';
      // Mutually exclusive with the textarea, matching the source feature.
      if (file) el('mr-orders-text').value = '';
      updateCount();
    });

    el('mr-orders-text').addEventListener('input', function () {
      if (el('mr-orders-text').value.trim()) {
        el('mr-file').value = '';
        fileText = '';
      }
      updateCount();
    });

    el('mr-login').addEventListener('click', function () {
      // Chrome has to be launched before the window can appear, so this is not instant.
      runSessionAction('mr-login', 'Opening…', '/api/automation/login', function () {
        el('mr-save-session').disabled = false;
      });
    });

    el('mr-save-session').addEventListener('click', function () {
      runSessionAction('mr-save-session', 'Saving…', '/api/automation/save-session', function () {
        el('mr-save-session').disabled = true;
      });
    });

    el('mr-clear-session').addEventListener('click', function () {
      runSessionAction('mr-clear-session', 'Clearing…', '/api/automation/clear-session');
    });

    el('mr-start').addEventListener('click', async function () {
      const file = el('mr-file').files[0];
      const ordersText = el('mr-orders-text').value.trim();

      if (!file && !ordersText) {
        RPA.showError('mr-alert', 'Upload a .txt file or paste order IDs.');
        return;
      }

      const count = parseOrderIds(file ? fileText : ordersText).length;
      if (!count) {
        RPA.showError('mr-alert', 'No usable order IDs were found in the input.');
        return;
      }

      if (!window.confirm(
        'Mark ' + count + ' order(s) as received on Mirakl? This writes to the marketplace and cannot be undone from here.'))
        return;

      RPA.clearError('mr-alert');

      const form = new FormData();
      if (file) form.append('file', file);
      if (ordersText) form.append('orders', ordersText);

      // Opened before the POST so the first events of the run cannot be missed.
      connect();
      setRunning(true);
      el('mr-run').hidden = false;

      try {
        await RPA.postJson('/api/mark-received/start', form);
      } catch (err) {
        RPA.showError('mr-alert', err.message);
        setRunning(false);
      }
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by
    // the time the listener below exists. Check the tab directly instead of waiting for a repeat.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-mark-received').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
