/* =============================================================================
   Stockout Warnings — out-of-stock products from the Partner Manager export,
   filtered by GMV and grouped by seller, ready to message.

   Same prepare -> messages -> send split as Late Order Warnings: prepare returns
   the parsed rows so editing the template re-posts a few KB instead of the whole
   export again. Sending drives WhatsApp Web in the same shared Chrome window;
   progress arrives over the shared /api/automation/events stream.

   Unlike Incident Warnings, this panel owns a full session + mapping editor of
   its own rather than pointing the operator at the Late Order Warnings tab — see
   StockoutWarningsController's class summary for why that is safe: the mapping
   table is shared data, but it is saved through ISellerGroupStore.SaveEntries,
   which can never touch another module's message templates.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'stockout-warnings';

  let activated = false;
  let stream = null;       // EventSource, once the panel has been visited
  let total = 0;
  let running = false;

  // The prepared payload, held here so a template edit can re-render without a re-upload.
  let lastData = null;
  let lastMessages = [];

  // What the server says "Reset to default" should restore.
  let defaultTemplate = '';
  let defaultProductLineTemplate = '';

  function el(id) { return document.getElementById(id); }

  /**
   * The { success, message, data } envelope every /api/stockout-warnings endpoint returns. Same
   * shape and same reason as the helper in incidents-report.js: RPA.sendJson only throws on a
   * non-2xx status and its error reader looks for `error`, never `message`.
   */
  async function soJson(method, url, payload) {
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

  /** Same envelope as soJson, but for a multipart upload rather than a JSON body. */
  async function soUpload(url, form) {
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
  // WhatsApp session
  // ---------------------------------------------------------------------------

  async function refreshStatus() {
    const badge = el('so-session-badge');

    let status;
    try {
      status = await soJson('GET', '/api/stockout-warnings/status');
    } catch (e) {
      badge.className = 'badge red';
      badge.textContent = 'Status unavailable';
      return;
    }

    el('so-profile-path').textContent = status.profilePath || '';

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

    const send = el('so-send');
    RPA.setBusy(send, running, 'Running…');
    send.disabled = running || lastMessages.length === 0;
    if (running && runningModule && runningModule !== MODULE) {
      send.title = 'Another automation run (' + runningModule + ') is using the browser.';
    } else {
      send.removeAttribute('title');
    }

    // Wiping the session out from under a running batch would fail every remaining group.
    el('so-clear-session').disabled = running;
    if (running) el('so-run').hidden = false;
  }

  /** Runs a session button's request with its own busy state, reporting failures in the alert. */
  async function runSessionAction(buttonId, busyLabel, url, onSuccess) {
    const button = el(buttonId);
    RPA.clearError('so-session-alert');
    RPA.setBusy(button, true, busyLabel);
    try {
      const result = await soJson('POST', url);
      if (onSuccess) onSuccess(result);
    } catch (err) {
      RPA.showError('so-session-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      await refreshStatus();
    }
  }

  // ---------------------------------------------------------------------------
  // Run log
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('so-console');
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('so-progress-fill').style.width = percent + '%';
    el('so-progress').setAttribute('aria-valuenow', String(percent));
    el('so-progress-text').textContent = completed + ' / ' + total;
  }

  // Only this module's events are rendered here — the bus is shared with every automation module.
  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('so-run').hidden = false;
        el('so-console').textContent = '';
        el('so-progress').classList.remove('is-done');
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
        el('so-progress').classList.add('is-done');
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
    const body = el('so-mapping-body');
    body.innerHTML = (entries || []).map(mappingRowHtml).join('');

    if (mappingFilter) mappingFilter.apply();
    else updateMappingCount();
  }

  /** Counts what would actually be saved, not what is on screen — a blank row is dropped by the
   * server (nothing to match a seller on). */
  function updateMappingCount() {
    const entries = collectMapping();
    const withGroup = entries.filter(e => e.groupName).length;
    const body = el('so-mapping-body');
    const shown = body.querySelectorAll('tr:not(.is-filtered-out)').length;
    const hidden = body.querySelectorAll('tr').length - shown;

    if (!entries.length) {
      el('so-mapping-count').textContent = 'No sellers mapped yet';
      return;
    }

    el('so-mapping-count').textContent =
      entries.length.toLocaleString('en-US') + ' seller(s) · ' +
      withGroup.toLocaleString('en-US') + ' with a group' +
      (hidden ? ' · ' + shown.toLocaleString('en-US') + ' shown' : '') +
      (hidden && !shown ? ' — no rows match' : '');
  }

  /** Reads the table back out. Blank rows are dropped; a seller with no group is kept. */
  function collectMapping() {
    return Array.from(el('so-mapping-body').querySelectorAll('tr')).map(function (row) {
      return {
        sellerId: row.querySelector('.map-id').value.trim(),
        sellerName: row.querySelector('.map-name').value.trim(),
        groupName: row.querySelector('.map-group').value.trim()
      };
    }).filter(e => e.sellerId || e.sellerName);
  }

  function renderMappingWarnings(warnings) {
    const box = el('so-mapping-warnings');
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
    // A blank row matches no search term, so it would be added and hidden in the same breath.
    if (mappingFilter) mappingFilter.clearSearch();

    el('so-mapping-body').insertAdjacentHTML('beforeend', mappingRowHtml({
      sellerId: sellerId || '',
      sellerName: sellerName || '',
      groupName: groupName || ''
    }));

    if (mappingFilter) mappingFilter.apply();
    else updateMappingCount();

    const rows = el('so-mapping-body').querySelectorAll('tr');
    const added = rows[rows.length - 1];
    added.scrollIntoView({ block: 'center', behavior: 'smooth' });
    added.querySelector('.map-group').focus();
  }

  async function loadMapping() {
    let data;
    try {
      data = await soJson('GET', '/api/stockout-warnings/mapping');
    } catch (err) {
      RPA.showError('so-mapping-alert', 'The mapping could not be loaded: ' + err.message);
      return;
    }

    renderMapping(data.entries);
    renderMappingWarnings(data.warnings);

    el('so-mapping-path').textContent = data.path || '';
    el('so-mapping-updated').textContent = data.updatedUtc ? 'Last saved ' + data.updatedUtc : 'Never saved';
  }

  async function saveMapping() {
    const button = el('so-map-save');
    RPA.clearError('so-mapping-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      const result = await soJson('PUT', '/api/stockout-warnings/mapping', { entries: collectMapping() });
      renderMappingWarnings(result.warnings);
      el('so-mapping-updated').textContent = 'Saved just now · ' + result.saved + ' entries';
    } catch (err) {
      RPA.showError('so-mapping-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  async function importMapping(file) {
    const button = el('so-map-import');
    RPA.clearError('so-mapping-alert');
    RPA.setBusy(button, true, 'Reading…');
    try {
      const form = new FormData();
      form.append('file', file);
      const result = await soUpload('/api/stockout-warnings/mapping/import', form);

      renderMapping(result.entries);
      el('so-mapping-updated').textContent =
        'Imported: ' + result.added + ' added, ' + result.updated + ' updated, ' +
        result.skipped + ' unchanged — not saved yet';
    } catch (err) {
      RPA.showError('so-mapping-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
      el('so-map-file').value = '';
    }
  }

  // ---------------------------------------------------------------------------
  // Prepare
  // ---------------------------------------------------------------------------

  const FUNNEL_COLUMNS = [
    { label: 'Where the rows went', render: f => RPA.escapeHtml(f.label) },
    { label: 'Rows', render: f => RPA.fmtInt(f.count), numeric: true, value: f => f.count }
  ];

  const PRODUCT_COLUMNS = [
    { label: 'Seller', render: r => RPA.escapeHtml(r.sellerName) },
    { label: 'GTIN', render: r => RPA.escapeHtml(r.gtin), numeric: true },
    { label: 'Product', render: r => RPA.escapeHtml(r.productName || '-') },
    { label: 'Brand', render: r => RPA.escapeHtml(r.brand || '-') },
    { label: 'GMV', render: r => RPA.fmtInt(r.gmv), numeric: true, value: r => r.gmv },
    { label: 'Sold items accepted', render: r => RPA.escapeHtml(r.soldItemsAccepted), numeric: true },
    {
      label: 'WhatsApp group',
      render: r => r.groupName
        ? RPA.escapeHtml(r.groupName)
        : '<span class="badge amber">' + RPA.escapeHtml(r.mappingProblem || 'unmapped') + '</span>'
    }
  ];

  const UNMAPPED_COLUMNS = [
    { label: 'Seller', render: s => RPA.escapeHtml(s.sellerName) },
    { label: 'Products', render: s => RPA.fmtInt(s.productCount), numeric: true, value: s => s.productCount },
    { label: 'Total GMV', render: s => RPA.fmtInt(s.totalGmv), numeric: true, value: s => s.totalGmv },
    { label: 'Why', render: s => '<span class="badge amber">' + RPA.escapeHtml(s.mappingProblem || '') + '</span>' },
    {
      label: '',
      render: s => '<button type="button" class="btn btn-ghost btn-sm so-add-map"' +
        ' data-seller-name="' + RPA.escapeHtml(s.sellerName) + '">Add to mapping</button>'
    }
  ];

  /** The funnel is a set of terminal buckets, so it reads as label/count rather than as survivors. */
  function funnelRows(funnel) {
    return [
      { label: 'Rows in the file', count: funnel.rowsInFile },
      { label: 'Missing GTIN', count: funnel.missingGtin },
      { label: 'Missing seller name', count: funnel.missingSeller },
      { label: 'GMV could not be read', count: funnel.unreadableGmv },
      { label: 'Below the GMV threshold', count: funnel.belowThreshold },
      { label: 'Eligible products', count: funnel.eligible },
      { label: 'Sellers with eligible products', count: funnel.sellers },
      { label: 'Sellers with a WhatsApp group', count: funnel.mappedSellers },
      { label: 'Sellers with no group', count: funnel.unmappedSellers }
    ];
  }

  /** Flattens sellers to one row per product for the products table. */
  function productRows(sellers) {
    const rows = [];
    sellers.forEach(function (seller) {
      seller.products.forEach(function (product) {
        rows.push(Object.assign({}, product, {
          sellerName: seller.sellerName,
          groupName: seller.groupName,
          mappingProblem: seller.mappingProblem
        }));
      });
    });
    return rows;
  }

  function renderWarnings(warnings) {
    const card = el('so-warnings');
    if (!warnings || !warnings.length) {
      card.hidden = true;
      return;
    }
    card.hidden = false;
    el('so-warnings-list').innerHTML = warnings
      .map(w => '<li>' + RPA.escapeHtml(w) + '</li>')
      .join('');
  }

  function renderPrepared(data) {
    lastData = data;

    RPA.setExportContext('Reference time ' + data.referenceTime + ' · GMV threshold ' + RPA.fmtInt(data.gmvThreshold));

    renderWarnings(data.warnings);

    const unmapped = data.sellers.filter(s => !s.groupName);
    el('so-unmapped').hidden = unmapped.length === 0;
    el('so-unmapped-summary').textContent = unmapped.length
      ? unmapped.length.toLocaleString('en-US') + ' seller(s) with eligible stockout products have no WhatsApp group'
      : '';
    RPA.renderTable('so-unmapped-wrap', unmapped, UNMAPPED_COLUMNS, 'Every seller with eligible products is mapped.');

    RPA.renderTable('so-funnel-wrap', funnelRows(data.funnel), FUNNEL_COLUMNS, 'Nothing to report.');
    RPA.renderTable('so-products-wrap', productRows(data.sellers), PRODUCT_COLUMNS,
      'No product is at or above this GMV threshold. Lower the threshold to widen the list.');

    RPA.syncExportButtons();

    el('so-prepared').hidden = false;
    RPA.stamp('so-stamp');

    return renderMessages();
  }

  async function prepare() {
    const file = el('so-file').files[0];
    if (!file) {
      RPA.showError('so-prepare-alert', 'Pick the Partner Manager stockout export first.');
      return;
    }
    RPA.clearError('so-prepare-alert');

    const form = new FormData();
    form.append('file', file);
    form.append('gmvThreshold', el('so-gmv-threshold').value || '0');

    const button = el('so-prepare');
    RPA.setBusy(button, true, 'Working…');
    RPA.showSkeleton('so-prepare-skeleton', 'so-prepared');
    try {
      await renderPrepared(await soUpload('/api/stockout-warnings/prepare', form));
    } catch (err) {
      RPA.showError('so-prepare-alert', err.message);
    } finally {
      RPA.hideSkeleton('so-prepare-skeleton');
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
          ' · ' + message.productCount + ' product(s)' +
          (message.accountCount > 1 ? ' · <span class="badge amber">' + message.accountCount + ' sellers merged</span>' : '') +
          (message.truncated ? ' · <span class="badge amber">truncated</span>' : '') +
          (message.overLimit ? ' · <span class="badge red">over the character limit</span>' : '') +
        '</span>' +
        '<button type="button" class="btn btn-ghost btn-sm so-copy" data-index="' + index + '">Copy</button>' +
      '</div>' +
      '<pre class="msg-body">' + RPA.escapeHtml(message.body) + '</pre>' +
    '</div>';
  }

  async function renderMessages() {
    if (!lastData) return;

    RPA.clearError('so-messages-alert');
    try {
      const result = await soJson('POST', '/api/stockout-warnings/messages', {
        sellers: lastData.sellers,
        referenceTime: lastData.referenceTime,
        template: el('so-template').value,
        productLineTemplate: el('so-line-template').value
      });

      lastMessages = result.messages || [];
      el('so-messages').innerHTML = lastMessages.length
        ? lastMessages.map(messageCardHtml).join('')
        : '<div class="empty-state">No message to compose — every eligible seller is unmapped, or nothing is above the GMV threshold.</div>';

      el('so-messages-summary').textContent = lastMessages.length
        ? lastMessages.length.toLocaleString('en-US') + ' message(s) ready'
        : '';
      el('so-messages-export').disabled = lastMessages.length === 0;
      el('so-send').disabled = running || lastMessages.length === 0;

      if (result.warnings && result.warnings.length) {
        RPA.showError('so-messages-alert', result.warnings.join(' '));
      }
    } catch (err) {
      RPA.showError('so-messages-alert', err.message);
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
  // Settings
  // ---------------------------------------------------------------------------

  async function loadSettings() {
    try {
      const settings = await soJson('GET', '/api/stockout-warnings/settings');

      defaultTemplate = settings.defaultTemplate;
      defaultProductLineTemplate = settings.defaultProductLineTemplate;

      el('so-template').value = settings.template;
      el('so-line-template').value = settings.productLineTemplate;
      el('so-gmv-threshold').value = settings.gmvThreshold;
      el('so-gmv-threshold').min = settings.minGmvThreshold;
      el('so-gmv-threshold').max = settings.maxGmvThreshold;
      el('so-placeholders').innerHTML = settings.placeholders
        .map(p => '<code>' + RPA.escapeHtml(p) + '</code>')
        .join(' ');
      el('so-settings-note').textContent = settings.updatedUtc ? 'Saved ' + settings.updatedUtc : '';
    } catch (err) {
      RPA.showError('so-prepare-alert', err.message);
    }
  }

  async function saveSettings() {
    const button = el('so-save-settings');
    RPA.clearError('so-settings-alert');
    RPA.setBusy(button, true, 'Saving…');
    try {
      await soJson('PUT', '/api/stockout-warnings/settings', {
        template: el('so-template').value,
        productLineTemplate: el('so-line-template').value,
        gmvThreshold: Number(el('so-gmv-threshold').value) || 0
      });
      await loadSettings();
    } catch (err) {
      RPA.showError('so-settings-alert', err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Send
  // ---------------------------------------------------------------------------

  /**
   * The last checkpoint before something irreversible. It names the destinations rather than just
   * counting them, because reading a group name is the only way to notice a wrong mapping — the
   * full list is on the cards above, so a long run shows the first twelve here and says so.
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
        Math.max(1, Math.round(names.length * 9 / 60)) + ' minute(s); no other automation can run during it.';

    return window.confirm(heading + '\n\n  ' + shown.join('\n  ') + tail + slotWarning);
  }

  async function send() {
    if (!lastMessages.length) return;

    const dryRun = el('so-dry-run').checked;
    if (!confirmSend(dryRun)) return;

    RPA.clearError('so-messages-alert');

    // Opened before the POST so the first events of the run cannot be missed.
    connect();
    setRunning(true, MODULE);
    el('so-run').hidden = false;

    try {
      await soJson('POST', '/api/stockout-warnings/send', {
        dryRun: dryRun,
        messages: lastMessages.map(m => ({
          groupName: m.groupName,
          sellerName: m.sellerName,
          body: m.body
        }))
      });
    } catch (err) {
      RPA.showError('so-messages-alert', err.message);
      setRunning(false);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  function activate() {
    if (activated) return;
    activated = true;
    loadMapping();
    loadSettings();
    connect();
    refreshStatus();
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('so-drop', 'so-file');

    el('so-prepare').addEventListener('click', prepare);
    el('so-render').addEventListener('click', renderMessages);
    el('so-save-settings').addEventListener('click', saveSettings);
    el('so-send').addEventListener('click', send);

    el('so-login').addEventListener('click', function () {
      // Chrome has to be launched before the window can appear, so this is not instant.
      runSessionAction('so-login', 'Opening…', '/api/stockout-warnings/login');
    });

    el('so-check-session').addEventListener('click', function () {
      runSessionAction('so-check-session', 'Checking…', '/api/stockout-warnings/check-session');
    });

    el('so-clear-session').addEventListener('click', function () {
      if (!window.confirm('Delete the saved WhatsApp profile? You will have to scan the QR code again.')) return;
      runSessionAction('so-clear-session', 'Clearing…', '/api/stockout-warnings/clear-session', function (result) {
        if (result) RPA.showError('so-session-alert', result);
      });
    });

    el('so-map-save').addEventListener('click', saveMapping);
    el('so-map-add').addEventListener('click', () => addMappingRow('', '', ''));

    mappingFilter = RPA.initRowFilter('so-mapping-body', {
      searchId: 'so-map-search',
      pendingId: 'so-map-pending',
      pendingSelector: '.map-group',
      onChange: updateMappingCount
    });

    el('so-map-import').addEventListener('click', () => el('so-map-file').click());
    el('so-map-file').addEventListener('change', function () {
      if (this.files && this.files[0]) importMapping(this.files[0]);
    });

    el('so-map-export').addEventListener('click', async function () {
      const button = el('so-map-export');
      RPA.clearError('so-mapping-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/stockout-warnings/mapping/excel',
          { entries: collectMapping() }, 'seller-groups.xlsx');
      } catch (err) {
        RPA.showError('so-mapping-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    // Removing a row must not silently drop unsaved edits elsewhere, so the table is never
    // re-rendered on remove — the row is taken out in place.
    el('so-mapping-body').addEventListener('click', function (event) {
      const button = event.target.closest('.map-remove');
      if (!button) return;
      button.closest('tr').remove();
      updateMappingCount();
    });

    el('so-mapping-body').addEventListener('input', updateMappingCount);

    el('so-template-reset').addEventListener('click', function () {
      el('so-template').value = defaultTemplate;
      el('so-line-template').value = defaultProductLineTemplate;
      renderMessages();
    });

    el('so-messages-export').addEventListener('click', async function () {
      const button = el('so-messages-export');
      RPA.clearError('so-messages-alert');
      RPA.setBusy(button, true, 'Building…');
      try {
        await RPA.postDownloadJson('/api/stockout-warnings/messages/excel',
          { messages: lastMessages }, 'stockout-warnings.xlsx');
      } catch (err) {
        RPA.showError('so-messages-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    // Delegated: both tables are re-rendered on every prepare, so per-row listeners would be lost.
    el('panel-stockout-warnings').addEventListener('click', async function (event) {
      const add = event.target.closest('.so-add-map');
      if (add) {
        addMappingRow('', add.dataset.sellerName, '');
        return;
      }

      const copy = event.target.closest('.so-copy');
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

    if (el('tab-stockout-warnings').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
