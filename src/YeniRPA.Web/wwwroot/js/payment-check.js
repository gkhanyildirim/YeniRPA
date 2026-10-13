/* =============================================================================
   Payment Check — schedule, the one-time storefront sign-in, the manual run and
   the history. Talks to /api/payment-check, which answers { success, message, data }.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'payment-check';

  const STATUS = {
    'ok':            { label: 'Payment page OK',  tone: 'green' },
    'unreachable':   { label: 'Problem',          tone: 'red' },
    'not-logged-in': { label: 'Login expired',    tone: 'amber' },
    'cart-empty':    { label: 'Cart empty',       tone: 'amber' },
    'error':         { label: 'Could not run',    tone: 'amber' }
  };
  const SOURCE = { scheduled: 'Schedule', manual: 'Manual' };

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
    RPA.clearError('pc-error');
    const box = el('pc-success');
    box.querySelector('.msg').textContent = message;
    box.classList.add('is-shown');
  }

  function showError(message) {
    el('pc-success').classList.remove('is-shown');
    RPA.showError('pc-error', message);
  }

  function clearMessages() {
    RPA.clearError('pc-error');
    el('pc-success').classList.remove('is-shown');
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

  function applySettings(data) {
    el('pc-interval').value = data.intervalMinutes;
    el('pc-from').value = data.windowStartHour;
    el('pc-to').value = data.windowEndHour;
    el('pc-enabled').checked = !!data.enabled;
    el('pc-telegram-hint').textContent = data.telegramReady
      ? ''
      : 'Telegram is not set up yet. Enter the bot token and chat ID in GMV Notification first.';

    const sessionBadge = el('pc-session-badge');
    sessionBadge.className = 'badge ' + (data.hasSession ? 'green' : 'red');
    sessionBadge.textContent = data.hasSession ? 'Session saved' : 'No saved session';
    el('pc-session-saved').textContent = data.sessionSavedUtc ? 'Saved ' + formatTime(data.sessionSavedUtc) : '';
    el('pc-login-clear').hidden = !data.hasSession;
  }

  function readForm() {
    return {
      enabled: el('pc-enabled').checked,
      intervalMinutes: parseInt(el('pc-interval').value, 10),
      windowStartHour: parseInt(el('pc-from').value, 10),
      windowEndHour: parseInt(el('pc-to').value, 10)
    };
  }

  function renderHistory(rows) {
    const body = el('pc-history-body');
    body.textContent = '';

    if (!rows.length) {
      const row = body.insertRow();
      const cell = row.insertCell();
      cell.colSpan = 6;
      cell.className = 'empty-cell';
      cell.textContent = 'No checks yet.';
      return;
    }

    rows.forEach(function (item) {
      const row = body.insertRow();
      row.insertCell().textContent = formatTime(item.timestampUtc);
      row.insertCell().textContent = SOURCE[item.trigger] || item.trigger;

      const status = STATUS[item.status] || { label: item.status, tone: 'amber' };
      row.insertCell().appendChild(badge(status.label, status.tone));

      row.insertCell().textContent = item.step || '';
      row.insertCell().textContent = item.detail || '';
      row.insertCell().textContent = item.notified ? 'Sent' : (item.notifyNote || 'Not sent');
    });
  }

  async function loadSettings() {
    applySettings((await api('GET', '/api/payment-check/settings')).data);
  }

  async function loadHistory() {
    renderHistory((await api('GET', '/api/payment-check/history?count=50')).data);
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
    if (!el('pc-save')) return;

    el('pc-save').addEventListener('click', function () {
      run(el('pc-save'), 'Saving…', async function () {
        const body = await api('POST', '/api/payment-check/settings', readForm());
        applySettings(body.data);
        showSuccess(body.message);
      });
    });

    el('pc-login-start').addEventListener('click', function () {
      run(el('pc-login-start'), 'Opening…', async function () {
        const body = await api('POST', '/api/payment-check/login/start');
        applySettings(body.data);
        showSuccess(body.message);
      });
    });

    el('pc-login-save').addEventListener('click', function () {
      run(el('pc-login-save'), 'Saving…', async function () {
        const body = await api('POST', '/api/payment-check/login/save');
        applySettings(body.data);
        showSuccess(body.message);
      });
    });

    el('pc-login-clear').addEventListener('click', function () {
      if (!window.confirm('Remove the saved storefront session? Checks will report "login expired" until you sign in again.')) return;
      run(el('pc-login-clear'), 'Removing…', async function () {
        const body = await api('POST', '/api/payment-check/login/clear');
        applySettings(body.data);
        showSuccess(body.message);
      });
    });

    el('pc-run').addEventListener('click', function () {
      const result = el('pc-run-result');
      result.textContent = '';
      run(el('pc-run'), 'Checking…', async function () {
        try {
          const body = await api('POST', '/api/payment-check/run');
          const status = STATUS[body.data.status] || { label: body.data.status };
          result.textContent = status.label + ' — ' + body.message +
            (body.data.notified ? ' (sent to Telegram)' : ' (not sent: ' + (body.data.notifyNote || 'unknown') + ')');
        } finally {
          await Promise.all([loadSettings(), loadHistory()]);
        }
      });
    });

    el('pc-history-refresh').addEventListener('click', function () {
      loadHistory().catch(function (err) { showError(err.message); });
    });

    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-payment-check').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
