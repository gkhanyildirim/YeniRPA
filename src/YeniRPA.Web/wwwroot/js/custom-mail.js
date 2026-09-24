/* =============================================================================
   Custom Mail — a free-form subject/body the operator writes themselves, with
   one shared attachment, sent to whichever sellers they tick out of an
   uploaded seller list. Addresses are never read from the seller list itself:
   each seller is looked up in a second, separate directory upload, the same
   "export + directory" shape offer-warnings.js uses. A seller the directory
   does not cover can be given a hand-entered address instead, saved through
   /api/custom-mail/overrides and preferred over the directory from then on —
   the same "Hand-entered addresses" pattern offer-warnings.js uses.

   Unlike offer-warnings.js there is no per-seller template: the mail is
   identical for every recipient, so the card list's expanded body always
   shows the same Subject/Body text, just addressed to a different seller —
   still worth showing per recipient so the operator can see exactly what a
   given seller will get, and it reuses the same .msg-list/.msg-card CSS
   (already height-capped) instead of a table that would grow the page
   unbounded as the seller list grows.

   Progress arrives over the shared /api/automation/events stream, the same
   one every other Outlook/WhatsApp module reports on.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'custom-mail';

  let activated = false;
  let stream = null;       // EventSource, once the panel has been visited
  let total = 0;
  let running = false;

  // The merged, deduplicated recipient list from the last prepare, and the batch id every send has to
  // quote — a send against a batch the server no longer holds is refused rather than served stale.
  let lastRecipients = [];
  let batchId = '';

  // Which recipients are ticked, keyed by the server's own normalized-e-mail key. A Set rather than a
  // flag on each row, so a re-render (e.g. from the search filter) can never silently drop a decision.
  let selected = new Set();

  // Which recipient cards are open, by the same key. Re-rendering after a tick or a filter keystroke
  // must not shut a card the operator was in the middle of reading.
  let expanded = new Set();

  let search = '';

  let maxPerRun = 0;
  let perPass = 0;

  // The saved hand-entered addresses, as loaded. The unmatched-sellers table merges into this list.
  let overrides = [];

  // The search box over the overrides table, set up on DOMContentLoaded.
  let overridesFilter = null;

  // The last unmatched-seller list from prepare, so the "Save these addresses" flow can find each
  // row's own seller id/name back by its server-issued key rather than trusting the DOM alone.
  let lastUnmatched = [];

  const PASS_BREAK_SECONDS = 120;
  const SECONDS_PER_MAIL = 3.5;

  function el(id) { return document.getElementById(id); }

  /** How many passes a run of this size takes, mirroring OfferMailRunner.PlanPasses. */
  function passCount(count) {
    return perPass > 0 ? Math.ceil(count / perPass) : 1;
  }

  /** Roughly how long a live run of this size holds the automation slot, in whole minutes. */
  function runMinutes(count) {
    const seconds = count * SECONDS_PER_MAIL + (passCount(count) - 1) * PASS_BREAK_SECONDS;
    return Math.max(1, Math.round(seconds / 60));
  }

  /**
   * The { success, message, data } envelope every /api/custom-mail endpoint returns. Same shape and
   * same reason as stockout-warnings.js's soJson: RPA.sendJson only throws on a non-2xx status and its
   * error reader looks for `error`, never `message`.
   */
  async function cmJson(method, url, payload) {
    const init = { method: method };
    if (payload !== undefined) {
      init.headers = { 'Content-Type': 'application/json' };
      init.body = JSON.stringify(payload);
    }

    const response = await fetch(url, init);

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

  /** Same envelope as cmJson, but for a multipart upload rather than a JSON body. */
  async function cmUpload(url, form) {
    const response = await fetch(url, { method: 'POST', body: form });

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
  // Outlook + status
  // ---------------------------------------------------------------------------

  async function refreshStatus() {
    const badge = el('cm-outlook-badge');

    let status;
    try {
      status = await cmJson('GET', '/api/custom-mail/status');
    } catch (e) {
      badge.className = 'badge red';
      badge.textContent = 'Status unavailable';
      return;
    }

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

    maxPerRun = status.maxMailsPerRun || maxPerRun;
    perPass = status.mailsPerPass || perPass;

    setRunning(status.isRunning, status.runningModule, status.stopRequested);
  }

  /** Idempotent: the run state arrives from the POST, from /status and from the event stream. */
  function setRunning(isRunning, runningModule, stopRequested) {
    running = !!isRunning;

    const send = el('cm-send');
    const mineNow = running && runningModule === MODULE;

    RPA.setBusy(send, mineNow, 'Running…');
    send.disabled = running || selectedRecipients().length === 0;

    if (running && runningModule && runningModule !== MODULE) {
      send.title = 'Another automation run (' + runningModule + ') holds the slot.';
    } else {
      send.removeAttribute('title');
    }

    const stop = el('cm-stop');
    stop.hidden = !mineNow;
    stop.disabled = !mineNow || !!stopRequested;
    stop.querySelector('.btn-text').textContent = stopRequested ? 'Stopping…' : 'Stop';

    if (running) el('cm-run').hidden = false;
  }

  async function stopRun() {
    if (!window.confirm(
      'Stop this run?\n\nMails already sent cannot be recalled. The run stops before the next one ' +
      'and the log names how many were not attempted.')) return;

    const stop = el('cm-stop');
    stop.disabled = true;
    stop.querySelector('.btn-text').textContent = 'Stopping…';

    try {
      await RPA.sendJson('/api/automation/stop', {});
    } catch (err) {
      RPA.showError('cm-send-alert', 'The run could not be stopped: ' + err.message);
      stop.disabled = false;
      stop.querySelector('.btn-text').textContent = 'Stop';
    }
  }

  // ---------------------------------------------------------------------------
  // Run log
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('cm-console');
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('cm-progress-fill').style.width = percent + '%';
    el('cm-progress').setAttribute('aria-valuenow', String(percent));
    el('cm-progress-text').textContent = completed + ' / ' + total;
  }

  // Only this panel's runs are rendered here — the bus is shared by every automation module.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('cm-run').hidden = false;
        el('cm-console').textContent = '';
        el('cm-progress').classList.remove('is-done');
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
        el('cm-progress').classList.add('is-done');
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
  // Prepare
  // ---------------------------------------------------------------------------

  function renderWarnings(warnings) {
    const card = el('cm-warnings');
    if (!warnings || !warnings.length) {
      card.hidden = true;
      return;
    }
    card.hidden = false;
    el('cm-warnings-list').innerHTML = warnings.map(w => '<li>' + RPA.escapeHtml(w) + '</li>').join('');
  }

  /** The recipients the search box currently shows. Purely a view — the selection and the send both
   * keep reading the full list, so a filter can never quietly drop a recipient that was ticked. */
  function visibleRecipients() {
    const needle = RPA.fold(search);
    if (!needle) return lastRecipients;

    return lastRecipients.filter(function (r) {
      const haystack = [r.sellerName, r.sellerId, r.email].filter(Boolean).join(' ');
      return RPA.fold(haystack).indexOf(needle) >= 0;
    });
  }

  /** What the expanded body of one recipient's mail card shows — the exact mail this seller receives.
   * Built from the wording boxes' current content, not from anything captured at prepare time, so an
   * edit to the subject/body is reflected the moment a card is opened. The body box is rich text
   * (HTML), so its formatting is rendered here rather than escaped — a bold word in the box has to
   * show up bold in the preview, or the preview would be showing something other than the real mail. */
  function mailPreviewHtml(r) {
    const subject = el('cm-subject').value;
    const bodyHtml = el('cm-body').innerHTML;
    return '<div class="msg-preview-head">To: ' + RPA.escapeHtml(r.sellerName || r.email) +
      ' &lt;' + RPA.escapeHtml(r.email) + '&gt;<br>Subject: ' + RPA.escapeHtml(subject) + '</div>' +
      '<hr>' + bodyHtml;
  }

  function recipientCardHtml(r) {
    const key = r.key;
    const open = expanded.has(key);
    const checked = selected.has(key) ? ' checked' : '';
    const bodyId = 'cm-body-' + RPA.fold(key).replace(/[^a-z0-9]+/g, '-');

    const source = r.matchedBy === 'override' ? ' · ✎ entered by hand' : '';

    return '<div class="msg-card' + (open ? '' : ' is-collapsed') + '" data-key="' + RPA.escapeHtml(key) + '">' +
      '<div class="msg-head">' +
        '<label class="check"><input type="checkbox" class="cm-pick" data-key="' + RPA.escapeHtml(key) + '"' +
          checked + ' aria-label="Select ' + RPA.escapeHtml(r.email) + '" />' +
          '<span class="badge green">' + RPA.escapeHtml(r.sellerName || r.email) + '</span></label>' +
        '<span class="msg-meta">' + RPA.escapeHtml(r.sellerId || '-') + ' · ' + RPA.escapeHtml(r.email) + source + '</span>' +
        '<button type="button" class="btn btn-ghost btn-sm cm-toggle" aria-controls="' + bodyId + '"' +
          ' aria-expanded="' + (open ? 'true' : 'false') + '">' +
          '<span class="spinner" aria-hidden="true"></span>' +
          '<span class="btn-text">' + (open ? 'Hide mail' : 'Show mail') + '</span>' +
        '</button>' +
      '</div>' +
      '<div class="msg-body is-rich" id="' + bodyId + '">' + mailPreviewHtml(r) + '</div>' +
    '</div>';
  }

  /** Rewrites only the currently-open cards' preview in place — called on every keystroke in the
   * wording boxes. A full re-render on every keystroke would rebuild several hundred DOM nodes per
   * key press and would reset the operator's scroll position; this touches only what is visible. */
  function refreshOpenPreviews() {
    expanded.forEach(function (key) {
      const preview = document.getElementById('cm-body-' + RPA.fold(key).replace(/[^a-z0-9]+/g, '-'));
      if (!preview) return;
      const r = lastRecipients.find(x => x.key === key);
      if (r) preview.innerHTML = mailPreviewHtml(r);
    });
  }

  function unmatchedRowHtml(row) {
    return '<tr data-key="' + RPA.escapeHtml(row.sellerKey) + '">' +
      '<td class="num">' + RPA.escapeHtml(row.sellerId || '-') + '</td>' +
      '<td>' + RPA.escapeHtml(row.sellerName || '-') + '</td>' +
      '<td><input type="text" class="cm-um-email" value="" spellcheck="false" aria-label="E-mail for ' +
        RPA.escapeHtml(row.sellerName || row.sellerId) + '" /></td>' +
      '<td><span class="badge amber">' + RPA.escapeHtml(row.reason || '') + '</span></td>' +
      '</tr>';
  }

  function renderUnmatched(unmatched) {
    lastUnmatched = unmatched || [];
    el('cm-unmatched').hidden = lastUnmatched.length === 0;
    if (!lastUnmatched.length) return;

    el('cm-unmatched-body').innerHTML = lastUnmatched.map(unmatchedRowHtml).join('');
    el('cm-unmatched-summary').textContent =
      RPA.fmtInt(lastUnmatched.length) + ' seller(s) have no address in the directory.';
  }

  /**
   * Appends what was typed on the unmatched table to the saved overrides and writes the whole list —
   * mirrors offer-warnings.js's saveUnmatched() exactly, including why it appends rather than merges:
   * deduplicating here would mean recomputing the server's seller key in JavaScript, and a fold that
   * disagreed by one character would add a second row for a seller instead of replacing the first.
   */
  async function saveUnmatched() {
    const button = el('cm-unmatched-save');
    RPA.clearError('cm-unmatched-alert');

    const typed = Array.from(el('cm-unmatched-body').querySelectorAll('tr'))
      .map(function (row) {
        const source = lastUnmatched.find(u => u.sellerKey === row.dataset.key);
        return {
          sellerId: source ? source.sellerId : '',
          sellerName: source ? source.sellerName : '',
          email: row.querySelector('.cm-um-email').value.trim()
        };
      })
      .filter(e => e.email);

    if (!typed.length) {
      RPA.showError('cm-unmatched-alert', 'Nothing to save — no address has been entered above.');
      return;
    }

    const merged = collectOverrides().concat(typed);

    RPA.setBusy(button, true, 'Saving…');
    try {
      await putOverrides(merged);
      el('cm-unmatched-summary').textContent =
        RPA.fmtInt(typed.length) + ' address(es) saved — click "Read the lists" again to pick them up';
    } catch (err) {
      RPA.showError('cm-unmatched-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Hand-entered addresses
  // ---------------------------------------------------------------------------

  function overrideRowHtml(entry) {
    return '<tr>' +
      '<td><input type="text" class="cm-ov-id" value="' + RPA.escapeHtml(entry.sellerId || '') + '" aria-label="Seller ID" /></td>' +
      '<td><input type="text" class="cm-ov-name" value="' + RPA.escapeHtml(entry.sellerName || '') + '" aria-label="Seller name" /></td>' +
      '<td><input type="text" class="cm-ov-email" value="' + RPA.escapeHtml(entry.email || '') + '" aria-label="E-mail" /></td>' +
      '<td class="num"><button type="button" class="btn btn-ghost btn-sm cm-ov-remove" aria-label="Remove row">Remove</button></td>' +
      '</tr>';
  }

  function renderOverrides() {
    el('cm-overrides-body').innerHTML = overrides.map(overrideRowHtml).join('');

    if (overridesFilter) overridesFilter.apply();
    else updateOverridesCount();
  }

  /** Reads the table back out. Rows with no seller are dropped; a half-filled row is kept. */
  function collectOverrides() {
    return Array.from(el('cm-overrides-body').querySelectorAll('tr')).map(function (row) {
      return {
        sellerId: row.querySelector('.cm-ov-id').value.trim(),
        sellerName: row.querySelector('.cm-ov-name').value.trim(),
        email: row.querySelector('.cm-ov-email').value.trim()
      };
    }).filter(e => e.sellerId || e.sellerName);
  }

  function updateOverridesCount() {
    const entries = collectOverrides();
    const withAddress = entries.filter(e => e.email).length;
    const body = el('cm-overrides-body');
    const shown = body.querySelectorAll('tr:not(.is-filtered-out)').length;
    const hidden = body.querySelectorAll('tr').length - shown;

    if (!entries.length) {
      el('cm-overrides-count').textContent = 'No addresses entered by hand yet';
      return;
    }

    el('cm-overrides-count').textContent =
      RPA.fmtInt(entries.length) + ' seller(s) · ' + RPA.fmtInt(withAddress) + ' with an address' +
      (hidden ? ' · ' + RPA.fmtInt(shown) + ' shown' : '') +
      (hidden && !shown ? ' — no rows match' : '');
  }

  function renderOverrideWarnings(warnings) {
    const box = el('cm-overrides-warnings');
    if (!warnings || !warnings.length) {
      box.hidden = true;
      box.innerHTML = '';
      return;
    }
    box.hidden = false;
    box.innerHTML = warnings.map(w => '<span class="badge amber">' + RPA.escapeHtml(w) + '</span>').join(' ');
  }

  async function loadOverrides() {
    let data;
    try {
      data = await cmJson('GET', '/api/custom-mail/overrides');
    } catch (err) {
      RPA.showError('cm-overrides-alert', 'The addresses could not be loaded: ' + err.message);
      return;
    }

    overrides = data.overrides || [];
    renderOverrides();
    renderOverrideWarnings(data.warnings);
    el('cm-overrides-path').textContent = data.path || '';
    el('cm-overrides-updated').textContent = data.updatedUtc ? 'Last saved ' + data.updatedUtc : 'Never saved';
  }

  async function putOverrides(entries) {
    const result = await cmJson('PUT', '/api/custom-mail/overrides', { overrides: entries });

    overrides = result.overrides || [];
    renderOverrides();
    renderOverrideWarnings(result.warnings);
    el('cm-overrides-updated').textContent = 'Saved just now · ' + result.saved + ' hand-entered address(es)';
    return result;
  }

  async function saveOverrides() {
    const button = el('cm-overrides-save');
    RPA.clearError('cm-overrides-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      await putOverrides(collectOverrides());
    } catch (err) {
      RPA.showError('cm-overrides-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  function addOverrideRow() {
    if (overridesFilter) overridesFilter.clearSearch();

    el('cm-overrides-body').insertAdjacentHTML('beforeend', overrideRowHtml({}));

    if (overridesFilter) overridesFilter.apply();
    else updateOverridesCount();

    const rows = el('cm-overrides-body').querySelectorAll('tr');
    const added = rows[rows.length - 1];
    added.scrollIntoView({ block: 'center', behavior: 'smooth' });
    added.querySelector('.cm-ov-name').focus();
  }

  function selectedRecipients() {
    return lastRecipients.filter(r => selected.has(r.key));
  }

  function updateSelectionSummary() {
    const picked = selectedRecipients().length;

    const shownKeys = new Set(visibleRecipients().map(r => r.key));
    const hiddenPicked = selectedRecipients().filter(r => !shownKeys.has(r.key)).length;

    let tail = '';
    if (maxPerRun && picked > maxPerRun) {
      tail = ' · ' + RPA.fmtInt(picked - maxPerRun) + ' over the ' + RPA.fmtInt(maxPerRun) + '-mail ceiling';
    } else if (perPass && picked > perPass) {
      tail = ' · ' + passCount(picked) + ' passes of ' + RPA.fmtInt(perPass) + ' · about ' +
        runMinutes(picked) + ' minute(s)';
    }

    el('cm-recipients-summary').textContent = lastRecipients.length
      ? RPA.fmtInt(picked) + ' of ' + RPA.fmtInt(lastRecipients.length) + ' selected' +
        (hiddenPicked ? ' · ' + RPA.fmtInt(hiddenPicked) + ' of them hidden by the filter' : '')
        + tail
      : '';

    el('cm-send').disabled = running || picked === 0;
  }

  function renderRecipients() {
    const shown = visibleRecipients();

    el('cm-recipients').innerHTML = shown.length
      ? shown.map(recipientCardHtml).join('')
      : '';

    el('cm-recipients-empty').hidden = lastRecipients.length !== 0;
    el('cm-recipients').hidden = lastRecipients.length === 0;

    el('cm-shown-summary').textContent = lastRecipients.length
      ? RPA.fmtInt(shown.length) + ' of ' + RPA.fmtInt(lastRecipients.length) + ' shown'
      : '';

    updateSelectionSummary();
  }

  function renderPrepared(data) {
    lastRecipients = data.recipients || [];
    batchId = data.batchId || '';

    // Everything starts ticked: the operator's job is to spot the address that should not go, not to
    // tick several hundred boxes to get the normal case.
    selected = new Set(lastRecipients.map(r => r.key));

    // A fresh run is a fresh list: last run's open cards would otherwise show a different run's
    // recipient behind a key that happens to collide.
    expanded = new Set();

    search = '';
    el('cm-search').value = '';

    renderWarnings(data.warnings);
    renderUnmatched(data.unmatched);
    renderRecipients();

    el('cm-merge-summary').textContent =
      RPA.fmtInt(data.sellersInFile) + ' seller(s) in the list · ' +
      RPA.fmtInt(lastRecipients.length) + ' resolved to an address · ' +
      RPA.fmtInt((data.unmatched || []).length) + ' with no address · ' +
      RPA.fmtInt(data.directoryRows) + ' row(s) in the directory';

    el('cm-prepared').hidden = false;
    RPA.stamp('cm-stamp');
  }

  async function prepare() {
    const sellers = el('cm-sellers-file').files[0];
    const directory = el('cm-directory-file').files[0];

    RPA.clearError('cm-prepare-alert');

    if (!sellers) {
      RPA.showError('cm-prepare-alert', 'Choose the seller list first.');
      return;
    }
    if (!directory) {
      RPA.showError('cm-prepare-alert', 'Choose the seller address directory as well — without it no seller has an address.');
      return;
    }

    const button = el('cm-prepare');
    RPA.setBusy(button, true, 'Reading…');
    RPA.showSkeleton('cm-prepare-skeleton', 'cm-prepared');
    try {
      const form = new FormData();
      form.append('sellers', sellers);
      form.append('directory', directory);

      renderPrepared(await cmUpload('/api/custom-mail/prepare', form));
    } catch (err) {
      RPA.showError('cm-prepare-alert', err.message);
    } finally {
      RPA.hideSkeleton('cm-prepare-skeleton');
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Send
  // ---------------------------------------------------------------------------

  /**
   * The last checkpoint before something irreversible. It names the recipients rather than counting
   * them, because reading an address is the only way to notice a wrong one.
   */
  function confirmSend(recipients, dryRun) {
    const shown = recipients.slice(0, 12).map(r => (r.sellerName || r.email) + '  →  ' + r.email);
    const rest = recipients.length - shown.length;

    const heading = dryRun
      ? 'DRY RUN — compose ' + recipients.length + ' mail(s) into Outlook Drafts, sending nothing?'
      : 'SEND ' + recipients.length + ' mail(s) for real? A sent mail cannot be recalled.';

    const tail = rest > 0
      ? '\n  …and ' + rest + ' more (all of them are ticked on the list above)'
      : '';

    const passes = passCount(recipients.length);
    const paced = passes > 1
      ? '\n\nThis goes out in ' + passes + ' passes of ' + RPA.fmtInt(perPass) +
        ', pausing between them. Stop is on the run log.'
      : '';

    const slotWarning = dryRun
      ? ''
      : '\n\nThis holds the automation slot for roughly ' + runMinutes(recipients.length) + ' minute(s).';

    const cc = el('cm-cc').value.trim();
    const copy = cc ? '\n\nEvery one of them also copies ' + cc + ', visibly.' : '';

    const bcc = el('cm-bcc').value.trim();
    const blindCopy = bcc ? '\n\nEvery one of them is also blind-copied to ' + bcc + ' (not visible to the recipients).' : '';

    const signature = el('cm-signature').checked ? '\nYour Outlook signature goes under each one.' : '';

    return window.confirm(heading + copy + blindCopy + signature + '\n\n  ' + shown.join('\n  ') + tail + paced + slotWarning);
  }

  async function send() {
    const recipients = selectedRecipients();
    if (!recipients.length) return;

    RPA.clearError('cm-send-alert');

    const subject = el('cm-subject').value.trim();
    if (!subject) {
      RPA.showError('cm-send-alert', 'Write a subject first.');
      return;
    }

    const bodyHtml = el('cm-body').innerHTML;
    if (!el('cm-body').textContent.trim()) {
      RPA.showError('cm-send-alert', 'Write a message body first.');
      return;
    }

    const attachment = el('cm-attachment-file').files[0];
    if (!attachment) {
      RPA.showError('cm-send-alert', 'Attach a file first — every recipient gets the same one.');
      return;
    }

    const dryRun = el('cm-dry-run').checked;
    if (!confirmSend(recipients, dryRun)) return;

    // Opened before the POST so the first events of the run cannot be missed.
    connect();
    setRunning(true, MODULE);
    el('cm-run').hidden = false;

    const button = el('cm-send');
    RPA.setBusy(button, true, 'Starting…');
    try {
      const form = new FormData();
      form.append('attachment', attachment);
      form.append('batchId', batchId);
      form.append('subject', subject);
      form.append('body', bodyHtml);
      form.append('cc', el('cm-cc').value.trim());
      form.append('bcc', el('cm-bcc').value.trim());
      form.append('recipientEmailsJson', JSON.stringify(recipients.map(r => r.key)));
      form.append('dryRun', dryRun);
      form.append('includeSignature', el('cm-signature').checked);

      await cmUpload('/api/custom-mail/send', form);
    } catch (err) {
      RPA.showError('cm-send-alert', err.message);
      setRunning(false);
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
    loadOverrides();
    connect();
    refreshStatus();
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('cm-sellers-drop', 'cm-sellers-file');
    RPA.initDropzone('cm-directory-drop', 'cm-directory-file');
    RPA.initDropzone('cm-attachment-drop', 'cm-attachment-file');

    el('cm-prepare').addEventListener('click', prepare);
    el('cm-send').addEventListener('click', send);
    el('cm-stop').addEventListener('click', stopRun);

    el('cm-check-outlook').addEventListener('click', async function () {
      const button = el('cm-check-outlook');
      RPA.clearError('cm-outlook-alert');
      RPA.setBusy(button, true, 'Checking…');
      try {
        const result = await cmJson('POST', '/api/custom-mail/check-outlook');
        if (!result.available && result.error) RPA.showError('cm-outlook-alert', result.error);
      } catch (err) {
        RPA.showError('cm-outlook-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
        await refreshStatus();
      }
    });

    // ---------------------------------------------------------------------
    // Hand-entered addresses
    // ---------------------------------------------------------------------

    el('cm-overrides-save').addEventListener('click', saveOverrides);
    el('cm-override-add').addEventListener('click', addOverrideRow);
    el('cm-unmatched-save').addEventListener('click', saveUnmatched);

    overridesFilter = RPA.initRowFilter('cm-overrides-body', {
      searchId: 'cm-override-search',
      pendingId: 'cm-override-pending',
      pendingSelector: '.cm-ov-email',
      onChange: updateOverridesCount
    });

    // Removing a row must not silently drop unsaved edits elsewhere, so the table is never
    // re-rendered on remove — the row is taken out in place.
    el('cm-overrides-body').addEventListener('click', function (event) {
      const button = event.target.closest('.cm-ov-remove');
      if (!button) return;
      button.closest('tr').remove();
      updateOverridesCount();
    });

    el('cm-overrides-body').addEventListener('input', updateOverridesCount);

    // ---------------------------------------------------------------------
    // Recipients
    // ---------------------------------------------------------------------

    el('cm-search').addEventListener('input', function () {
      search = this.value;
      renderRecipients();
    });

    el('cm-select-all').addEventListener('click', function () {
      visibleRecipients().forEach(r => selected.add(r.key));
      renderRecipients();
    });

    el('cm-select-none').addEventListener('click', function () {
      selected = new Set();
      renderRecipients();
    });

    el('cm-expand-all').addEventListener('click', function () {
      visibleRecipients().forEach(r => expanded.add(r.key));
      renderRecipients();
    });

    el('cm-collapse-all').addEventListener('click', function () {
      expanded = new Set();
      renderRecipients();
    });

    // Delegated: the card list is rebuilt on every prepare and every filter keystroke, so per-card
    // listeners would be lost.
    el('cm-recipients').addEventListener('change', function (event) {
      const box = event.target.closest('.cm-pick');
      if (!box) return;

      if (box.checked) selected.add(box.dataset.key);
      else selected.delete(box.dataset.key);

      updateSelectionSummary();
    });

    // Opening a card touches that card only. Re-rendering the list here would jump the scroll
    // position back to the top, which in a several-hundred-row list loses the operator's place.
    el('cm-recipients').addEventListener('click', function (event) {
      const button = event.target.closest('.cm-toggle');
      if (!button) return;

      const card = button.closest('.msg-card');
      if (!card) return;

      const open = card.classList.toggle('is-collapsed') === false;

      if (open) expanded.add(card.dataset.key);
      else expanded.delete(card.dataset.key);

      button.setAttribute('aria-expanded', open ? 'true' : 'false');
      button.querySelector('.btn-text').textContent = open ? 'Hide mail' : 'Show mail';

      // The preview was built from whatever the wording boxes held at the last render; refresh it now
      // in case the operator edited them while this card was collapsed.
      if (open) refreshOpenPreviews();
    });

    // Keeps every open card's preview in step with the wording boxes without a full re-render.
    ['cm-subject', 'cm-body'].forEach(function (id) {
      el(id).addEventListener('input', refreshOpenPreviews);
    });

    // Bold/italic/underline on the rich body box. execCommand is deprecated but remains the pragmatic
    // choice for three buttons in an app that otherwise ships no rich-text editor library; mousedown
    // is where focus/selection would be lost to the button, so the actual command runs on click,
    // after the browser has already decided mousedown won't move focus off the body box.
    [['cm-bold', 'bold'], ['cm-italic', 'italic'], ['cm-underline', 'underline']].forEach(function (pair) {
      const button = el(pair[0]);
      const command = pair[1];
      button.addEventListener('mousedown', function (event) { event.preventDefault(); });
      button.addEventListener('click', function () {
        document.execCommand(command, false, null);
        refreshOpenPreviews();
      });
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by the
    // time the listener below exists. Check the tab directly instead of waiting for a repeat.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-custom-mail').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
