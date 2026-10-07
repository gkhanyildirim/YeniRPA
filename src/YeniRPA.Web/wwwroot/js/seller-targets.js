/* =============================================================================
   Seller Targets — monthly target progress, sent to each seller by Outlook mail
   and WhatsApp group message.

   prepare reads the workbook once; the rendered texts live in the browser so a
   seller's own edits (the preview dialog) are exactly what gets sent. The two
   channels share one automation slot, so sending runs the mails first and starts
   the WhatsApp run when the mail run reports done.

   The destination is never taken from this page: the server resolves each
   seller's address and WhatsApp group from its own saved lists on send.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'seller-targets';
  const TEMPLATE_KEY = 'seller-targets.templates.v3';
  const THRESHOLD_KEY = 'seller-targets.threshold';

  let activated = false;
  let stream = null;
  let running = false;
  let total = 0;
  let mine = false;

  // Run state across the two channels.
  let pendingWhatsApp = null;   // { messages, dryRun } to start once the mail run is done
  let stopRequested = false;
  let currentPhase = 'mail';

  // Prepared data. `sellers` keeps the workbook order; `texts[i]` is the rendered/edited text of
  // sellers[i]; `picked[i]` is which channels are ticked.
  let month = '';
  let sellers = [];
  let texts = {};
  let picked = {};
  let defaults = { subject: '', below: '', above: '', threshold: 100 };
  let templates = { subject: '', below: '', above: '' };
  let threshold = 100;

  let previewIndex = -1;

  function el(id) { return document.getElementById(id); }

  const nf = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 });
  const pf = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 1 });

  /** The { success, message, data } envelope every /api/seller-targets endpoint returns. */
  async function stJson(method, url, payload) {
    const init = { method: method };
    if (payload !== undefined) {
      init.headers = { 'Content-Type': 'application/json' };
      init.body = JSON.stringify(payload);
    }
    return unwrap(await fetch(url, init));
  }

  async function stUpload(url, form) {
    return unwrap(await fetch(url, { method: 'POST', body: form }));
  }

  async function unwrap(response) {
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

  // ---------------------------------------------------------------------------
  // Channels
  // ---------------------------------------------------------------------------

  async function refreshStatus() {
    let status;
    try {
      status = await stJson('GET', '/api/seller-targets/status');
    } catch (e) {
      return;
    }

    const outlook = el('st-outlook-badge');
    if (status.outlookAvailable === true) {
      outlook.className = 'badge green';
      outlook.textContent = 'Outlook ready';
    } else if (status.outlookAvailable === false) {
      outlook.className = 'badge red';
      outlook.textContent = 'Outlook unavailable';
      outlook.title = status.outlookError || '';
    }

    const wa = el('st-wa-badge');
    if (status.signedIn === true) {
      wa.className = 'badge green';
      wa.textContent = 'WhatsApp signed in';
    } else if (status.signedIn === false) {
      wa.className = 'badge red';
      wa.textContent = 'WhatsApp signed out — scan the QR code';
    } else if (status.hasProfile) {
      wa.className = 'badge amber';
      wa.textContent = 'WhatsApp profile saved — not checked yet';
    } else {
      wa.className = 'badge amber';
      wa.textContent = 'WhatsApp: sign in required';
    }

    setRunning(status.isRunning, status.runningModule);
  }

  async function channelAction(buttonId, busyLabel, url) {
    const button = el(buttonId);
    RPA.clearError('st-channel-alert');
    RPA.setBusy(button, true, busyLabel);
    try {
      await stJson('POST', url);
    } catch (err) {
      RPA.showError('st-channel-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      await refreshStatus();
    }
  }

  function isMine(moduleName) {
    return typeof moduleName === 'string' && moduleName.indexOf(MODULE + '-') === 0;
  }

  function setRunning(isRunning, runningModule) {
    running = !!isRunning;

    const send = el('st-send');
    RPA.setBusy(send, running && isMine(runningModule), 'Running…');
    send.disabled = running || selectionCount().total === 0;

    if (running && runningModule && !isMine(runningModule)) {
      send.title = 'Another automation run (' + runningModule + ') holds the slot.';
    } else {
      send.removeAttribute('title');
    }

    const stop = el('st-stop');
    stop.hidden = !(running && isMine(runningModule));
    if (running) el('st-run').hidden = false;
  }

  async function stopRun() {
    if (!window.confirm('Stop this run?\n\nWhat was already sent cannot be recalled. The run stops before the next one, and the WhatsApp step is skipped.')) return;

    stopRequested = true;
    pendingWhatsApp = null;
    try {
      await RPA.sendJson('/api/automation/stop', {});
    } catch (err) {
      RPA.showError('st-messages-alert', 'The run could not be stopped: ' + err.message);
    }
  }

  // ---------------------------------------------------------------------------
  // Run log
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('st-console');
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('st-progress-fill').style.width = percent + '%';
    el('st-progress').setAttribute('aria-valuenow', String(percent));
    el('st-progress-text').textContent = completed + ' / ' + total;
  }

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = isMine(event.module);
        if (!mine) return;
        total = event.total;
        el('st-run').hidden = false;
        // The WhatsApp run continues the same log rather than wiping what the mail run wrote.
        if (currentPhase === 'mail') el('st-console').textContent = '';
        else appendLog('');
        appendLog('— ' + (currentPhase === 'mail' ? 'Mail' : 'WhatsApp') + ' —');
        el('st-progress').classList.remove('is-done');
        el('st-progress-label').textContent = currentPhase === 'mail' ? 'Mails processed' : 'WhatsApp messages processed';
        setProgress(0);
        setRunning(true, event.module);
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
        if (event.failed.length) appendLog('Failed:\n  ' + event.failed.join('\n  '));
        el('st-progress').classList.add('is-done');
        mine = false;

        if (pendingWhatsApp && !stopRequested) {
          const next = pendingWhatsApp;
          pendingWhatsApp = null;
          startWhatsApp(next);
        } else {
          setRunning(false);
          refreshStatus();
        }
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
    stream.addEventListener('error', function () { });
  }

  // ---------------------------------------------------------------------------
  // Templates
  // ---------------------------------------------------------------------------

  async function loadTemplates() {
    try {
      const data = await stJson('GET', '/api/seller-targets/templates');
      defaults = { subject: data.subject, below: data.belowBody, above: data.aboveBody, threshold: data.threshold };
      el('st-placeholders').innerHTML = data.placeholders.map(p => '<code>' + RPA.escapeHtml(p) + '</code>').join(' ');
    } catch (err) {
      RPA.showError('st-prepare-alert', err.message);
    }

    // An unsent edit is a per-viewer convenience, so the browser keeps it; the defaults come from the server.
    let saved = null;
    try { saved = JSON.parse(localStorage.getItem(TEMPLATE_KEY) || 'null'); } catch (e) { saved = null; }
    templates = {
      subject: (saved && saved.subject) || defaults.subject,
      below: (saved && saved.below) || defaults.below,
      above: (saved && saved.above) || defaults.above
    };

    let savedThreshold = NaN;
    try { savedThreshold = parseFloat(localStorage.getItem(THRESHOLD_KEY)); } catch (e) { savedThreshold = NaN; }
    threshold = isFinite(savedThreshold) ? savedThreshold : defaults.threshold;
    el('st-threshold').value = threshold;
  }

  function openTemplates() {
    el('st-tpl-subject').value = templates.subject;
    el('st-tpl-below').value = templates.below;
    el('st-tpl-above').value = templates.above;
    el('st-templates-dialog').showModal();
  }

  async function applyTemplates() {
    templates = {
      subject: el('st-tpl-subject').value,
      below: el('st-tpl-below').value,
      above: el('st-tpl-above').value
    };
    try { localStorage.setItem(TEMPLATE_KEY, JSON.stringify(templates)); } catch (e) { /* private window */ }

    el('st-templates-dialog').close();
    await renderMessages();
  }

  // ---------------------------------------------------------------------------
  // Prepare + messages
  // ---------------------------------------------------------------------------

  async function prepare() {
    const file = el('st-file').files[0];
    if (!file) {
      RPA.showError('st-prepare-alert', 'Pick the monthly seller targets workbook first.');
      return;
    }
    RPA.clearError('st-prepare-alert');

    const form = new FormData();
    form.append('file', file);
    const directory = el('st-directory-file').files[0];
    if (directory) form.append('directory', directory);

    const button = el('st-prepare');
    RPA.setBusy(button, true, 'Working…');
    RPA.showSkeleton('st-skeleton', 'st-prepared');
    try {
      const data = await stUpload('/api/seller-targets/prepare', form);
      month = data.month;
      sellers = data.sellers;
      picked = {};
      el('st-month-title').textContent = month + ' targets';
      el('st-prepared').hidden = false;
      RPA.stamp('st-stamp');
      await renderMessages();
    } catch (err) {
      RPA.showError('st-prepare-alert', err.message);
    } finally {
      RPA.hideSkeleton('st-skeleton');
      RPA.setBusy(button, false);
    }
  }

  /** Renders every seller's texts from the templates. Discards per-seller edits, by design. */
  async function renderMessages() {
    if (!sellers.length) return;
    RPA.clearError('st-messages-alert');

    const withData = sellers.map((s, i) => ({ s: s, i: i })).filter(x => x.s.hasData);
    try {
      const result = await stJson('POST', '/api/seller-targets/messages', {
        rows: withData.map(x => ({
          sellerId: x.s.sellerId, sellerName: x.s.sellerName, target: x.s.target, current: x.s.current,
          days: x.s.days, daysInMonth: x.s.daysInMonth
        })),
        month: month,
        subject: templates.subject,
        belowBody: templates.below,
        aboveBody: templates.above,
        threshold: threshold
      });

      texts = {};
      // The server answers in the order it was asked.
      result.messages.forEach(function (m, n) {
        texts[withData[n].i] = { subject: m.mailSubject, mail: m.mailBody, wa: m.whatsAppBody };
        withData[n].s.below = m.belowThreshold;
      });

      if (result.warnings && result.warnings.length) RPA.showError('st-messages-alert', result.warnings.join(' '));
    } catch (err) {
      RPA.showError('st-messages-alert', err.message);
    }

    renderStats();
    renderGrid();
    renderSplit();
  }

  /** Applies a changed threshold: remembered in this browser, then every text is re-chosen. */
  function setThreshold() {
    const value = parseFloat(el('st-threshold').value);
    if (!isFinite(value) || value < 0) return;
    threshold = value;
    try { localStorage.setItem(THRESHOLD_KEY, String(value)); } catch (e) { /* private window */ }
    renderMessages();
  }

  function renderSplit() {
    const withData = sellers.filter(s => s.hasData);
    const below = withData.filter(s => s.below).length;
    el('st-threshold-split').innerHTML =
      '<span class=\"badge red\">' + below + ' below ' + pf.format(threshold) + '%</span> ' +
      '<span class=\"badge green\">' + (withData.length - below) + ' at or above</span>';
  }

  // ---------------------------------------------------------------------------
  // Cards
  // ---------------------------------------------------------------------------

  function mailReady(s) { return s.hasData && !!s.email; }
  function waReady(s) { return s.hasData && !!s.groupName; }

  function renderStats() {
    const withData = sellers.filter(s => s.hasData);
    const reached = withData.reduce((sum, s) => sum + s.current, 0);
    const targeted = withData.reduce((sum, s) => sum + s.target, 0);

    const tiles = [
      [sellers.length, 'Sellers in the file'],
      [withData.length, 'With a revenue figure'],
      [sellers.filter(mailReady).length, 'Ready for mail'],
      [sellers.filter(waReady).length, 'Ready for WhatsApp'],
      [targeted > 0 ? pf.format(reached / targeted * 100) + '%' : '—', 'Combined completion (sellers with data)']
    ];

    el('st-stats').innerHTML = tiles.map(t =>
      '<div class="st-stat"><div class="st-stat-value">' + RPA.escapeHtml(String(t[0])) + '</div>' +
      '<div class="st-stat-label">' + RPA.escapeHtml(t[1]) + '</div></div>').join('');
  }

  function cardHtml(s, i) {
    const pick = picked[i] || { mail: false, wa: false };

    let top;
    if (s.hasData) {
      const pct = s.percent || 0;
      const done = pct >= 100;
      const fc = s.forecastPercent || 0;
      const forecast = '<div class="st-forecast">Month-end forecast <b>%' + pf.format(fc) + '</b> ' +
        '<span class="badge ' + (s.below ? 'red' : 'green') + '">' + (s.below ? 'Below threshold' : 'On track') + '</span></div>';
      top =
        '<div class="st-bar" role="img" aria-label="' + pf.format(pct) + ' percent of the target"><span class="st-bar-fill' + (done ? ' is-done' : '') +
        '" style="width:' + Math.min(100, pct) + '%"></span></div>' +
        '<div class="st-figures"><span>Current <b>' + nf.format(s.current) + ' TL</b></span>' +
        '<span>Target <b>' + nf.format(s.target) + ' TL</b></span></div>' + forecast;
    } else {
      top = '<div class="st-figures"><span>Target <b>' + nf.format(s.target) + ' TL</b></span>' +
        '<span class="badge amber">No revenue data</span></div>';
    }

    function channel(kind, label, ready, dest, problem) {
      return '<label class="st-channel' + (ready ? '' : ' is-missing') + '">' +
        '<input type="checkbox" data-index="' + i + '" data-kind="' + kind + '"' +
        (ready ? '' : ' disabled') + (pick[kind] ? ' checked' : '') + ' />' +
        '<span>' + label + '</span>' +
        '<span class="st-dest" title="' + RPA.escapeHtml(ready ? dest : (problem || '')) + '">' +
        RPA.escapeHtml(ready ? dest : (s.hasData ? (problem || 'Not available') : 'No notification')) + '</span></label>';
    }

    return '<div class="st-card' + (s.hasData ? '' : ' is-nodata') + '" data-index="' + i + '">' +
      '<div class="st-card-head"><div><div class="st-name">' + RPA.escapeHtml(s.sellerName) + '</div>' +
      '<div class="st-id">' + (s.sellerId ? 'ID ' + RPA.escapeHtml(s.sellerId) : 'no ID') + '</div></div>' +
      (s.hasData ? '<div class="st-percent' + (s.percent >= 100 ? ' is-done' : '') + '">%' + pf.format(s.percent) + '</div>' : '') +
      '</div>' + top +
      '<div class="st-channels">' +
      channel('mail', 'Mail', mailReady(s), s.email || '', s.emailProblem) +
      channel('wa', 'WhatsApp', waReady(s), s.groupName || '', s.groupProblem) +
      '</div>' +
      '<div class="st-card-foot">' +
      (s.hasData ? '<button type="button" class="btn btn-ghost btn-sm st-preview" data-index="' + i + '">Preview</button>' : '') +
      '</div></div>';
  }

  function visibleIndexes() {
    const term = el('st-search').value.trim().toLowerCase();
    const onlyData = el('st-only-data').checked;

    return sellers.map((s, i) => i).filter(function (i) {
      const s = sellers[i];
      if (onlyData && !s.hasData) return false;
      if (!term) return true;
      return (s.sellerName + ' ' + s.sellerId).toLowerCase().indexOf(term) >= 0;
    });
  }

  function renderGrid() {
    const indexes = visibleIndexes();
    el('st-grid').innerHTML = indexes.length
      ? indexes.map(i => cardHtml(sellers[i], i)).join('')
      : '<div class="empty-state">No seller matches.</div>';
    updateSelection();
  }

  function selectionCount() {
    let mail = 0;
    let wa = 0;
    Object.keys(picked).forEach(function (k) {
      if (picked[k].mail) mail++;
      if (picked[k].wa) wa++;
    });
    return { mail: mail, wa: wa, total: mail + wa };
  }

  function updateSelection() {
    const c = selectionCount();
    el('st-selection-summary').textContent = c.total
      ? c.mail + ' mail(s) · ' + c.wa + ' WhatsApp message(s) selected'
      : 'Nothing selected';
    el('st-send').disabled = running || c.total === 0;
  }

  function selectAllReady() {
    sellers.forEach(function (s, i) {
      if (!texts[i]) return;
      picked[i] = { mail: mailReady(s), wa: waReady(s) };
    });
    renderGrid();
  }

  // ---------------------------------------------------------------------------
  // Preview dialog
  // ---------------------------------------------------------------------------

  function showPane(kind) {
    el('st-tab-mail').setAttribute('aria-selected', kind === 'mail' ? 'true' : 'false');
    el('st-tab-wa').setAttribute('aria-selected', kind === 'wa' ? 'true' : 'false');
    el('st-pane-mail').hidden = kind !== 'mail';
    el('st-pane-wa').hidden = kind !== 'wa';
  }

  function openPreview(index) {
    const s = sellers[index];
    const t = texts[index];
    if (!s || !t) return;

    previewIndex = index;
    el('st-preview-title').textContent = s.sellerName + ' — ' + (s.hasData ? '%' + pf.format(s.percent) : '');
    el('st-preview-to').value = s.email || '(no saved address)';
    el('st-preview-subject').value = t.subject;
    el('st-preview-mail').value = t.mail;
    el('st-preview-group').value = s.groupName || '(no mapped group)';
    el('st-preview-wa').value = t.wa;
    showPane('mail');
    el('st-preview-dialog').showModal();
  }

  function savePreview() {
    if (previewIndex < 0) return;
    texts[previewIndex] = {
      subject: el('st-preview-subject').value,
      mail: el('st-preview-mail').value,
      wa: el('st-preview-wa').value
    };
    el('st-preview-dialog').close();
  }

  // ---------------------------------------------------------------------------
  // Contacts dialog
  // ---------------------------------------------------------------------------

  function openContacts() {
    el('st-contacts-body').innerHTML = sellers.map(function (s, i) {
      return '<tr data-index="' + i + '">' +
        '<td>' + RPA.escapeHtml(s.sellerId || '') + '</td>' +
        '<td>' + RPA.escapeHtml(s.sellerName) + '</td>' +
        '<td><input type="text" class="ct-email" value="' + RPA.escapeHtml(s.email || '') + '" aria-label="E-mail" spellcheck="false" /></td>' +
        '<td><input type="text" class="ct-group" value="' + RPA.escapeHtml(s.groupName || '') + '" aria-label="WhatsApp group" spellcheck="false" /></td>' +
        '</tr>';
    }).join('');
    RPA.clearError('st-contacts-alert');
    el('st-contacts-dialog').showModal();
  }

  async function saveContacts() {
    const button = el('st-contacts-save');
    RPA.clearError('st-contacts-alert');

    const rows = Array.from(el('st-contacts-body').querySelectorAll('tr'));
    const contacts = rows.map(function (row) {
      const s = sellers[Number(row.dataset.index)];
      return {
        sellerId: s.sellerId,
        sellerName: s.sellerName,
        email: row.querySelector('.ct-email').value.trim(),
        groupName: row.querySelector('.ct-group').value.trim()
      };
    });

    RPA.setBusy(button, true, 'Saving…');
    try {
      const result = await stJson('PUT', '/api/seller-targets/contacts', { contacts: contacts });

      contacts.forEach(function (c, n) {
        const s = sellers[Number(rows[n].dataset.index)];
        if (c.email) { s.email = c.email; s.emailProblem = null; }
        if (c.groupName) { s.groupName = c.groupName; s.groupProblem = null; }
      });

      el('st-contacts-dialog').close();
      RPA.showError('st-messages-alert', result.mailSaved + ' address(es) and ' + result.groupsSaved + ' group(s) saved.');
      renderStats();
      renderGrid();
    } catch (err) {
      RPA.showError('st-contacts-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Send
  // ---------------------------------------------------------------------------

  function collectMessages() {
    const mail = [];
    const wa = [];
    sellers.forEach(function (s, i) {
      const pick = picked[i];
      const t = texts[i];
      if (!pick || !t) return;
      if (pick.mail && mailReady(s)) mail.push({ sellerId: s.sellerId, sellerName: s.sellerName, email: s.email, subject: t.subject, body: t.mail });
      if (pick.wa && waReady(s)) wa.push({ sellerId: s.sellerId, sellerName: s.sellerName, body: t.wa });
    });
    return { mail: mail, wa: wa };
  }

  function confirmSend(batch, dryRun) {
    const names = new Set(batch.mail.concat(batch.wa).map(m => m.sellerName));
    const shown = Array.from(names).slice(0, 12);
    const rest = names.size - shown.length;

    const heading = dryRun
      ? 'DRY RUN — ' + batch.mail.length + ' mail draft(s) and ' + batch.wa.length + ' WhatsApp compose(s), nothing is sent?'
      : 'SEND ' + batch.mail.length + ' mail(s) and ' + batch.wa.length + ' WhatsApp message(s) for real? Neither can be recalled.';

    return window.confirm(heading + '\n\n  ' + shown.join('\n  ') + (rest > 0 ? '\n  …and ' + rest + ' more' : ''));
  }

  async function send() {
    const batch = collectMessages();
    if (!batch.mail.length && !batch.wa.length) return;

    const dryRun = el('st-dry-run').checked;
    if (!confirmSend(batch, dryRun)) return;

    RPA.clearError('st-messages-alert');
    connect();
    stopRequested = false;
    setRunning(true, MODULE + '-mail');
    el('st-run').hidden = false;

    try {
      if (batch.mail.length) {
        currentPhase = 'mail';
        pendingWhatsApp = batch.wa.length ? { messages: batch.wa, dryRun: dryRun } : null;
        await stJson('POST', '/api/seller-targets/send-mail', {
          messages: batch.mail, dryRun: dryRun, includeSignature: el('st-signature').checked
        });
      } else {
        await startWhatsApp({ messages: batch.wa, dryRun: dryRun });
      }
    } catch (err) {
      pendingWhatsApp = null;
      RPA.showError('st-messages-alert', err.message);
      setRunning(false);
    }
  }

  /** The mail run's done event fires just before the server frees the slot, so a refusal is retried briefly. */
  async function startWhatsApp(next) {
    currentPhase = 'whatsapp';

    for (let attempt = 0; attempt < 6; attempt++) {
      try {
        await stJson('POST', '/api/seller-targets/send-whatsapp', { messages: next.messages, dryRun: next.dryRun });
        return;
      } catch (err) {
        const busy = /already in progress/i.test(err.message);
        if (!busy || attempt === 5) {
          RPA.showError('st-messages-alert', 'WhatsApp step: ' + err.message);
          setRunning(false);
          refreshStatus();
          return;
        }
        await new Promise(resolve => setTimeout(resolve, 700));
      }
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
    RPA.initDropzone('st-drop', 'st-file');
    RPA.initDropzone('st-directory-drop', 'st-directory-file');

    el('st-prepare').addEventListener('click', prepare);
    el('st-send').addEventListener('click', send);
    el('st-stop').addEventListener('click', stopRun);

    el('st-check-outlook').addEventListener('click', async function () {
      const button = el('st-check-outlook');
      RPA.clearError('st-channel-alert');
      RPA.setBusy(button, true, 'Checking…');
      try {
        const result = await stJson('POST', '/api/seller-targets/check-outlook');
        if (!result.available) RPA.showError('st-channel-alert', result.error || 'Outlook could not be reached.');
      } catch (err) {
        RPA.showError('st-channel-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
        refreshStatus();
      }
    });

    el('st-wa-login').addEventListener('click', () => channelAction('st-wa-login', 'Opening…', '/api/seller-targets/login'));
    el('st-wa-check').addEventListener('click', () => channelAction('st-wa-check', 'Checking…', '/api/seller-targets/check-session'));

    el('st-search').addEventListener('input', renderGrid);
    el('st-only-data').addEventListener('change', renderGrid);
    el('st-select-all').addEventListener('click', selectAllReady);
    el('st-select-none').addEventListener('click', function () { picked = {}; renderGrid(); });

    el('st-contacts-open').addEventListener('click', openContacts);
    el('st-contacts-save').addEventListener('click', saveContacts);
    el('st-templates-open').addEventListener('click', openTemplates);
    el('st-threshold').addEventListener('change', setThreshold);
    el('st-tpl-apply').addEventListener('click', applyTemplates);
    el('st-tpl-reset').addEventListener('click', function () {
      el('st-tpl-subject').value = defaults.subject;
      el('st-tpl-below').value = defaults.below;
      el('st-tpl-above').value = defaults.above;
    });

    el('st-preview-save').addEventListener('click', savePreview);
    el('st-tab-mail').addEventListener('click', () => showPane('mail'));
    el('st-tab-wa').addEventListener('click', () => showPane('wa'));

    // Every dialog closes through its own [data-st-close] buttons; Esc is handled by <dialog>.
    el('panel-seller-targets').addEventListener('click', function (event) {
      const close = event.target.closest('[data-st-close]');
      if (close) close.closest('dialog').close();

      const preview = event.target.closest('.st-preview');
      if (preview) openPreview(Number(preview.dataset.index));
    });

    // Delegated: the grid is re-rendered on every filter keystroke.
    el('st-grid').addEventListener('change', function (event) {
      const box = event.target.closest('input[type="checkbox"][data-index]');
      if (!box) return;
      const i = Number(box.dataset.index);
      picked[i] = picked[i] || { mail: false, wa: false };
      picked[i][box.dataset.kind] = box.checked;
      updateSelection();
    });

    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-seller-targets').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
