/* =============================================================================
   Kargo Takip — 17Track delivery check.

   Upload a Mirakl orders export -> the server filters it down to the Kolay
   Gelsin / Sürat Kargo / PTT Kargo lines (prepare) -> the operator starts the
   scrape (start) -> progress streams over the shared automation event bus,
   same as Product Status -> the delivered rows are fetched from
   /api/track17/result once the run reports done.

   Two-step upload rather than one POST (unlike Product Status): the file is
   parsed and filtered as soon as it is chosen, so the operator sees the
   carrier breakdown and batch count before committing to a run that can take
   several minutes.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'track17';

  let stream = null;
  let activated = false;
  let total = 0;
  let batchId = null;

  function el(id) { return document.getElementById(id); }

  // ---------------------------------------------------------------------------
  // Prepare
  // ---------------------------------------------------------------------------

  function renderSummary(summary) {
    batchId = summary.batchId;

    const parts = Object.keys(summary.matchedByCarrier).map(function (carrier) {
      return carrier + ': ' + RPA.fmtInt(summary.matchedByCarrier[carrier]);
    });

    el('t17-summary-carriers').textContent = parts.join(' · ');
    el('t17-summary-detail').textContent =
      RPA.fmtInt(summary.matchedRows.length) + ' order line(s) · ' +
      RPA.fmtInt(summary.distinctTrackingNumbers) + ' distinct tracking number(s) · ' +
      summary.batchCount + ' batch(es) of up to 40' +
      (summary.skippedMalformedTracking
        ? ' · ' + RPA.fmtInt(summary.skippedMalformedTracking) + ' skipped (no usable tracking number)'
        : '');

    el('t17-summary').hidden = false;
    el('t17-start').disabled = false;
  }

  // ---------------------------------------------------------------------------
  // Results
  // ---------------------------------------------------------------------------

  function columns() {
    return [
      { label: 'Sipariş no', filter: 'text', value: r => r.orderNumber, render: r => RPA.escapeHtml(r.orderNumber) },
      { label: 'Tracking no', filter: 'text', value: r => r.trackingNumber, render: r => RPA.escapeHtml(r.trackingNumber) },
      { label: 'Kargo firması', filter: 'select', value: r => r.carrier, render: r => RPA.escapeHtml(r.carrier) },
      { label: 'Durum', value: r => r.lastEventText, render: r => RPA.escapeHtml(r.lastEventText || '') },
      { label: 'Teslim tarihi', value: r => r.deliveredOn || '', render: r => RPA.escapeHtml(r.deliveredOn || '') },
    ];
  }

  function renderResult(result) {
    if (!result) return;

    RPA.resetDataTables();
    el('t17-results').hidden = false;
    RPA.setExportContext('17Track sorgusu ' +
      new Date(result.completedUtc).toLocaleString('tr-TR', {
        year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
      }));

    RPA.renderDataTable(
      't17-table',
      result.delivered,
      columns(),
      'Bu çalışmada teslim edilmiş sipariş bulunamadı.');

    const note = el('t17-run-note');
    const failedText = result.failedBatches.length
      ? ' · ' + result.failedBatches.length + ' batch başarısız oldu'
      : '';
    note.textContent = RPA.fmtInt(result.trackingNumbersChecked) + ' tracking numarası sorgulandı · ' +
      RPA.fmtInt(result.delivered.length) + ' teslim edilmiş sipariş satırı bulundu' + failedText;
  }

  async function loadResult() {
    try {
      const response = await fetch('/api/track17/result');
      if (!response.ok) return;
      const body = await response.json();
      if (body && body.data) renderResult(body.data);
    } catch (e) { /* the table is a bonus here; the run log already said what happened */ }
  }

  // ---------------------------------------------------------------------------
  // Progress + console
  // ---------------------------------------------------------------------------

  function appendLog(message) {
    const box = el('t17-console');
    const pinned = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
    box.textContent += message + '\n';
    if (pinned) box.scrollTop = box.scrollHeight;
  }

  function setProgress(completed) {
    const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
    el('t17-progress-fill').style.width = percent + '%';
    el('t17-progress').setAttribute('aria-valuenow', String(percent));
    el('t17-progress-text').textContent = completed + ' / ' + total;
  }

  function setRunning(running) {
    const button = el('t17-start');
    if (running !== button.classList.contains('is-busy')) {
      RPA.setBusy(button, running, 'Çalışıyor…');
    }
  }

  // ---------------------------------------------------------------------------
  // Event stream — shared with every other automation module.
  // ---------------------------------------------------------------------------

  let mine = false;

  function handleEvent(event) {
    switch (event.type) {
      case 'started':
        mine = event.module === MODULE;
        if (!mine) return;
        total = event.total;
        el('t17-run').hidden = false;
        el('t17-console').textContent = '';
        el('t17-progress').classList.remove('is-done');
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
        appendLog('Tamamlandı. Sorgulanan: ' + event.processed + ' · Başarısız: ' + event.failed.length);
        el('t17-progress').classList.add('is-done');
        setRunning(false);
        el('t17-stamp').textContent = 'Son çalışma ' + new Date().toLocaleString('tr-TR', {
          year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
        });
        loadResult();
        break;
    }
  }

  function connect() {
    if (stream) return;

    stream = new EventSource('/api/automation/events');
    stream.addEventListener('message', function (message) {
      let payload;
      try { payload = JSON.parse(message.data); } catch (e) { return; }
      handleEvent(payload);
    });
    stream.addEventListener('error', function () { });
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  function activate() {
    if (activated) return;
    activated = true;
    connect();
    loadResult();

    fetch('/api/automation/status').then(function (r) { return r.json(); }).then(function (status) {
      if (status && status.isRunning && status.runningModule === MODULE) {
        setRunning(true);
        el('t17-run').hidden = false;
      }
    }).catch(function () { });
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('t17-drop', 't17-file');

    el('t17-file').addEventListener('change', async function () {
      const file = el('t17-file').files[0];
      el('t17-summary').hidden = true;
      el('t17-start').disabled = true;
      batchId = null;
      if (!file) return;

      RPA.clearError('t17-alert');
      const button = el('t17-prepare-status');
      button.hidden = false;
      button.textContent = 'Dosya okunuyor…';

      const form = new FormData();
      form.append('file', file);

      try {
        const body = await RPA.postJson('/api/track17/prepare', form);
        renderSummary(body.data);
        button.hidden = true;
      } catch (err) {
        button.hidden = true;
        RPA.showError('t17-alert', err.message);
      }
    });

    el('t17-start').addEventListener('click', async function () {
      if (!batchId) return;
      RPA.clearError('t17-alert');

      connect();
      setRunning(true);
      el('t17-run').hidden = false;

      try {
        await RPA.sendJson('/api/track17/start', { batchId: batchId });
      } catch (err) {
        RPA.showError('t17-alert', err.message);
        setRunning(false);
      }
    });

    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-track17').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
