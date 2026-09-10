/* =============================================================================
   Seller Notification — Mirakl browser automation.

   Opens every order's own Mirakl conversation dialog and sends a topic/message
   through it. Structurally identical to mark-received.js (no prepare/review
   step: paste or upload order IDs, confirm, run) plus one thing that module
   does not have — a saved list of named topic/message templates the operator
   can load into the send form, edit, and run without ever saving. Session and
   the event stream work exactly like mark-received.js, because both drive the
   same Mirakl browser and share its login.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'seller-notification';

  let stream = null;       // EventSource, once the panel has been visited
  let activated = false;
  let total = 0;
  let templates = [];      // last list loaded from / saved to the server

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
    const box = el('sn-console');
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('sn-progress-fill').style.width = percent + '%';
    el('sn-progress').setAttribute('aria-valuenow', String(percent));
    el('sn-progress-text').textContent = completed + ' / ' + total;
  }

  /** Idempotent: the run state arrives from several places (POST, status, events). */
  function setRunning(running) {
    const button = el('sn-start');
    if (running !== button.classList.contains('is-busy')) {
      RPA.setBusy(button, running, 'Running…');
    }
    // Wiping the session out from under a running batch would fail every remaining order.
    el('sn-clear-session').disabled = running;
  }

  // ---------------------------------------------------------------------------
  // Event stream
  // ---------------------------------------------------------------------------

  // AutomationJobBus is shared with every automation module, and its log/progress/done events
  // carry no module of their own, so without this latch another module's run would print here.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('sn-run').hidden = false;
        el('sn-console').textContent = '';
        el('sn-progress').classList.remove('is-done');
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
        el('sn-progress').classList.add('is-done');
        setRunning(false);
        el('sn-stamp').textContent = 'Last run ' + new Date().toLocaleString('en-US', {
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

  /** RPA.sendJson is POST-only; the template save is a PUT. */
  async function sendJsonMethod(method, url, payload) {
    const response = await fetch(url, {
      method: method,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    const text = await response.text();
    let parsed = null;
    try { parsed = text ? JSON.parse(text) : null; } catch (e) { /* not JSON */ }

    if (response.ok) return parsed;

    const message = (parsed && (parsed.message || parsed.error || parsed.title)) || text;
    throw new Error(message || ('Request failed with status ' + response.status + '.'));
  }

  async function refreshStatus() {
    const badge = el('sn-session-badge');

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
    if (status.isRunning) el('sn-run').hidden = false;
  }

  /** Runs a session button's request with its own busy state, reporting failures in the alert. */
  async function runSessionAction(buttonId, busyLabel, url, onSuccess) {
    const button = el(buttonId);
    RPA.clearError('sn-alert');
    RPA.setBusy(button, true, busyLabel);
    try {
      await send(url);
      if (onSuccess) onSuccess();
    } catch (err) {
      RPA.showError('sn-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      await refreshStatus();
    }
  }

  // ---------------------------------------------------------------------------
  // Message templates — a saved list the send form can load from, not the
  // source of truth for a run: the operator can still type a one-off
  // topic/message below without ever saving it.
  // ---------------------------------------------------------------------------

  function templateRowHtml(t) {
    return '<tr data-id="' + RPA.escapeHtml(t.id || '') + '">' +
      '<td><input type="text" class="tpl-name" value="' + RPA.escapeHtml(t.name || '') + '" aria-label="Template name" /></td>' +
      '<td><input type="text" class="tpl-topic" value="' + RPA.escapeHtml(t.topic || '') + '" aria-label="Topic" /></td>' +
      '<td><textarea class="tpl-message" rows="2" aria-label="Message">' + RPA.escapeHtml(t.message || '') + '</textarea></td>' +
      '<td class="num"><button type="button" class="btn btn-ghost btn-sm tpl-remove" aria-label="Remove template">Remove</button></td>' +
      '</tr>';
  }

  function renderTemplates(list) {
    templates = list || [];
    el('sn-template-body').innerHTML = templates.map(templateRowHtml).join('');
    updateTemplateCount();
    renderTemplateOptions();
  }

  function updateTemplateCount() {
    const count = el('sn-template-body').querySelectorAll('tr').length;
    el('sn-template-count').textContent = count ? count + ' template(s)' : 'No templates saved yet';
  }

  /** Reads the table back out. A row with neither a name nor a topic/message is dropped. */
  function collectTemplates() {
    return Array.from(el('sn-template-body').querySelectorAll('tr')).map(function (row) {
      return {
        id: row.dataset.id || (window.crypto && crypto.randomUUID ? crypto.randomUUID() : String(Date.now() + Math.random())),
        name: row.querySelector('.tpl-name').value.trim(),
        topic: row.querySelector('.tpl-topic').value.trim(),
        message: row.querySelector('.tpl-message').value.trim()
      };
    }).filter(t => t.name || t.topic || t.message);
  }

  function addTemplateRow() {
    el('sn-template-body').insertAdjacentHTML('beforeend', templateRowHtml({ id: '', name: '', topic: '', message: '' }));
    updateTemplateCount();

    const rows = el('sn-template-body').querySelectorAll('tr');
    const added = rows[rows.length - 1];
    added.scrollIntoView({ block: 'center', behavior: 'smooth' });
    added.querySelector('.tpl-name').focus();
  }

  function renderTemplateOptions() {
    const select = el('sn-template-select');
    const current = select.value;
    select.innerHTML = '<option value="">Custom (not saved)</option>' +
      templates
        .filter(t => t.name)
        .map(t => '<option value="' + RPA.escapeHtml(t.id) + '">' + RPA.escapeHtml(t.name) + '</option>')
        .join('');
    // Keeps the current selection if that template still exists after a save; otherwise falls back
    // to "Custom" rather than silently landing on whatever now occupies that position in the list.
    select.value = templates.some(t => t.id === current) ? current : '';
  }

  async function loadTemplates() {
    let result;
    try {
      const response = await fetch('/api/seller-notification/templates');
      if (!response.ok) throw new Error('Request failed with status ' + response.status + '.');
      result = await response.json();
    } catch (err) {
      RPA.showError('sn-template-alert', 'The templates could not be loaded: ' + err.message);
      return;
    }

    renderTemplates(result.data || []);
  }

  async function saveTemplates() {
    const button = el('sn-template-save');
    RPA.clearError('sn-template-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      const result = await sendJsonMethod('PUT', '/api/seller-notification/templates', {
        templates: collectTemplates()
      });
      renderTemplates((result && result.data) || []);
    } catch (err) {
      RPA.showError('sn-template-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  function activate() {
    if (activated) return;
    activated = true;
    loadTemplates();
    connect();
    refreshStatus();
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('sn-drop', 'sn-file');

    let fileText = ''; // cached so the count readout does not re-read the file on every keystroke

    function updateCount() {
      const fileChosen = el('sn-file').files.length > 0;
      const text = fileChosen ? fileText : el('sn-orders-text').value;
      const count = parseOrderIds(text).length;
      el('sn-order-count').textContent = count
        ? count + ' order ID(s)' + (fileChosen ? ' from file' : '')
        : '';
      el('sn-start').disabled = count === 0;
    }

    el('sn-file').addEventListener('change', async function () {
      const file = el('sn-file').files[0];
      fileText = file ? await file.text() : '';
      // Mutually exclusive with the textarea, matching Mark as Received.
      if (file) el('sn-orders-text').value = '';
      updateCount();
    });

    el('sn-orders-text').addEventListener('input', function () {
      if (el('sn-orders-text').value.trim()) {
        el('sn-file').value = '';
        fileText = '';
      }
      updateCount();
    });

    el('sn-login').addEventListener('click', function () {
      // Chrome has to be launched before the window can appear, so this is not instant.
      runSessionAction('sn-login', 'Opening…', '/api/automation/login', function () {
        el('sn-save-session').disabled = false;
      });
    });

    el('sn-save-session').addEventListener('click', function () {
      runSessionAction('sn-save-session', 'Saving…', '/api/automation/save-session', function () {
        el('sn-save-session').disabled = true;
      });
    });

    el('sn-clear-session').addEventListener('click', function () {
      runSessionAction('sn-clear-session', 'Clearing…', '/api/automation/clear-session');
    });

    el('sn-template-add').addEventListener('click', addTemplateRow);
    el('sn-template-save').addEventListener('click', saveTemplates);
    el('sn-template-body').addEventListener('click', function (event) {
      const button = event.target.closest('.tpl-remove');
      if (!button) return;
      button.closest('tr').remove();
      updateTemplateCount();
    });

    el('sn-template-select').addEventListener('change', function () {
      const chosen = templates.find(t => t.id === this.value);
      if (!chosen) return;
      el('sn-topic').value = chosen.topic || '';
      el('sn-message').value = chosen.message || '';
    });

    el('sn-start').addEventListener('click', async function () {
      const file = el('sn-file').files[0];
      const ordersText = el('sn-orders-text').value.trim();
      const topic = el('sn-topic').value.trim();
      const message = el('sn-message').value.trim();

      if (!file && !ordersText) {
        RPA.showError('sn-alert', 'Upload a .txt file or paste order IDs.');
        return;
      }
      if (!topic) {
        RPA.showError('sn-alert', 'Topic cannot be empty.');
        return;
      }
      if (!message) {
        RPA.showError('sn-alert', 'Message cannot be empty.');
        return;
      }

      const count = parseOrderIds(file ? fileText : ordersText).length;
      if (!count) {
        RPA.showError('sn-alert', 'No usable order IDs were found in the input.');
        return;
      }

      if (!window.confirm(
        'Send this message to ' + count + ' order(s) on Mirakl? This writes to the marketplace and cannot be undone from here.'))
        return;

      RPA.clearError('sn-alert');

      const form = new FormData();
      if (file) form.append('file', file);
      if (ordersText) form.append('orders', ordersText);
      form.append('topic', topic);
      form.append('message', message);

      // Opened before the POST so the first events of the run cannot be missed.
      connect();
      setRunning(true);
      el('sn-run').hidden = false;

      try {
        await RPA.postJson('/api/seller-notification/start', form);
      } catch (err) {
        RPA.showError('sn-alert', err.message);
        setRunning(false);
      }
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by
    // the time the listener below exists. Check the tab directly instead of waiting for a repeat.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-seller-notification').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
