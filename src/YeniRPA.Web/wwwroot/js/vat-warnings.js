/* =============================================================================
   Seller VAT Warnings — splits the "offers with no VAT rate" export into one
   workbook per seller and mails each seller their own.

   The sibling of offer-warnings.js, with one difference that shapes the whole
   panel: there the mapping table is the input and the attachments already exist,
   here two uploads are the input and the server writes the attachments. So this
   panel has a prepare that takes files, and a batch id that every send has to
   quote — the server keeps its own copy of which address and which file belong
   to which seller, and that copy is what it sends from.

   Progress arrives over the shared /api/automation/events stream, the same one
   Create Return and the other warning modules report on.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'vat-warnings';

  let activated = false;
  let stream = null;       // EventSource, once the panel has been visited
  let total = 0;
  let running = false;

  // The prepared payload, held here so the cards can re-render without a rebuild.
  let lastMails = [];
  let lastUnmatched = [];

  // Identifies the batch the server prepared. Every send quotes it; a send against a batch the server
  // no longer holds is refused rather than served from a stale pairing.
  let batchId = '';

  // Which mails the operator has ticked, keyed by the server's own seller key. A Set rather than a
  // flag on each mail, so a re-render cannot silently drop a decision made about a row.
  let selected = new Set();

  // The saved hand-entered addresses, as loaded. The unmatched table merges into this list.
  let overrides = [];

  // What the server says "Reset to default" should restore.
  let defaultSubject = '';
  let defaultBody = '';

  function el(id) { return document.getElementById(id); }

  function fmtBytes(bytes) {
    if (!bytes) return '';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(0) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  /**
   * The key a seller is identified by, as the server handed it out. Deliberately not recomputed here:
   * the server folds names its own way, and a key invented in the browser that disagreed would send
   * the operator's tick to a different row than the one they ticked.
   */
  function sellerKey(row) { return row.sellerKey || ''; }

  // ---------------------------------------------------------------------------
  // Outlook + status
  // ---------------------------------------------------------------------------

  async function refreshStatus() {
    const badge = el('vw-outlook-badge');

    let status;
    try {
      const response = await fetch('/api/vat-warnings/status');
      if (!response.ok) throw new Error();
      status = await response.json();
    } catch (e) {
      badge.className = 'badge red';
      badge.textContent = 'Status unavailable';
      return;
    }

    // available is null until something has actually asked Outlook — "we have not looked" and
    // "Outlook is not there" are different claims and the badge must not conflate them.
    if (status.outlookAvailable === true) {
      badge.className = 'badge green';
      badge.textContent = 'Outlook reachable';
    } else if (status.outlookAvailable === false) {
      badge.className = 'badge red';
      badge.textContent = 'Outlook not reachable';
    } else {
      badge.className = 'badge amber';
      badge.textContent = 'Not checked yet';
    }

    setRunning(status.isRunning, status.runningModule);
  }

  /** Idempotent: the run state arrives from the POST, from /status and from the event stream. */
  function setRunning(isRunning, runningModule) {
    running = !!isRunning;

    const send = el('vw-send');
    RPA.setBusy(send, running && runningModule === MODULE, 'Running…');
    send.disabled = running || selectedMails().length === 0;

    if (running && runningModule && runningModule !== MODULE) {
      send.title = 'Another automation run (' + runningModule + ') holds the slot.';
    } else {
      send.removeAttribute('title');
    }

    if (running) el('vw-run').hidden = false;
  }

  // ---------------------------------------------------------------------------
  // Run log
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('vw-console');
    // Follow the tail only while the operator is already at the bottom — scrolling back through a
    // long run must not be yanked away by the next line.
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('vw-progress-fill').style.width = percent + '%';
    el('vw-progress').setAttribute('aria-valuenow', String(percent));
    el('vw-progress-text').textContent = completed + ' / ' + total;
  }

  // Only this panel's runs are rendered here. The bus is shared and its log lines carry no module of
  // their own, so the running module is latched on `started` and gates the rest.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('vw-run').hidden = false;
        el('vw-console').textContent = '';
        el('vw-progress').classList.remove('is-done');
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
        if (event.failed.length) appendLog('Failed:\n  ' + event.failed.join('\n  '));
        el('vw-progress').classList.add('is-done');
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
  // Settings
  // ---------------------------------------------------------------------------

  function overrideRowHtml(entry) {
    return '<tr>' +
      '<td><input type="text" class="ov-id" value="' + RPA.escapeHtml(entry.sellerId || '') + '" aria-label="Seller ID" /></td>' +
      '<td><input type="text" class="ov-name" value="' + RPA.escapeHtml(entry.sellerName || '') + '" aria-label="Seller name" /></td>' +
      '<td><input type="text" class="ov-email" value="' + RPA.escapeHtml(entry.email || '') + '" aria-label="E-mail" /></td>' +
      '<td class="num"><button type="button" class="btn btn-ghost btn-sm ov-remove" aria-label="Remove row">Remove</button></td>' +
      '</tr>';
  }

  function renderOverrides() {
    el('vw-overrides-body').innerHTML = overrides.map(overrideRowHtml).join('');
    updateOverridesCount();
  }

  /** Reads the table back out. Rows with no seller are dropped; a half-filled row is kept. */
  function collectOverrides() {
    return Array.from(el('vw-overrides-body').querySelectorAll('tr')).map(function (row) {
      return {
        sellerId: row.querySelector('.ov-id').value.trim(),
        sellerName: row.querySelector('.ov-name').value.trim(),
        email: row.querySelector('.ov-email').value.trim()
      };
    }).filter(e => e.sellerId || e.sellerName);
  }

  function updateOverridesCount() {
    const entries = collectOverrides();
    const withAddress = entries.filter(e => e.email).length;

    el('vw-overrides-count').textContent = entries.length
      ? RPA.fmtInt(entries.length) + ' seller(s) · ' + RPA.fmtInt(withAddress) + ' with an address'
      : 'No addresses entered by hand yet';
  }

  function renderSettingsWarnings(warnings) {
    const box = el('vw-settings-warnings');
    if (!warnings || !warnings.length) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }
    box.hidden = false;
    box.innerHTML = warnings.map(w => '<span class="badge amber">' + RPA.escapeHtml(w) + '</span>').join(' ');
  }

  async function loadSettings() {
    let data;
    try {
      const response = await fetch('/api/vat-warnings/settings');
      if (!response.ok) throw new Error('Request failed with status ' + response.status + '.');
      data = await response.json();
    } catch (err) {
      RPA.showError('vw-settings-alert', 'The settings could not be loaded: ' + err.message);
      return;
    }

    defaultSubject = data.defaultSubjectTemplate || '';
    defaultBody = data.defaultBodyTemplate || '';

    overrides = data.overrides || [];
    renderOverrides();
    renderSettingsWarnings(data.warnings);

    el('vw-subject').value = data.subjectTemplate || '';
    el('vw-body').value = data.bodyTemplate || '';
    el('vw-folder').value = data.outputFolder || '';
    el('vw-sheet').value = data.defaultSheetName || '';
    el('vw-settings-path').textContent = data.path || '';
    el('vw-settings-updated').textContent = data.updatedUtc ? 'Last saved ' + data.updatedUtc : 'Never saved';
    el('vw-output-summary').textContent = 'Default: ' + (data.defaultOutputFolder || '');

    el('vw-placeholders').innerHTML = (data.placeholders || [])
      .map(p => '<code>' + RPA.escapeHtml(p) + '</code>')
      .join(' ');
  }

  /** Writes the whole settings file: the wording, the folder and every hand-entered address. */
  async function putSettings(entries) {
    const result = await sendJsonMethod('PUT', '/api/vat-warnings/settings', {
      subjectTemplate: el('vw-subject').value,
      bodyTemplate: el('vw-body').value,
      outputFolder: el('vw-folder').value,
      overrides: entries
    });

    // What came back, not what went out: the server collapses rows describing one seller, and
    // re-rendering the pre-collapse list would post the duplicates straight back next time.
    overrides = result.overrides || [];
    renderOverrides();
    renderSettingsWarnings(result.warnings);
    el('vw-settings-updated').textContent = 'Saved just now · ' + result.saved + ' hand-entered address(es)';
    return result;
  }

  async function saveSettings() {
    const button = el('vw-save-settings');
    RPA.clearError('vw-settings-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      await putSettings(collectOverrides());
      await refreshStatus();
    } catch (err) {
      RPA.showError('vw-settings-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  function addOverrideRow() {
    el('vw-overrides-body').insertAdjacentHTML('beforeend', overrideRowHtml({}));
    updateOverridesCount();

    const rows = el('vw-overrides-body').querySelectorAll('tr');
    const added = rows[rows.length - 1];
    added.scrollIntoView({ block: 'center', behavior: 'smooth' });
    added.querySelector('.ov-name').focus();
  }

  // ---------------------------------------------------------------------------
  // Unmatched sellers
  // ---------------------------------------------------------------------------

  function unmatchedRowHtml(row) {
    return '<tr data-key="' + RPA.escapeHtml(row.sellerKey) + '">' +
      '<td class="num">' + RPA.escapeHtml(row.sellerId || '-') + '</td>' +
      '<td>' + RPA.escapeHtml(row.sellerName || '-') + '</td>' +
      '<td class="num">' + RPA.fmtInt(row.offerCount) + '</td>' +
      '<td><input type="text" class="um-email" value="" spellcheck="false" aria-label="E-mail for ' +
        RPA.escapeHtml(row.sellerName || row.sellerId) + '" /></td>' +
      '<td><span class="badge amber">' + RPA.escapeHtml(row.reason || '') + '</span></td>' +
      '</tr>';
  }

  function renderUnmatched() {
    el('vw-unmatched').hidden = lastUnmatched.length === 0;
    if (!lastUnmatched.length) return;

    // Sorted by how many offers are waiting on the address: that is the one number that says which of
    // these rows is worth chasing first.
    const rows = lastUnmatched.slice().sort((a, b) => b.offerCount - a.offerCount);

    el('vw-unmatched-body').innerHTML = rows.map(unmatchedRowHtml).join('');
    el('vw-unmatched-summary').textContent =
      RPA.fmtInt(rows.length) + ' seller(s) · ' +
      RPA.fmtInt(rows.reduce((sum, r) => sum + r.offerCount, 0)) + ' offer(s) waiting on an address';
  }

  /**
   * Appends what was typed to the saved list and writes the whole thing.
   *
   * Deliberately appends rather than merging: deduplicating here would mean recomputing the server's
   * seller key in JavaScript, and RPA.fold is *not* that rule — it also strips accents and
   * punctuation. A key that disagreed by one character would add a second row for a seller instead of
   * replacing the first, leaving a stale address in front of the one just entered. The server
   * collapses the list on the way in, where the key rule actually lives.
   */
  async function saveUnmatched() {
    const button = el('vw-unmatched-save');
    RPA.clearError('vw-unmatched-alert');

    const typed = Array.from(el('vw-unmatched-body').querySelectorAll('tr'))
      .map(function (row) {
        const source = lastUnmatched.find(u => u.sellerKey === row.dataset.key);
        return {
          sellerId: source ? source.sellerId : '',
          sellerName: source ? source.sellerName : '',
          email: row.querySelector('.um-email').value.trim()
        };
      })
      .filter(e => e.email);

    if (!typed.length) {
      RPA.showError('vw-unmatched-alert', 'Nothing to save — no address has been entered above.');
      return;
    }

    // The table above is the authority on what is currently saved, including edits not yet written.
    const merged = collectOverrides().concat(typed);

    RPA.setBusy(button, true, 'Saving…');
    try {
      await putSettings(merged);
      el('vw-unmatched-summary').textContent =
        RPA.fmtInt(typed.length) + ' address(es) saved — build the mails again to pick them up';
    } catch (err) {
      RPA.showError('vw-unmatched-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Prepare
  // ---------------------------------------------------------------------------

  const FUNNEL_COLUMNS = [
    { label: 'Where the sellers went', render: f => RPA.escapeHtml(f.label) },
    { label: 'Sellers', render: f => RPA.fmtInt(f.count), numeric: true, value: f => f.count }
  ];

  function funnelRows(funnel) {
    return [
      { label: 'Sellers in the export', count: funnel.sellersInFile },
      { label: 'Ready to send', count: funnel.ready },
      { label: 'No address for this seller', count: funnel.noEmail },
      { label: 'An address does not look valid', count: funnel.invalidEmail },
      { label: 'Address list gives two different addresses', count: funnel.ambiguousEmail },
      { label: 'File name collides with another seller', count: funnel.fileNameClash },
      { label: 'File could not be written', count: funnel.writeFailed }
    ];
  }

  function renderWarnings(warnings) {
    const card = el('vw-warnings');
    if (!warnings || !warnings.length) {
      card.hidden = true;
      return;
    }
    card.hidden = false;
    el('vw-warnings-list').innerHTML = warnings.map(w => '<li>' + RPA.escapeHtml(w) + '</li>').join('');
  }

  function mailCardHtml(mail, index) {
    const broken = !!mail.problem;
    const recipients = mail.recipients || [];

    // The seller is the label on the badge, because the seller is what identifies the mail — and it
    // is the seller/attachment pairing that has to be read to catch a wrong row.
    const head = broken
      ? '<span class="badge amber">' + RPA.escapeHtml(mail.problem) + '</span>'
      : '<label class="check"><input type="checkbox" class="vw-pick" data-index="' + index + '"' +
        (selected.has(sellerKey(mail)) ? ' checked' : '') + ' />' +
        '<span class="badge green">' + RPA.escapeHtml(mail.sellerName || mail.email) + '</span></label>';

    const attachment = mail.attachmentName
      ? RPA.escapeHtml(mail.attachmentName) +
        (mail.attachmentSizeBytes ? ' · ' + fmtBytes(mail.attachmentSizeBytes) : '')
      : 'no attachment';

    const to = recipients.length
      ? recipients.length + (recipients.length === 1 ? ' recipient' : ' recipients') +
        ' · ' + RPA.escapeHtml(recipients.join('; '))
      : 'no recipient';

    // A hand-entered address is visibly hand-entered: it is the one the operator is answerable for.
    const source = mail.matchedBy === 'override' ? ' · ✎ entered by hand' : '';

    return '<div class="msg-card">' +
      '<div class="msg-head">' +
        head +
        '<span class="msg-meta">✉ ' + to + ' · 📎 ' + attachment +
          ' · ' + RPA.fmtInt(mail.offerCount) + ' offer(s)' + source + '</span>' +
      '</div>' +
      '<pre class="msg-body">' + RPA.escapeHtml(mail.subject) + '\n\n' + RPA.escapeHtml(mail.body) + '</pre>' +
    '</div>';
  }

  function selectedMails() {
    return lastMails.filter(m => !m.problem && selected.has(sellerKey(m)));
  }

  function updateSelectionSummary() {
    const ready = lastMails.filter(m => !m.problem).length;
    const picked = selectedMails().length;

    el('vw-mails-summary').textContent = ready
      ? RPA.fmtInt(picked) + ' of ' + RPA.fmtInt(ready) + ' selected'
      : '';

    el('vw-send').disabled = running || picked === 0;
    el('vw-mails-export').disabled = lastMails.length === 0;
  }

  function renderMails() {
    el('vw-mails').innerHTML = lastMails.length
      ? lastMails.map(mailCardHtml).join('')
      : '<div class="empty-state">No mail to compose — the export named no sellers.</div>';

    updateSelectionSummary();
  }

  function renderPrepared(data) {
    lastMails = data.mails || [];
    lastUnmatched = data.unmatched || [];
    batchId = data.batchId || '';

    // Everything that can be sent starts ticked: the operator's job here is to spot the row that
    // should not go, not to tick 123 boxes to get the normal case.
    selected = new Set(lastMails.filter(m => !m.problem).map(sellerKey));

    RPA.setExportContext(
      RPA.fmtInt(data.offersInFile) + ' offers · ' +
      RPA.fmtInt(data.directoryRows) + ' address rows · ' + data.date);

    renderWarnings(data.warnings);
    renderUnmatched();

    RPA.renderTable('vw-funnel-wrap', funnelRows(data.funnel), FUNNEL_COLUMNS, 'Nothing to report.');
    RPA.syncExportButtons();

    renderMails();

    el('vw-output-summary').textContent = 'This run wrote to ' + data.outputFolder;
    el('vw-prepared').hidden = false;
    RPA.stamp('vw-stamp');
  }

  async function prepare() {
    const offers = el('vw-offers-file').files[0];
    const directory = el('vw-directory-file').files[0];

    RPA.clearError('vw-prepare-alert');

    if (!offers) {
      RPA.showError('vw-prepare-alert', 'Choose the offer export first.');
      return;
    }
    if (!directory) {
      RPA.showError('vw-prepare-alert', 'Choose the seller address list as well — without it no mail has a recipient.');
      return;
    }

    const button = el('vw-prepare');
    RPA.setBusy(button, true, 'Splitting…');
    RPA.showSkeleton('vw-prepare-skeleton', 'vw-prepared');
    try {
      const form = new FormData();
      form.append('offers', offers);
      form.append('directory', directory);
      form.append('sheetName', el('vw-sheet').value);
      form.append('subjectTemplate', el('vw-subject').value);
      form.append('bodyTemplate', el('vw-body').value);

      renderPrepared(await RPA.postJson('/api/vat-warnings/prepare', form));
    } catch (err) {
      RPA.showError('vw-prepare-alert', err.message);
    } finally {
      RPA.hideSkeleton('vw-prepare-skeleton');
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Send
  // ---------------------------------------------------------------------------

  /**
   * The last checkpoint before something irreversible. It names the recipients rather than counting
   * them, because reading an address beside its attachment is the only way to notice a wrong row.
   */
  function confirmSend(mails, dryRun) {
    const shown = mails.slice(0, 12).map(m => m.sellerName + '  →  ' + m.email + '  ←  ' + m.attachmentName);
    const rest = mails.length - shown.length;

    const heading = dryRun
      ? 'DRY RUN — compose ' + mails.length + ' mail(s) into Outlook Drafts, sending nothing?'
      : 'SEND ' + mails.length + ' mail(s) for real? A sent mail cannot be recalled.';

    const tail = rest > 0
      ? '\n  …and ' + rest + ' more (all of them are listed on the cards above)'
      : '';

    const slotWarning = dryRun
      ? ''
      : '\n\nThis holds the automation slot for roughly ' +
        Math.max(1, Math.round(mails.length * 3.5 / 60)) + ' minute(s).';

    return window.confirm(heading + '\n\n  ' + shown.join('\n  ') + tail + slotWarning);
  }

  async function send() {
    const mails = selectedMails();
    if (!mails.length) return;

    const dryRun = el('vw-dry-run').checked;
    if (!confirmSend(mails, dryRun)) return;

    RPA.clearError('vw-mails-alert');

    // Opened before the POST so the first events of the run cannot be missed.
    connect();
    setRunning(true, MODULE);
    el('vw-run').hidden = false;

    try {
      await RPA.sendJson('/api/vat-warnings/send', {
        batchId: batchId,
        dryRun: dryRun,
        mails: mails.map(m => ({
          sellerKey: m.sellerKey,
          sellerName: m.sellerName,
          recipients: m.recipients,
          subject: m.subject,
          body: m.body,
          attachmentName: m.attachmentName
        }))
      });
    } catch (err) {
      RPA.showError('vw-mails-alert', err.message);
      setRunning(false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  /** RPA.sendJson is POST-only; the settings save is a PUT. */
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
    loadSettings();
    connect();
    refreshStatus();
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('vw-offers-drop', 'vw-offers-file');
    RPA.initDropzone('vw-directory-drop', 'vw-directory-file');

    el('vw-prepare').addEventListener('click', prepare);
    el('vw-send').addEventListener('click', send);
    el('vw-save-settings').addEventListener('click', saveSettings);
    el('vw-override-add').addEventListener('click', addOverrideRow);
    el('vw-unmatched-save').addEventListener('click', saveUnmatched);

    el('vw-check-outlook').addEventListener('click', async function () {
      const button = el('vw-check-outlook');
      RPA.clearError('vw-outlook-alert');
      RPA.setBusy(button, true, 'Checking…');
      try {
        const result = await sendJsonMethod('POST', '/api/vat-warnings/check-outlook', {});
        if (!result.available && result.error) RPA.showError('vw-outlook-alert', result.error);
      } catch (err) {
        RPA.showError('vw-outlook-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
        await refreshStatus();
      }
    });

    el('vw-mails-export').addEventListener('click', async function () {
      const button = el('vw-mails-export');
      RPA.clearError('vw-mails-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/vat-warnings/mails/excel', { mails: lastMails }, 'vat-warnings.xlsx');
      } catch (err) {
        RPA.showError('vw-mails-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    // Removing a row must not silently drop unsaved edits elsewhere, so the table is never
    // re-rendered on remove — the row is taken out in place.
    el('vw-overrides-body').addEventListener('click', function (event) {
      const button = event.target.closest('.ov-remove');
      if (!button) return;
      button.closest('tr').remove();
      updateOverridesCount();
    });

    // Keeps the count honest while the operator is still filling rows in.
    el('vw-overrides-body').addEventListener('input', updateOverridesCount);

    el('vw-template-reset').addEventListener('click', function () {
      el('vw-subject').value = defaultSubject;
      el('vw-body').value = defaultBody;
    });

    el('vw-select-all').addEventListener('click', function () {
      selected = new Set(lastMails.filter(m => !m.problem).map(sellerKey));
      renderMails();
    });

    el('vw-select-none').addEventListener('click', function () {
      selected = new Set();
      renderMails();
    });

    // Delegated: the cards are rebuilt on every prepare, so per-row listeners would be lost.
    el('vw-mails').addEventListener('change', function (event) {
      const box = event.target.closest('.vw-pick');
      if (!box) return;

      const mail = lastMails[Number(box.dataset.index)];
      if (!mail) return;

      if (box.checked) selected.add(sellerKey(mail));
      else selected.delete(sellerKey(mail));

      updateSelectionSummary();
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by the
    // time the listener below exists. Check the tab directly instead of waiting for a repeat.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-vat-warnings').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
