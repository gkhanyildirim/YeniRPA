/* =============================================================================
   GMV Notification — Telegram + schedule settings, the manual buttons, and the
   send history. Talks to /api/gmv, which answers { success, message, data }.
   The bot token is only ever sent to the server; the page learns whether one is
   saved and nothing more.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'gmv-notification';

  const STATUS = {
    sent:    { label: 'Sent',            tone: 'green' },
    checked: { label: 'Checked',         tone: 'blue' },
    test:    { label: 'Test sent',       tone: 'green' },
    failed:  { label: 'Failed',          tone: 'red' },
    session: { label: 'Session warning', tone: 'amber' },
    skipped: { label: 'Skipped',         tone: 'amber' },
    missed:  { label: 'Missed',          tone: 'amber' }
  };
  const SOURCE = { scheduled: 'Schedule', manual: 'Manual', test: 'Test' };
  const SESSION = {
    valid:   { label: 'Session valid',            tone: 'green' },
    expired: { label: 'Session expired',          tone: 'red' },
    none:    { label: 'No saved session',         tone: 'red' },
    unknown: { label: 'Not checked yet',          tone: 'amber' }
  };

  function el(id) { return document.getElementById(id); }

  async function api(method, url, payload) {
    const options = { method: method };
    if (payload !== undefined) {
      options.headers = { 'Content-Type': 'application/json' };
      options.body = JSON.stringify(payload);
    }

    const response = await fetch(url, options);
    let body;
    try {
      body = await response.json();
    } catch (e) {
      throw new Error('Request failed with status ' + response.status + '.');
    }
    if (!body || body.success !== true) {
      throw new Error((body && body.message) || 'Request failed.');
    }
    return body;
  }

  function showSuccess(message) {
    RPA.clearError('gmv-error');
    const box = el('gmv-success');
    box.querySelector('.msg').textContent = message;
    box.classList.add('is-shown');
  }

  function showError(message) {
    el('gmv-success').classList.remove('is-shown');
    RPA.showError('gmv-error', message);
  }

  function clearMessages() {
    RPA.clearError('gmv-error');
    el('gmv-success').classList.remove('is-shown');
  }

  function formatMoney(value) {
    return '€' + Math.round(value).toLocaleString('tr-TR');
  }

  function formatTime(iso) {
    return new Date(iso).toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'short' });
  }

  function badge(text, tone) {
    const span = document.createElement('span');
    span.className = 'badge ' + tone;
    span.textContent = text;
    return span;
  }

  function syncModeFields() {
    const mode = el('gmv-mode').value;
    el('gmv-times-field').hidden = mode !== 'times';
    el('gmv-window-field').hidden = mode !== 'hourly';
  }

  function applySettings(data) {
    el('gmv-chat').value = data.chatId || '';
    el('gmv-mode').value = data.mode || 'times';
    el('gmv-times').value = (data.times || []).join(', ');
    el('gmv-from').value = data.windowStartHour;
    el('gmv-to').value = data.windowEndHour;
    el('gmv-target').value = data.dailyTarget == null ? '' : data.dailyTarget;
    el('gmv-enabled').checked = !!data.enabled;
    el('gmv-keepalive').textContent = data.keepAliveMinutes;

    // The token field is always emptied: a saved token is reported, never echoed back.
    el('gmv-token').value = '';
    el('gmv-token-hint').textContent = data.hasToken
      ? 'A token is saved. Leave this empty to keep it.'
      : 'Create a bot with @BotFather and paste its token here.';
    el('gmv-clear-token').hidden = !data.hasToken;

    const session = SESSION[data.session.state] || SESSION.unknown;
    const sessionBadge = el('gmv-session-badge');
    sessionBadge.className = 'badge ' + session.tone;
    sessionBadge.textContent = session.label;
    el('gmv-session-checked').textContent = data.session.checkedUtc
      ? 'Last checked ' + formatTime(data.session.checkedUtc)
      : '';

    syncModeFields();
  }

  function readForm() {
    const target = el('gmv-target').value.trim();
    return {
      enabled: el('gmv-enabled').checked,
      mode: el('gmv-mode').value,
      times: el('gmv-times').value.split(',').map(function (t) { return t.trim(); }).filter(Boolean),
      windowStartHour: parseInt(el('gmv-from').value, 10),
      windowEndHour: parseInt(el('gmv-to').value, 10),
      chatId: el('gmv-chat').value.trim(),
      token: el('gmv-token').value.trim(),
      dailyTarget: target === '' ? null : Number(target)
    };
  }

  function renderHistory(rows) {
    const body = el('gmv-history-body');
    body.textContent = '';

    if (!rows.length) {
      const row = body.insertRow();
      const cell = row.insertCell();
      cell.colSpan = 5;
      cell.className = 'empty-cell';
      cell.textContent = 'No notifications yet.';
      return;
    }

    rows.forEach(function (item) {
      const row = body.insertRow();
      row.insertCell().textContent = formatTime(item.timestampUtc);
      row.insertCell().textContent = SOURCE[item.trigger] || item.trigger;

      const gmv = row.insertCell();
      gmv.className = 'num';
      gmv.textContent = item.gmv == null ? '—' : formatMoney(item.gmv);

      const status = STATUS[item.status] || { label: item.status, tone: 'amber' };
      row.insertCell().appendChild(badge(status.label, status.tone));

      row.insertCell().textContent = item.error || '';
    });
  }

  async function loadSettings() {
    applySettings((await api('GET', '/api/gmv/settings')).data);
  }

  async function loadHistory() {
    renderHistory((await api('GET', '/api/gmv/history?count=50')).data);
  }

  /** Runs one button's request with the shared busy state and error box. */
  async function run(button, busyLabel, work) {
    clearMessages();
    RPA.setBusy(button, true, busyLabel);
    try {
      await work();
    } catch (err) {
      showError(err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  async function activate() {
    try {
      await Promise.all([loadSettings(), loadHistory()]);
    } catch (err) {
      showError(err.message);
    }
  }

  document.addEventListener('DOMContentLoaded', function () {
    if (!el('gmv-save')) return;

    el('gmv-mode').addEventListener('change', syncModeFields);

    el('gmv-save').addEventListener('click', function () {
      run(el('gmv-save'), 'Saving…', async function () {
        const body = await api('POST', '/api/gmv/settings', readForm());
        applySettings(body.data);
        showSuccess(body.message);
      });
    });

    el('gmv-test').addEventListener('click', function () {
      run(el('gmv-test'), 'Sending…', async function () {
        try {
          showSuccess((await api('POST', '/api/gmv/test')).message);
        } finally {
          await loadHistory();
        }
      });
    });

    el('gmv-clear-token').addEventListener('click', function () {
      if (!window.confirm('Remove the saved bot token? Notifications will be switched off.')) return;
      run(el('gmv-clear-token'), 'Removing…', async function () {
        const body = await api('POST', '/api/gmv/clear-token');
        applySettings(body.data);
        showSuccess(body.message);
      });
    });

    el('gmv-check').addEventListener('click', function () {
      const result = el('gmv-check-result');
      result.textContent = '';
      run(el('gmv-check'), 'Reading…', async function () {
        try {
          const body = await api('POST', '/api/gmv/check', { send: el('gmv-check-send').checked });
          result.textContent = body.message;
        } finally {
          await Promise.all([loadSettings(), loadHistory()]);
        }
      });
    });

    el('gmv-history-refresh').addEventListener('click', function () {
      loadHistory().catch(function (err) { showError(err.message); });
    });

    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-gmv-notification').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
