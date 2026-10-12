/* =============================================================================
   Jobs — the background jobs, when each last ran and runs next, and their
   schedule. Talks to /api/jobs, which answers { success, message, data }. The
   list is rendered from whatever the server sends, so a new job needs no change
   here.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'jobs';
  const REFRESH_MS = 30000;

  let timer = null;
  // A row being edited is not overwritten by the periodic refresh.
  let editing = false;

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
    RPA.clearError('jobs-error');
    const box = el('jobs-success');
    box.querySelector('.msg').textContent = message;
    box.classList.add('is-shown');
  }

  function showError(message) {
    el('jobs-success').classList.remove('is-shown');
    RPA.showError('jobs-error', message);
  }

  function formatTime(iso) {
    return iso ? new Date(iso).toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'medium' }) : '—';
  }

  function badge(text, tone) {
    const span = document.createElement('span');
    span.className = 'badge ' + tone;
    span.textContent = text;
    return span;
  }

  function numberInput(value, min, max, label, cls) {
    const input = document.createElement('input');
    input.type = 'number';
    input.min = min;
    input.max = max;
    input.step = 1;
    input.value = value;
    input.setAttribute('aria-label', label);
    if (cls) input.className = cls;
    input.addEventListener('input', function () { editing = true; });
    return input;
  }

  function renderRow(job) {
    const row = document.createElement('tr');

    const name = row.insertCell();
    const title = document.createElement('span');
    title.className = 'jobs-name';
    title.textContent = job.name;
    name.appendChild(title);
    [job.description, job.note].forEach(function (text) {
      if (!text) return;
      const line = document.createElement('span');
      line.className = 'jobs-note';
      line.textContent = text;
      name.appendChild(line);
    });

    const toggleCell = row.insertCell();
    const toggle = document.createElement('input');
    toggle.type = 'checkbox';
    toggle.checked = job.enabled;
    toggle.disabled = !job.canToggle;
    toggle.setAttribute('aria-label', job.name + ' on');
    toggle.addEventListener('change', function () { editing = true; });
    toggleCell.appendChild(toggle);

    const minutes = numberInput(job.intervalMinutes, job.minMinutes, job.maxMinutes,
      job.name + ' interval in minutes', 'jobs-minutes');
    row.insertCell().appendChild(minutes);

    const windowCell = row.insertCell();
    let from = null;
    let to = null;
    if (job.hasWindow) {
      const wrap = document.createElement('span');
      wrap.className = 'jobs-hours';
      from = numberInput(job.windowStartHour, 0, 23, job.name + ' from hour');
      to = numberInput(job.windowEndHour, 0, 23, job.name + ' to hour');
      wrap.append(from, document.createTextNode('–'), to);
      windowCell.appendChild(wrap);
    } else {
      windowCell.textContent = '—';
    }

    row.insertCell().textContent = formatTime(job.lastRunUtc);
    row.insertCell().textContent = job.enabled ? formatTime(job.nextRunUtc) : '—';

    const status = row.insertCell();
    if (job.lastOk === true) status.appendChild(badge('OK', 'green'));
    else if (job.lastOk === false) status.appendChild(badge('Problem', 'red'));
    else status.appendChild(badge(job.enabled ? 'Waiting' : 'Off', 'amber'));
    if (job.lastMessage) {
      const line = document.createElement('span');
      line.className = 'jobs-note';
      line.textContent = job.lastMessage;
      status.appendChild(line);
    }

    const actions = row.insertCell();
    const save = document.createElement('button');
    save.type = 'button';
    save.className = 'btn btn-primary btn-sm';
    save.innerHTML = '<span class="spinner" aria-hidden="true"></span><span class="btn-text">Save</span>';
    save.addEventListener('click', function () {
      const payload = { intervalMinutes: parseInt(minutes.value, 10) };
      if (job.canToggle) payload.enabled = toggle.checked;
      if (job.hasWindow) {
        payload.windowStartHour = parseInt(from.value, 10);
        payload.windowEndHour = parseInt(to.value, 10);
      }
      saveJob(save, job.id, payload);
    });
    actions.appendChild(save);

    return row;
  }

  function render(jobs) {
    const body = el('jobs-body');
    body.textContent = '';

    if (!jobs.length) {
      const row = body.insertRow();
      const cell = row.insertCell();
      cell.colSpan = 8;
      cell.className = 'empty-cell';
      cell.textContent = 'No jobs.';
      return;
    }

    jobs.forEach(function (job) { body.appendChild(renderRow(job)); });
  }

  async function load() {
    render((await api('GET', '/api/jobs')).data);
    editing = false;
  }

  async function saveJob(button, id, payload) {
    RPA.clearError('jobs-error');
    el('jobs-success').classList.remove('is-shown');
    RPA.setBusy(button, true, 'Saving…');
    try {
      const body = await api('PUT', '/api/jobs/' + encodeURIComponent(id), payload);
      render(body.data);
      editing = false;
      showSuccess(body.message);
    } catch (err) {
      showError(err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  function startRefresh() {
    stopRefresh();
    timer = setInterval(function () {
      if (editing) return;
      load().catch(function (err) { showError(err.message); });
    }, REFRESH_MS);
  }

  function stopRefresh() {
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
  }

  async function activate() {
    try {
      await load();
    } catch (err) {
      showError(err.message);
    }
    startRefresh();
  }

  document.addEventListener('DOMContentLoaded', function () {
    if (!el('jobs-body')) return;

    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
      else stopRefresh();
    });

    if (el('tab-jobs').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
