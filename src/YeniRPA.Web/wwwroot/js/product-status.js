/* =============================================================================
   Product Status — Mirakl browser automation.

   Ported from the RPA project's Product Status Export. Session handling and the
   event stream are the same as mark-received.js, because both drive the same
   Mirakl browser and share its login.

   The one structural difference: this module produces data rather than clicks,
   so the run ends by fetching the finished table from /api/product-status/result
   instead of just printing "done". The table is held on the server, so it also
   comes back after a reload — the scrape is minutes of real browser pages and is
   not something to repeat because a tab was closed.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'product-status';

  let stream = null;       // EventSource, once the panel has been visited
  let activated = false;
  let total = 0;
  let lastResult = null;   // the result renderResult last drew — re-filtered when the checkbox changes

  function el(id) { return document.getElementById(id); }

  // ---------------------------------------------------------------------------
  // Seller-name parsing (client-side mirror of the server's parse — used only for
  // the live count readout, never trusted as the source of truth for what runs)
  // ---------------------------------------------------------------------------

  function parseSellers(text) {
    const seen = new Set();
    const names = [];
    (text || '').split('\n').forEach(function (line) {
      const trimmed = line.trim();
      if (!trimmed || trimmed[0] === '#') return;
      if (seen.has(trimmed)) return;
      seen.add(trimmed);
      names.push(trimmed);
    });
    return names;
  }

  // ---------------------------------------------------------------------------
  // Results
  // ---------------------------------------------------------------------------

  /** Builds the pivot's columns: the seller, then one per status label the scrape found. */
  function columnsFor(labels) {
    const columns = [{
      label: 'Seller',
      filter: 'text',
      value: r => r.sellerName,
      render: r => RPA.escapeHtml(r.sellerName)
    }];

    labels.forEach(function (label, index) {
      columns.push({
        label: RPA.escapeHtml(label),
        numeric: true,
        // The raw figure, so sorting compares numbers and Excel receives one.
        value: r => r.counts[index],
        render: r => RPA.fmtInt(r.counts[index])
      });
    });

    return columns;
  }

  // ---------------------------------------------------------------------------
  // Accounting
  //
  // A file of 765 rows can honestly produce a table of 472, because the submitted
  // list goes through five filters before the browser opens and nothing used to
  // report any of them. These helpers name what each one took, so the row count
  // can be reconciled with the spreadsheet it came from.
  // ---------------------------------------------------------------------------

  /** The parts of an intake worth naming: the skipped header, and anything that removed lines. */
  function intakeBreakdown(intake) {
    const parts = [];

    // Named even though it is always one row: the skip is unconditional, so a file with no header
    // loses a real seller here, and seeing a seller's name quoted back is the only way to catch it.
    if (intake.headerRows) {
      parts.push(intake.headerText
        ? 'header skipped: "' + intake.headerText + '"'
        : intake.headerRows + ' header row');
    }

    if (intake.blank) parts.push(intake.blank + ' blank');
    if (intake.comments) parts.push(intake.comments + ' starting with #');
    if (intake.duplicates) parts.push(intake.duplicates + ' duplicate(s) removed');

    return parts;
  }

  // Held rather than printed straight away. The acceptance comes back over the POST while 'started'
  // comes over the event stream, either can arrive first, and 'started' clears the console — so a
  // line printed on the wrong side of that would be wiped.
  let pendingIntake = null;

  /** The one report the file path gets: what the server made of the spreadsheet it parsed. */
  function logIntake(intake) {
    if (!intake) return;

    pendingIntake = intake;
    if (mine) flushIntake();
  }

  function flushIntake() {
    const intake = pendingIntake;
    if (!intake) return;
    pendingIntake = null;

    const source = intake.fileRows
      ? intake.fileRows + ' line(s) in file'
      : intake.pastedLines + ' line(s) pasted';
    const parts = intakeBreakdown(intake);

    appendLog(source +
      (parts.length ? ' · ' + parts.join(' · ') : '') +
      ' · ' + intake.sellers + ' seller(s) to read');
  }

  /** The same account, kept with the result so it survives a reload — the run log does not. */
  function renderIntakeNote(result) {
    const note = el('ps-intake-note');
    const intake = result.intake;

    if (!intake) {
      note.hidden = true;
      return;
    }

    let text = result.rows.length + ' of ' + intake.sellers + ' seller(s) read · ' +
      result.withoutProducts.length + ' had no products · ' +
      result.failed.length + ' could not be read.';

    const parts = intakeBreakdown(intake);
    if (parts.length) {
      text += ' From ' + (intake.fileRows + intake.pastedLines) + ' submitted line(s): ' +
        parts.join(', ') + '.';
    }

    // Worth spelling out: a column of broken VLOOKUPs arrives as "#N/A" and is dropped by the rule
    // that exists for comments, which looks like data loss unless you know what it is.
    if (intake.comments) {
      text += ' Lines starting with # are read as comments — a spreadsheet error cell such as ' +
        '#N/A is dropped the same way.';
    }

    note.hidden = false;
    note.textContent = text;
  }

  /** Rows for the table/export: all of them, or with the zero/not-found sellers left out. */
  function filteredRows(result, showEmpty) {
    if (showEmpty) return result.rows;
    const empty = new Set(result.withoutProducts);
    return result.rows.filter(r => !empty.has(r.sellerName));
  }

  // Re-run whenever the "Show sellers with no products" checkbox changes, so the table — and the
  // Excel export taken from whatever it currently shows — stay in sync with it.
  function renderTable() {
    if (!lastResult) return;
    const showEmpty = el('ps-show-empty').checked;

    RPA.renderDataTable(
      'ps-table',
      filteredRows(lastResult, showEmpty),
      columnsFor(lastResult.labels),
      'No seller returned any product statuses.');

    // No match in Mirakl and a real seller with an empty catalogue read the same way here, so both
    // land in this bucket. Named anyway, so a row of zeroes (or its absence) reads as "not found /
    // no products" instead of looking like a real, verified zero count.
    const skipped = el('ps-skipped-note');
    skipped.hidden = !lastResult.withoutProducts.length;
    skipped.textContent = lastResult.withoutProducts.length
      ? lastResult.withoutProducts.length + ' seller(s) have no products in Mirakl' +
        (showEmpty
          ? ' and show as 0 in the table: '
          : ' and are hidden from the table below (tick "Show sellers with no products" to include them): ') +
        lastResult.withoutProducts.join(', ')
      : '';
  }

  function renderResult(result) {
    if (!result) return;

    // A filter typed during an earlier run is held per table and would otherwise still be applied
    // here, quietly narrowing both this table and the Excel export taken from it.
    RPA.resetDataTables();

    // Older held results predate these fields; the notes below read them unconditionally.
    result.withoutProducts = result.withoutProducts || [];
    result.failed = result.failed || [];
    lastResult = result;

    el('ps-results').hidden = false;
    RPA.setExportContext('Product status read from Mirakl on ' +
      new Date(result.completedUtc).toLocaleString('en-US', {
        year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
      }));

    renderTable();
    renderIntakeNote(result);

    // Sellers that could not be read are named here rather than left as zero rows in the table —
    // "no products" and "could not be read" are different answers.
    const note = el('ps-failed-note');
    note.hidden = !result.failed.length;
    note.textContent = result.failed.length
      ? result.failed.length + ' seller(s) could not be read and are missing from the table: ' +
        result.failed.join(', ')
      : '';
  }

  /** 204 means nothing has run yet, which is not an error — the results card just stays hidden. */
  async function loadResult() {
    try {
      const response = await fetch('/api/product-status/result');
      if (response.status === 204 || !response.ok) return;
      renderResult(await response.json());
    } catch (e) { /* the table is a bonus here; the run log already said what happened */ }
  }

  // ---------------------------------------------------------------------------
  // Progress + console
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('ps-console');
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('ps-progress-fill').style.width = percent + '%';
    el('ps-progress').setAttribute('aria-valuenow', String(percent));
    el('ps-progress-text').textContent = completed + ' / ' + total;
  }

  /** Idempotent: the run state arrives from several places (POST, status, events). */
  function setRunning(running) {
    const button = el('ps-start');
    if (running !== button.classList.contains('is-busy')) {
      RPA.setBusy(button, running, 'Running…');
    }
    // Wiping the session out from under a running batch would fail every remaining seller.
    el('ps-clear-session').disabled = running;
  }

  // ---------------------------------------------------------------------------
  // Event stream
  // ---------------------------------------------------------------------------

  // AutomationJobBus is shared with the other automation modules, and its log/progress/done events
  // carry no module of their own, so without this latch another module's run would print here.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('ps-run').hidden = false;
        el('ps-console').textContent = '';
        el('ps-progress').classList.remove('is-done');
        setProgress(0);
        setRunning(true);
        flushIntake();     // after the clear, so the account survives it
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
        if (event.failed.length) appendLog('Failed sellers:\n  ' + event.failed.join('\n  '));
        el('ps-progress').classList.add('is-done');
        setRunning(false);
        el('ps-stamp').textContent = 'Last run ' + new Date().toLocaleString('en-US', {
          year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
        });
        loadResult();
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
    const badge = el('ps-session-badge');

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
    if (status.isRunning) el('ps-run').hidden = false;
  }

  /** Runs a session button's request with its own busy state, reporting failures in the alert. */
  async function runSessionAction(buttonId, busyLabel, url, onSuccess) {
    const button = el(buttonId);
    RPA.clearError('ps-alert');
    RPA.setBusy(button, true, busyLabel);
    try {
      await send(url);
      if (onSuccess) onSuccess();
    } catch (err) {
      RPA.showError('ps-alert', err.message);
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
    loadResult();
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('ps-drop', 'ps-file');

    function updateCount() {
      const file = el('ps-file').files[0];
      // The file is parsed on the server, so its rows cannot be counted here — the name is the
      // only honest readout for that path.
      const count = file ? 0 : parseSellers(el('ps-sellers-text').value).length;
      el('ps-seller-count').textContent = file
        ? 'Reading names from ' + file.name
        : (count ? count + ' seller(s)' : '');
      el('ps-start').disabled = !file && count === 0;
    }

    el('ps-file').addEventListener('change', function () {
      // Mutually exclusive with the textarea, matching the source feature.
      if (el('ps-file').files.length) el('ps-sellers-text').value = '';
      updateCount();
    });

    el('ps-show-empty').addEventListener('change', renderTable);

    el('ps-sellers-text').addEventListener('input', function () {
      if (el('ps-sellers-text').value.trim()) el('ps-file').value = '';
      updateCount();
    });

    el('ps-login').addEventListener('click', function () {
      // Chrome has to be launched before the window can appear, so this is not instant.
      runSessionAction('ps-login', 'Opening…', '/api/automation/login', function () {
        el('ps-save-session').disabled = false;
      });
    });

    el('ps-save-session').addEventListener('click', function () {
      runSessionAction('ps-save-session', 'Saving…', '/api/automation/save-session', function () {
        el('ps-save-session').disabled = true;
      });
    });

    el('ps-clear-session').addEventListener('click', function () {
      runSessionAction('ps-clear-session', 'Clearing…', '/api/automation/clear-session');
    });

    el('ps-start').addEventListener('click', async function () {
      const file = el('ps-file').files[0];
      const sellersText = el('ps-sellers-text').value.trim();

      if (!file && !sellersText) {
        RPA.showError('ps-alert', 'Upload a seller list or paste seller names.');
        return;
      }

      RPA.clearError('ps-alert');

      const form = new FormData();
      if (file) form.append('file', file);
      if (sellersText) form.append('sellers', sellersText);

      // Opened before the POST so the first events of the run cannot be missed.
      connect();
      setRunning(true);
      el('ps-run').hidden = false;

      try {
        // The acceptance carries the intake, and it is the only report the file path gets: the
        // spreadsheet is parsed on the server, so nothing here could have counted its rows.
        const accepted = await RPA.postJson('/api/product-status/start', form);
        logIntake(accepted && accepted.intake);
      } catch (err) {
        RPA.showError('ps-alert', err.message);
        setRunning(false);
      }
    });

    // app.js selects the initial module while running its own DOMContentLoaded handler, which is
    // registered before this one — so the first rpa:modulechange has already been dispatched by
    // the time the listener below exists. Check the tab directly instead of waiting for a repeat.
    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-product-status').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
