/* =============================================================================
   Seller Offer Warnings — one warning mail per seller, carrying that seller's
   own offer list as an attachment.

   The mapping table is the input here, not an export: prepare renders from what
   is saved plus whatever wording is currently in the template boxes, so trying
   out a sentence costs a few KB rather than a re-upload. Sending drives the
   Outlook running on the server side; progress arrives over the shared
   /api/automation/events stream, the same one Create Return and Late Order
   Warnings report on.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'offer-warnings';

  // The address fetch is a second run this panel owns: it drives the Mirakl browser rather than
  // Outlook, so it is its own module on the shared bus, but it reports into the same run log.
  const FETCH_MODULE = 'offer-emails';

  let activated = false;
  let stream = null;       // EventSource, once the panel has been visited
  let total = 0;
  let running = false;

  // The prepared payload, held here so a template edit can re-render without a reload.
  let lastMails = [];

  // Which mails the operator has ticked, keyed by seller. A Set rather than a flag on each mail, so
  // a re-render cannot silently drop a decision that was made about a row.
  let selected = new Set();

  // What the server says "Reset to default" should restore.
  let defaultSubject = '';
  let defaultBody = '';

  function el(id) { return document.getElementById(id); }

  /**
   * A mail is identified by its seller, not by its address: a seller has several users on one mail,
   * and one agency address can be a recipient for several sellers.
   *
   * Deliberately *not* RPA.fold — this is only a local key for which checkboxes are ticked, and
   * borrowing the fold would imply it agrees with the server's SellerKey, which folds differently.
   * The server does its own resolution and refuses anything ambiguous.
   */
  function sellerKey(row) {
    const id = (row.sellerId || '').trim();
    return id ? 'id:' + id : 'name:' + (row.sellerName || '').trim().toLowerCase();
  }

  function fmtBytes(bytes) {
    if (!bytes) return '';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(0) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  // ---------------------------------------------------------------------------
  // Outlook + folder status
  // ---------------------------------------------------------------------------

  async function refreshStatus() {
    const badge = el('ow-outlook-badge');

    let status;
    try {
      const response = await fetch('/api/offer-warnings/status');
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

    renderFolderSummary(status);
    setRunning(status.isRunning, status.runningModule);
  }

  function renderFolderSummary(status) {
    const summary = el('ow-folder-summary');
    if (!status.folderExists) {
      summary.textContent = 'Folder not found';
      return;
    }
    summary.textContent = RPA.fmtInt(status.filesInFolder) + ' file(s) in the folder';
  }

  async function checkFolder() {
    const button = el('ow-check-folder');
    RPA.setBusy(button, true, 'Looking…');
    try {
      await refreshStatus();
    } finally {
      RPA.setBusy(button, false);
    }
  }

  /** Idempotent: the run state arrives from the POST, from /status and from the event stream. */
  function setRunning(isRunning, runningModule) {
    running = !!isRunning;

    const send = el('ow-send');
    RPA.setBusy(send, running && runningModule === MODULE, 'Running…');
    send.disabled = running || selectedMails().length === 0;

    // One run slot for the whole app, so the fetch and the send lock each other out as well as
    // locking out Create Return.
    const fetchButton = el('ow-map-fetch');
    RPA.setBusy(fetchButton, running && runningModule === FETCH_MODULE, 'Reading Mirakl…');
    fetchButton.disabled = running;

    if (running && runningModule && runningModule !== MODULE) {
      send.title = 'Another automation run (' + runningModule + ') holds the slot.';
    } else {
      send.removeAttribute('title');
    }

    if (running) el('ow-run').hidden = false;
  }

  // ---------------------------------------------------------------------------
  // Run log
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('ow-console');
    // Follow the tail only while the operator is already at the bottom — scrolling back through a
    // long run must not be yanked away by the next line.
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('ow-progress-fill').style.width = percent + '%';
    el('ow-progress').setAttribute('aria-valuenow', String(percent));
    el('ow-progress-text').textContent = completed + ' / ' + total;
  }

  // Only this panel's runs are rendered here. The bus is shared and its log lines carry no module of
  // their own, so the running module is latched on `started` and gates the rest.
  let mine = null;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = (event.module === MODULE || event.module === FETCH_MODULE) ? event.module : null;
        if (!mine) return;
        total = event.total;
        el('ow-run').hidden = false;
        el('ow-console').textContent = '';
        el('ow-progress').classList.remove('is-done');
        el('ow-progress-label').textContent = mine === FETCH_MODULE ? 'Seller pages read' : 'Mails processed';
        setProgress(0);
        setRunning(true, mine);
        break;

      case 'log':
        if (mine) appendLog(event.message);
        break;

      case 'progress':
        if (!mine) return;
        total = event.total;
        setProgress(event.completed);
        break;

      case 'done': {
        if (!mine) return;
        const wasFetch = mine === FETCH_MODULE;

        appendLog('');
        appendLog('Finished. Processed: ' + event.processed + ' · Failed: ' + event.failed.length);
        if (event.failed.length) appendLog('Failed:\n  ' + event.failed.join('\n  '));
        el('ow-progress').classList.add('is-done');
        setRunning(false);

        // The bus carries progress; the table it produced is collected separately.
        if (wasFetch) collectFetchResult();
        refreshStatus();
        break;
      }
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
  // Mapping table
  // ---------------------------------------------------------------------------

  function mappingRowHtml(entry) {
    return '<tr>' +
      '<td><input type="text" class="map-id" value="' + RPA.escapeHtml(entry.sellerId || '') + '" aria-label="Seller ID" /></td>' +
      '<td><input type="text" class="map-name" value="' + RPA.escapeHtml(entry.sellerName || '') + '" aria-label="Seller name" /></td>' +
      '<td><input type="text" class="map-email" value="' + RPA.escapeHtml(entry.email || '') + '" aria-label="E-mail" /></td>' +
      '<td><input type="text" class="map-file" value="' + RPA.escapeHtml(entry.fileName || '') + '" aria-label="Attachment file name" /></td>' +
      '<td><input type="number" class="map-lead0" min="0" value="' + Number(entry.leadTime0 || 0) + '" aria-label="Offers at lead time 0" /></td>' +
      '<td><input type="number" class="map-lead1" min="0" value="' + Number(entry.leadTime1 || 0) + '" aria-label="Offers at lead time 1" /></td>' +
      '<td class="num"><button type="button" class="btn btn-ghost btn-sm map-remove" aria-label="Remove row">Remove</button></td>' +
      '</tr>';
  }

  function renderMapping(entries) {
    el('ow-mapping-body').innerHTML = (entries || []).map(mappingRowHtml).join('');
    updateMappingCount();
  }

  /**
   * Counts what would actually be saved, not what is on screen: a row with no seller is dropped by
   * the server, so counting <tr> elements would disagree with the save confirmation.
   */
  function updateMappingCount() {
    const entries = collectMapping();
    const ready = entries.filter(e => e.email && e.fileName).length;

    el('ow-mapping-count').textContent = entries.length
      ? RPA.fmtInt(entries.length) + ' seller(s) · ' + RPA.fmtInt(ready) + ' with an address and a file'
      : 'No sellers mapped yet';
  }

  /** Reads the table back out. Rows with no seller are dropped; a half-filled row is kept. */
  function collectMapping() {
    return Array.from(el('ow-mapping-body').querySelectorAll('tr')).map(function (row) {
      return {
        sellerId: row.querySelector('.map-id').value.trim(),
        sellerName: row.querySelector('.map-name').value.trim(),
        email: row.querySelector('.map-email').value.trim(),
        fileName: row.querySelector('.map-file').value.trim(),
        leadTime0: Number(row.querySelector('.map-lead0').value) || 0,
        leadTime1: Number(row.querySelector('.map-lead1').value) || 0
      };
    }).filter(e => e.sellerId || e.sellerName);
  }

  function renderMappingWarnings(warnings) {
    const box = el('ow-mapping-warnings');
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

  function addMappingRow() {
    el('ow-mapping-body').insertAdjacentHTML('beforeend', mappingRowHtml({}));
    updateMappingCount();

    const rows = el('ow-mapping-body').querySelectorAll('tr');
    const added = rows[rows.length - 1];
    added.scrollIntoView({ block: 'center', behavior: 'smooth' });
    added.querySelector('.map-name').focus();
  }

  async function loadMapping() {
    let data;
    try {
      const response = await fetch('/api/offer-warnings/mapping');
      if (!response.ok) throw new Error('Request failed with status ' + response.status + '.');
      data = await response.json();
    } catch (err) {
      RPA.showError('ow-mapping-alert', 'The mapping could not be loaded: ' + err.message);
      return;
    }

    defaultSubject = data.defaultSubjectTemplate || '';
    defaultBody = data.defaultBodyTemplate || '';

    renderMapping(data.entries);
    renderMappingWarnings(data.warnings);

    el('ow-subject').value = data.subjectTemplate || '';
    el('ow-body').value = data.bodyTemplate || '';
    el('ow-folder').value = data.attachmentFolder || '';
    el('ow-mapping-path').textContent = data.path || '';
    el('ow-mapping-updated').textContent = data.updatedUtc ? 'Last saved ' + data.updatedUtc : 'Never saved';

    el('ow-placeholders').innerHTML = (data.placeholders || [])
      .map(p => '<code>' + RPA.escapeHtml(p) + '</code>')
      .join(' ');
  }

  async function saveMapping() {
    const button = el('ow-map-save');
    RPA.clearError('ow-mapping-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      const result = await sendJsonMethod('PUT', '/api/offer-warnings/mapping', {
        entries: collectMapping(),
        subjectTemplate: el('ow-subject').value,
        bodyTemplate: el('ow-body').value,
        attachmentFolder: el('ow-folder').value
      });
      renderMappingWarnings(result.warnings);
      el('ow-mapping-updated').textContent = 'Saved just now · ' + result.saved + ' entries';
      await refreshStatus();
    } catch (err) {
      RPA.showError('ow-mapping-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  function renderFetchProblems(problems) {
    const box = el('ow-fetch-problems');
    if (!problems || !problems.length) {
      box.hidden = true;
      return;
    }
    box.hidden = false;
    RPA.renderTable('ow-fetch-problems-wrap', problems, FETCH_PROBLEM_COLUMNS, '');
  }

  /**
   * Starts the address fetch. It drives a real browser through one seller page at a time, so it runs
   * in the background and reports into the run log; the finished table is collected when it is done.
   * Like the import, it replaces the table in place and leaves saving to the operator.
   */
  async function fetchEmails() {
    const button = el('ow-map-fetch');
    RPA.clearError('ow-mapping-alert');
    renderFetchProblems([]);
    RPA.setBusy(button, true, 'Reading Mirakl…');

    // Opened before the POST so the first events of the run cannot be missed.
    connect();
    el('ow-run').hidden = false;

    try {
      await RPA.sendJson('/api/offer-warnings/mapping/fetch-emails', {
        entries: collectMapping(),
        onlyMissing: el('ow-fetch-missing').checked
      });
      el('ow-mapping-updated').textContent = 'Reading the Mirakl back office — watch the run log below';
    } catch (err) {
      RPA.showError('ow-mapping-alert', err.message);
      RPA.setBusy(button, false);
    }
  }

  /** Picks up the table the fetch produced, once its run reports done. */
  async function collectFetchResult() {
    RPA.setBusy(el('ow-map-fetch'), false);

    let result;
    try {
      const response = await fetch('/api/offer-warnings/mapping/fetch-result');
      if (!response.ok) throw new Error('Request failed with status ' + response.status + '.');
      result = await response.json();
    } catch (err) {
      RPA.showError('ow-mapping-alert', 'The fetched table could not be collected: ' + err.message);
      return;
    }

    if (!result.available) return;

    if (result.error) {
      // The table comes back untouched in this case — say so, rather than letting a half-applied
      // fetch sit there looking finished.
      RPA.showError('ow-mapping-alert', result.error);
      el('ow-mapping-updated').textContent = 'Fetch stopped — the table was not changed';
      return;
    }

    renderMapping(result.entries);
    renderFetchProblems(result.problems);

    const parts = [
      result.filled + ' filled',
      result.unchanged + ' already had an address',
      result.noSellerId + ' without a seller ID'
    ];
    if (result.skippedDisabled) parts.push(result.skippedDisabled + ' disabled user(s) skipped');

    el('ow-mapping-updated').textContent = 'Fetched: ' + parts.join(', ') + ' — not saved yet';
  }

  async function importMapping(file) {
    const button = el('ow-map-import');
    RPA.clearError('ow-mapping-alert');
    RPA.setBusy(button, true, 'Reading…');
    try {
      const form = new FormData();
      form.append('file', file);
      const result = await RPA.postJson('/api/offer-warnings/mapping/import', form);

      renderMapping(result.entries);
      el('ow-mapping-updated').textContent =
        'Imported: ' + result.added + ' added, ' + result.updated + ' updated, ' +
        result.skipped + ' unchanged — not saved yet';
    } catch (err) {
      RPA.showError('ow-mapping-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      el('ow-map-file').value = '';
    }
  }

  // ---------------------------------------------------------------------------
  // Prepare
  // ---------------------------------------------------------------------------

  const FUNNEL_COLUMNS = [
    { label: 'Where the rows went', render: f => RPA.escapeHtml(f.label) },
    { label: 'Rows', render: f => RPA.fmtInt(f.count), numeric: true, value: f => f.count }
  ];

  /** Rows the fetch could not fill, so the operator knows exactly which ones need a hand. */
  const FETCH_PROBLEM_COLUMNS = [
    { label: 'Seller ID', render: p => RPA.escapeHtml(p.sellerId || '-'), numeric: true },
    { label: 'Seller', render: p => RPA.escapeHtml(p.sellerName || '-') },
    { label: 'Why', render: p => '<span class="badge amber">' + RPA.escapeHtml(p.reason || '') + '</span>' }
  ];

  const PROBLEM_COLUMNS = [
    { label: 'Seller ID', render: m => RPA.escapeHtml(m.sellerId || '-'), numeric: true },
    { label: 'Seller', render: m => RPA.escapeHtml(m.sellerName) },
    { label: 'E-mail', render: m => RPA.escapeHtml(m.email || '-') },
    { label: 'Attachment', render: m => RPA.escapeHtml(m.attachmentName || '-') },
    { label: 'Why', render: m => '<span class="badge amber">' + RPA.escapeHtml(m.problem || '') + '</span>' }
  ];

  function funnelRows(funnel) {
    return [
      { label: 'Rows in the mapping table', count: funnel.entriesInTable },
      { label: 'Ready to send', count: funnel.ready },
      { label: 'No e-mail address', count: funnel.noEmail },
      { label: 'An address does not look valid', count: funnel.invalidEmail },
      { label: 'Seller repeated on another row', count: funnel.duplicateSeller },
      { label: 'No attachment file name', count: funnel.noFileName },
      { label: 'Attachment not found in the folder', count: funnel.fileNotFound }
    ];
  }

  function renderWarnings(warnings) {
    const card = el('ow-warnings');
    if (!warnings || !warnings.length) {
      card.hidden = true;
      return;
    }
    card.hidden = false;
    el('ow-warnings-list').innerHTML = warnings
      .map(w => '<li>' + RPA.escapeHtml(w) + '</li>')
      .join('');
  }

  function mailCardHtml(mail, index) {
    const broken = !!mail.problem;
    const recipients = mail.recipients || [];

    // The seller is the label on the badge, because the seller is what identifies the mail — and it
    // is the seller/attachment pairing that has to be read to catch a wrong row. The addresses go
    // underneath in full: a count alone ("3 recipients") hides the one that should not be there.
    const head = broken
      ? '<span class="badge amber">' + RPA.escapeHtml(mail.problem) + '</span>'
      : '<label class="check"><input type="checkbox" class="ow-pick" data-index="' + index + '"' +
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

    return '<div class="msg-card">' +
      '<div class="msg-head">' +
        head +
        '<span class="msg-meta">✉ ' + to + ' · 📎 ' + attachment + '</span>' +
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

    el('ow-mails-summary').textContent = ready
      ? RPA.fmtInt(picked) + ' of ' + RPA.fmtInt(ready) + ' selected'
      : '';

    el('ow-send').disabled = running || picked === 0;
    el('ow-mails-export').disabled = lastMails.length === 0;
  }

  function renderMails() {
    el('ow-mails').innerHTML = lastMails.length
      ? lastMails.map(mailCardHtml).join('')
      : '<div class="empty-state">No mail to compose — the mapping table is empty.</div>';

    updateSelectionSummary();
  }

  function renderPrepared(data) {
    lastMails = data.mails || [];

    // Everything that can be sent starts ticked: the operator's job here is to spot the row that
    // should not go, not to tick 188 boxes to get the normal case.
    selected = new Set(lastMails.filter(m => !m.problem).map(sellerKey));

    RPA.setExportContext('Attachment folder ' + data.attachmentFolder + ' · ' + data.date);

    renderWarnings(data.warnings);

    const problems = lastMails.filter(m => m.problem);
    el('ow-problems').hidden = problems.length === 0;
    el('ow-problems-summary').textContent = problems.length
      ? RPA.fmtInt(problems.length) + ' row(s) in the table will not be mailed'
      : '';
    RPA.renderTable('ow-problems-wrap', problems, PROBLEM_COLUMNS, 'Every row in the table can be mailed.');

    RPA.renderTable('ow-funnel-wrap', funnelRows(data.funnel), FUNNEL_COLUMNS, 'Nothing to report.');
    RPA.syncExportButtons();

    renderMails();

    el('ow-folder-summary').textContent = RPA.fmtInt(data.filesInFolder) + ' file(s) in the folder';
    el('ow-prepared').hidden = false;
    RPA.stamp('ow-stamp');
  }

  async function prepare() {
    const button = el('ow-prepare');
    RPA.clearError('ow-prepare-alert');
    RPA.setBusy(button, true, 'Working…');
    RPA.showSkeleton('ow-prepare-skeleton', 'ow-prepared');
    try {
      renderPrepared(await RPA.sendJson('/api/offer-warnings/prepare', {
        subjectTemplate: el('ow-subject').value,
        bodyTemplate: el('ow-body').value
      }));
    } catch (err) {
      RPA.showError('ow-prepare-alert', err.message);
    } finally {
      RPA.hideSkeleton('ow-prepare-skeleton');
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Send
  // ---------------------------------------------------------------------------

  /**
   * The last checkpoint before something irreversible. It names the recipients rather than counting
   * them, because reading an address beside its attachment is the only way to notice a wrong row —
   * the full list is on the cards above, so a long run shows the first twelve and says so.
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

    const dryRun = el('ow-dry-run').checked;
    if (!confirmSend(mails, dryRun)) return;

    RPA.clearError('ow-mails-alert');

    // Opened before the POST so the first events of the run cannot be missed.
    connect();
    setRunning(true, MODULE);
    el('ow-run').hidden = false;

    try {
      await RPA.sendJson('/api/offer-warnings/send', {
        dryRun: dryRun,
        mails: mails.map(m => ({
          sellerId: m.sellerId,
          sellerName: m.sellerName,
          recipients: m.recipients,
          subject: m.subject,
          body: m.body,
          attachmentName: m.attachmentName
        }))
      });
    } catch (err) {
      RPA.showError('ow-mails-alert', err.message);
      setRunning(false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

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
    el('ow-prepare').addEventListener('click', prepare);
    el('ow-send').addEventListener('click', send);
    el('ow-check-folder').addEventListener('click', checkFolder);

    el('ow-check-outlook').addEventListener('click', async function () {
      const button = el('ow-check-outlook');
      RPA.clearError('ow-outlook-alert');
      RPA.setBusy(button, true, 'Checking…');
      try {
        const result = await sendJsonMethod('POST', '/api/offer-warnings/check-outlook', {});
        if (!result.available && result.error) RPA.showError('ow-outlook-alert', result.error);
      } catch (err) {
        RPA.showError('ow-outlook-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
        await refreshStatus();
      }
    });

    el('ow-map-save').addEventListener('click', saveMapping);
    el('ow-map-add').addEventListener('click', addMappingRow);
    el('ow-map-fetch').addEventListener('click', fetchEmails);

    el('ow-map-import').addEventListener('click', () => el('ow-map-file').click());
    el('ow-map-file').addEventListener('change', function () {
      if (this.files && this.files[0]) importMapping(this.files[0]);
    });

    el('ow-map-export').addEventListener('click', async function () {
      const button = el('ow-map-export');
      RPA.clearError('ow-mapping-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/offer-warnings/mapping/excel',
          { entries: collectMapping() }, 'satici-mail-eslesme.xlsx');
      } catch (err) {
        RPA.showError('ow-mapping-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    el('ow-mails-export').addEventListener('click', async function () {
      const button = el('ow-mails-export');
      RPA.clearError('ow-mails-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/offer-warnings/mails/excel',
          { mails: lastMails }, 'offer-warnings.xlsx');
      } catch (err) {
        RPA.showError('ow-mails-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    // Removing a row must not silently drop unsaved edits elsewhere, so the table is never
    // re-rendered on remove — the row is taken out in place.
    el('ow-mapping-body').addEventListener('click', function (event) {
      const button = event.target.closest('.map-remove');
      if (!button) return;
      button.closest('tr').remove();
      updateMappingCount();
    });

    // Keeps the count honest while the operator is still filling rows in.
    el('ow-mapping-body').addEventListener('input', updateMappingCount);

    el('ow-template-reset').addEventListener('click', function () {
      el('ow-subject').value = defaultSubject;
      el('ow-body').value = defaultBody;
    });

    el('ow-select-all').addEventListener('click', function () {
      selected = new Set(lastMails.filter(m => !m.problem).map(sellerKey));
      renderMails();
    });

    el('ow-select-none').addEventListener('click', function () {
      selected = new Set();
      renderMails();
    });

    // Delegated: the cards are rebuilt on every prepare, so per-row listeners would be lost.
    el('ow-mails').addEventListener('change', function (event) {
      const box = event.target.closest('.ow-pick');
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

    if (el('tab-offer-warnings').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
