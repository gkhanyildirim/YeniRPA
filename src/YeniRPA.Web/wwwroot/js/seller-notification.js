/* =============================================================================
   Seller Notification — Mirakl browser automation.

   Opens every order's own Mirakl conversation dialog and sends a topic/message
   through it. Structurally identical to mark-received.js (no prepare/review
   step: paste or upload order IDs, confirm, run) plus one thing that module
   does not have — named message templates, kept per tab (return / undelivered /
   custom) as collapsible cards the operator edits and picks from. Session and
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
  // Message templates — saved per kind (return / undelivered / custom) and edited
  // as collapsible cards. The template picked on the active tab is what a run
  // sends; edits are only persisted by "Save templates".
  // ---------------------------------------------------------------------------

  const KIND_HINTS = {
    'return': 'Sent with Mirakl\'s "Return / Cancel the order" topic. Only the message is editable.',
    'undelivered': 'Sent with Mirakl\'s "Return / Cancel the order" topic for parcels that came back undelivered.',
    'custom': 'Sent with Mirakl\'s "Other reason" topic and the free-text topic you enter on the template.'
  };

  let activeKind = 'return';
  const selected = { 'return': '', 'undelivered': '', 'custom': '' }; // picked template id per kind
  const expanded = new Set();                                          // ids of open cards

  function kindOf(t) {
    return t.kind === 'return' || t.kind === 'undelivered' ? t.kind : 'custom';
  }

  function newId() {
    return window.crypto && crypto.randomUUID ? crypto.randomUUID() : String(Date.now() + Math.random());
  }

  function templateCardHtml(t) {
    const open = expanded.has(t.id);
    const bodyId = 'sn-body-' + t.id;
    const isCustom = kindOf(t) === 'custom';

    return '<div class="msg-card' + (open ? '' : ' is-collapsed') + '" data-id="' + RPA.escapeHtml(t.id) + '">' +
      '<div class="msg-head">' +
        '<label><input type="radio" name="sn-pick" class="tpl-pick" value="' + RPA.escapeHtml(t.id) + '"' +
          (selected[activeKind] === t.id ? ' checked' : '') + ' aria-label="Use this template" /></label>' +
        '<span class="sn-title">' + RPA.escapeHtml(t.name || '(unnamed)') + '</span>' +
        '<button type="button" class="btn btn-ghost btn-sm tpl-toggle" aria-expanded="' + open + '" aria-controls="' + RPA.escapeHtml(bodyId) + '">' +
          (open ? 'Hide' : 'Edit') + '</button>' +
      '</div>' +
      '<div class="msg-body sn-fields" id="' + RPA.escapeHtml(bodyId) + '">' +
        '<div class="field"><label>Name</label><input type="text" class="tpl-name" value="' + RPA.escapeHtml(t.name || '') + '" /></div>' +
        (isCustom
          ? '<div class="field"><label>Topic</label><input type="text" class="tpl-topic" spellcheck="false" value="' + RPA.escapeHtml(t.topic || '') + '" placeholder="What the seller will see as the conversation subject" /></div>'
          : '') +
        '<div class="field"><label>Message</label><textarea class="tpl-message" spellcheck="false">' + RPA.escapeHtml(t.message || '') + '</textarea></div>' +
        '<div><button type="button" class="btn btn-ghost btn-sm tpl-remove">Remove</button></div>' +
      '</div>' +
      '</div>';
  }

  /** Copies what the operator typed in the visible cards back into `templates`, so switching tabs loses nothing. */
  function syncFromDom() {
    el('sn-template-list').querySelectorAll('.msg-card').forEach(function (card) {
      const t = templates.find(x => x.id === card.dataset.id);
      if (!t) return;
      t.name = card.querySelector('.tpl-name').value.trim();
      t.message = card.querySelector('.tpl-message').value;
      const topic = card.querySelector('.tpl-topic');
      if (topic) t.topic = topic.value.trim();
    });
  }

  function visibleTemplates() {
    return templates.filter(t => kindOf(t) === activeKind);
  }

  function renderTemplates() {
    const list = visibleTemplates();
    if (!list.some(t => t.id === selected[activeKind])) selected[activeKind] = list.length ? list[0].id : '';

    el('sn-template-list').innerHTML = list.length
      ? list.map(templateCardHtml).join('')
      : '<div class="sn-empty">No templates on this tab yet. Use "Add template".</div>';
    el('sn-kind-hint').textContent = KIND_HINTS[activeKind];
    el('sn-template-count').textContent = list.length ? list.length + ' template(s)' : '';

    document.querySelectorAll('.sn-tab').forEach(function (tab) {
      tab.setAttribute('aria-selected', String(tab.dataset.kind === activeKind));
    });
    updateSelected();
  }

  function updateSelected() {
    const t = templates.find(x => x.id === selected[activeKind]);
    el('sn-selected').textContent = t
      ? 'Will send: ' + (t.name || '(unnamed)') + ' (' + activeKind + ')'
      : 'No template selected on this tab';
  }

  function switchKind(kind) {
    if (kind === activeKind) return;
    syncFromDom();
    activeKind = kind;
    renderTemplates();
  }

  function addTemplate() {
    syncFromDom();
    const t = { id: newId(), name: 'New template', topic: '', message: '', kind: activeKind };
    templates.push(t);
    expanded.add(t.id);
    selected[activeKind] = selected[activeKind] || t.id;
    renderTemplates();

    const card = el('sn-template-list').querySelector('[data-id="' + t.id + '"]');
    card.scrollIntoView({ block: 'center', behavior: 'smooth' });
    card.querySelector('.tpl-name').select();
  }

  function setAllExpanded(open) {
    syncFromDom();
    visibleTemplates().forEach(t => open ? expanded.add(t.id) : expanded.delete(t.id));
    renderTemplates();
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

    templates = result.data || [];
    renderTemplates();
  }

  async function saveTemplates() {
    const button = el('sn-template-save');
    syncFromDom();
    RPA.clearError('sn-template-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      const payload = templates
        .filter(t => t.name || t.topic || t.message)
        .map(t => ({ id: t.id, name: t.name, topic: t.topic || '', message: t.message || '', kind: kindOf(t) }));
      const result = await sendJsonMethod('PUT', '/api/seller-notification/templates', { templates: payload });
      templates = (result && result.data) || [];
      renderTemplates();
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

    el('sn-template-add').addEventListener('click', addTemplate);
    el('sn-template-save').addEventListener('click', saveTemplates);
    el('sn-expand-all').addEventListener('click', function () { setAllExpanded(true); });
    el('sn-collapse-all').addEventListener('click', function () { setAllExpanded(false); });

    document.querySelectorAll('.sn-tab').forEach(function (tab) {
      tab.addEventListener('click', function () { switchKind(tab.dataset.kind); });
    });

    const list = el('sn-template-list');

    list.addEventListener('click', function (event) {
      const card = event.target.closest('.msg-card');
      if (!card) return;
      const id = card.dataset.id;

      if (event.target.closest('.tpl-remove')) {
        syncFromDom();
        templates = templates.filter(t => t.id !== id);
        expanded.delete(id);
        renderTemplates();
        return;
      }

      const toggle = event.target.closest('.tpl-toggle');
      if (toggle) {
        // Toggled in place rather than re-rendered, so what is being typed in other cards is untouched.
        const open = card.classList.toggle('is-collapsed') === false;
        if (open) expanded.add(id); else expanded.delete(id);
        toggle.setAttribute('aria-expanded', String(open));
        toggle.textContent = open ? 'Hide' : 'Edit';
      }
    });

    list.addEventListener('change', function (event) {
      const pick = event.target.closest('.tpl-pick');
      if (!pick) return;
      selected[activeKind] = pick.value;
      syncFromDom();
      updateSelected();
    });

    list.addEventListener('input', function (event) {
      const name = event.target.closest('.tpl-name');
      if (!name) return;
      name.closest('.msg-card').querySelector('.sn-title').textContent = name.value.trim() || '(unnamed)';
    });

    el('sn-start').addEventListener('click', async function () {
      const file = el('sn-file').files[0];
      const ordersText = el('sn-orders-text').value.trim();

      syncFromDom();
      const template = templates.find(t => t.id === selected[activeKind]);
      const topic = template && activeKind === 'custom' ? (template.topic || '').trim() : '';
      const message = template ? (template.message || '').trim() : '';

      if (!template) {
        RPA.showError('sn-alert', 'Pick a template on the ' + activeKind + ' tab first.');
        return;
      }
      if (!file && !ordersText) {
        RPA.showError('sn-alert', 'Upload a .txt file or paste order IDs.');
        return;
      }
      if (activeKind === 'custom' && !topic) {
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
        'Send "' + (template.name || 'unnamed') + '" to ' + count + ' order(s) on Mirakl? This writes to the marketplace and cannot be undone from here.'))
        return;

      RPA.clearError('sn-alert');

      const form = new FormData();
      if (file) form.append('file', file);
      if (ordersText) form.append('orders', ordersText);
      form.append('kind', activeKind);
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
