/* =============================================================================
   Application shell: module navigation, theme, file pickers, upload plumbing.
   The two report dashboards register themselves on window.RPA and are driven
   from here.
   ============================================================================= */

window.RPA = window.RPA || {};

(function (RPA) {
  'use strict';

  // ---------------------------------------------------------------------------
  // Shared palette. Chart.js needs concrete colours, and the CSS custom
  // properties change with the theme, so they are resolved from the live
  // document rather than hard-coded here.
  // ---------------------------------------------------------------------------

  RPA.palette = function () {
    const css = getComputedStyle(document.documentElement);
    const read = (name, fallback) => (css.getPropertyValue(name).trim() || fallback);
    return {
      accent: read('--accent', '#0B5FC4'),
      red: read('--red', '#C9312B'),
      green: read('--green', '#0A6B45'),
      amber: read('--amber', '#9A5B06'),
      ink2: read('--ink-2', '#4A5A6B'),
      ink3: read('--ink-3', '#7B8B9C'),
      line: read('--line', '#DCE3EA'),
      surface: read('--surface', '#FFFFFF')
    };
  };

  /**
   * Chart.js paints to a canvas, so colours must be values the 2D context
   * understands — color-mix() is not reliably supported there. Convert an
   * #rgb/#rrggbb token to rgba() instead.
   */
  RPA.alpha = function (color, a) {
    const hex = String(color).trim().replace('#', '');
    const full = hex.length === 3 ? hex.split('').map(c => c + c).join('') : hex;
    if (full.length < 6) return color;
    const n = parseInt(full.slice(0, 6), 16);
    return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + a + ')';
  };

  // Applied on every (re)render so charts follow the theme.
  RPA.applyChartDefaults = function () {
    const p = RPA.palette();
    Chart.defaults.font.family =
      "'IBM Plex Sans VF', ui-sans-serif, 'Segoe UI', system-ui, sans-serif";
    Chart.defaults.font.size = 11;
    Chart.defaults.color = p.ink2;
    Chart.defaults.borderColor = p.line;
  };

  // ---------------------------------------------------------------------------
  // Formatting helpers shared by both dashboards.
  // ---------------------------------------------------------------------------

  RPA.fmtInt = v => (v || 0).toLocaleString('en-US');
  RPA.fmtPct = v => (v * 100).toFixed(1) + '%';
  RPA.fmtHours = v => v.toFixed(1) + 'h';
  RPA.fmtMoney = function (v, cur) {
    const n = (v || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    return cur ? n + ' ' + cur : n;
  };
  RPA.fmtDays = v => (v === null || v === undefined ? '-' : v.toFixed(1) + ' days');

  RPA.escapeHtml = function (value) {
    if (value === null || value === undefined) return '';
    return String(value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  };

  /**
   * Renders a table into a stable wrapper element. The empty state replaces the
   * wrapper's *contents*, never the wrapper itself — the original report swapped
   * the <table> for a <div> via outerHTML, after which re-filtering back to a
   * non-empty result set silently rendered nothing.
   */
  RPA.renderTable = function (wrapperId, rows, columns, emptyMessage) {
    const wrap = document.getElementById(wrapperId);
    if (!wrap) return;

    // Export mirrors the table it just rendered. A column with a `value` function contributes its
    // raw figure so Excel receives a number; without one the rendered cell is stripped back to text,
    // which is what badges and other markup-only cells should become.
    RPA.registerExport(wrapperId, {
      columns: columns.map(c => ({ label: textFromHtml(c.label), numeric: !!c.numeric })),
      rows: rows.map(r => columns.map(c => (c.value ? c.value(r) : textFromHtml(c.render(r)))))
    });

    if (!rows.length) {
      wrap.innerHTML = '<div class="empty-state">' + RPA.escapeHtml(emptyMessage) + '</div>';
      wrap.style.border = 'none';
      return;
    }

    wrap.style.border = '';
    const thead = '<thead><tr>' +
      columns.map(c => '<th>' + c.label + '</th>').join('') + '</tr></thead>';
    const tbody = '<tbody>' + rows.map(r =>
      '<tr>' + columns.map(c => '<td' + (c.numeric ? ' class="num"' : '') + '>' + c.render(r) + '</td>').join('') + '</tr>'
    ).join('') + '</tbody>';

    wrap.innerHTML = '<table>' + thead + tbody + '</table>';
  };

  RPA.renderKpis = function (gridId, items) {
    // A KPI block is a label/value readout rather than something anyone sums, so the exported value
    // is the formatted string that is on screen ("8.3%", "36.7h") instead of a bare number.
    RPA.registerExport(gridId, {
      columns: [{ label: 'Metric' }, { label: 'Value', numeric: true }],
      rows: items.map(([label, value]) => [label, value])
    });

    const grid = document.getElementById(gridId);
    grid.innerHTML = items.map(([label, value, tone], i) => {
      // Values are never broken mid-number, so a long one (e.g. "124,652,748.41 TRY")
      // has to be stepped down in size instead of wrapping or clipping.
      const longest = String(value).split(' ')
        .reduce((max, part) => Math.max(max, part.length), 0);
      const sizeClass = longest > 17 ? ' is-xlong' : longest > 11 ? ' is-long' : '';

      return '<div class="kpi' + (tone ? ' is-' + tone : '') +
               '" style="animation-delay:' + (i * 26) + 'ms">' +
               '<div class="kpi-label">' + RPA.escapeHtml(label) + '</div>' +
               '<div class="kpi-value ' + (tone || '') + sizeClass + '">' +
                 RPA.escapeHtml(value) +
               '</div>' +
             '</div>';
    }).join('');
  };

  // ---------------------------------------------------------------------------
  // Per-section Excel export
  //
  // Every render leaves its data here keyed by the id of the element it rendered into; the button in
  // the markup names the same id in data-export and supplies the human title for the sheet. Nothing
  // is recomputed at download time, so the workbook holds exactly what is on screen — including
  // whatever the date filter is currently narrowing it to.
  // ---------------------------------------------------------------------------

  const exportSpecs = new Map();
  let exportContext = '';

  /** Sets the line printed under the title of every exported sheet — i.e. the active filter. */
  RPA.setExportContext = function (text) { exportContext = text || ''; };

  /** Records a section's data, or drops it when there is nothing to export. */
  RPA.registerExport = function (key, spec) {
    if (spec && spec.rows && spec.rows.length) exportSpecs.set(key, spec);
    else exportSpecs.delete(key);
  };

  /** Greys out the button of any section with no data — a missing column, or a filter that empties it. */
  RPA.syncExportButtons = function () {
    document.querySelectorAll('[data-export]').forEach(button => {
      button.disabled = !exportSpecs.has(button.dataset.export);
    });
  };

  // Cells are produced as HTML strings, so the export path reads them back through an off-document
  // element rather than trying to unpick the markup with a regex.
  const scratch = document.createElement('div');
  function textFromHtml(html) {
    scratch.innerHTML = html;
    return scratch.textContent.replace(/\s+/g, ' ').trim();
  }

  async function downloadSection(button) {
    const spec = exportSpecs.get(button.dataset.export);
    if (!spec) return;

    const title = button.dataset.exportTitle || button.dataset.export;
    const prefix = button.dataset.exportPrefix || 'Report';
    const alert = button.closest('.panel')?.querySelector('.alert');

    RPA.setBusy(button, true, 'Building…');
    try {
      await RPA.postDownloadJson('/api/export/xlsx', {
        fileName: prefix + ' - ' + title,
        sheetName: title,
        title: title,
        subtitle: exportContext,
        columns: spec.columns,
        rows: spec.rows
      }, 'export.xlsx');
    } catch (err) {
      if (alert) RPA.showError(alert.id, err.message);
    } finally {
      RPA.setBusy(button, false);
    }
  }

  RPA.stamp = function (elementId) {
    const el = document.getElementById(elementId);
    if (!el) return;
    el.textContent = 'Prepared ' + new Date().toLocaleString('en-US', {
      year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
    });
  };

  /** Seeds a pair of date inputs with the min/max of the data they filter. */
  RPA.seedDateRange = function (fromId, toId, isoDates) {
    const sorted = isoDates.filter(Boolean).map(d => d.slice(0, 10)).sort();
    const from = document.getElementById(fromId);
    const to = document.getElementById(toId);
    if (!sorted.length) {
      [from, to].forEach(el => { el.removeAttribute('min'); el.removeAttribute('max'); });
      return;
    }
    [from, to].forEach(el => {
      el.min = sorted[0];
      el.max = sorted[sorted.length - 1];
    });
  };

  // ---------------------------------------------------------------------------
  // Theme
  // ---------------------------------------------------------------------------

  function currentTheme() {
    const explicit = document.documentElement.dataset.theme;
    if (explicit === 'dark' || explicit === 'light') return explicit;
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  function initTheme() {
    const toggle = document.getElementById('theme-toggle');
    if (!toggle) return;

    toggle.addEventListener('click', () => {
      const next = currentTheme() === 'dark' ? 'light' : 'dark';
      document.documentElement.dataset.theme = next;
      try { localStorage.setItem('rpa-theme', next); } catch (e) { /* private mode */ }
      document.dispatchEvent(new CustomEvent('rpa:themechange', { detail: { theme: next } }));
    });
  }

  // ---------------------------------------------------------------------------
  // Module navigation (tabs + hash deep-links + remembered choice)
  // ---------------------------------------------------------------------------

  const MODULES = {
    'order-report': { tab: 'tab-order', panel: 'panel-order' },
    'return-sla': { tab: 'tab-return', panel: 'panel-return' },
    // Browser automation rather than a report: writes to Mirakl instead of reading a file.
    'create-return': { tab: 'tab-create-return', panel: 'panel-create-return' },
    // Reference page: static content, no upload and no dashboard of its own.
    'methodology': { tab: 'tab-methodology', panel: 'panel-methodology' }
  };

  function showModule(name, options) {
    if (!MODULES[name]) name = 'order-report';
    const opts = options || {};

    Object.keys(MODULES).forEach(key => {
      const isActive = key === name;
      const tab = document.getElementById(MODULES[key].tab);
      const panel = document.getElementById(MODULES[key].panel);
      tab.setAttribute('aria-selected', isActive ? 'true' : 'false');
      tab.tabIndex = isActive ? 0 : -1;
      panel.hidden = !isActive;
    });

    try { localStorage.setItem('rpa-module', name); } catch (e) { /* private mode */ }

    if (opts.updateHash !== false && window.location.hash !== '#/' + name) {
      history.replaceState(null, '', '#/' + name);
    }
    if (opts.focus) document.getElementById(MODULES[name].tab).focus();

    // Lets a module defer work until it is actually looked at — the automation module opens its
    // event stream here rather than holding a connection open for report-only users.
    document.dispatchEvent(new CustomEvent('rpa:modulechange', { detail: { module: name } }));
  }

  function initNav() {
    const tabs = Array.from(document.querySelectorAll('.nav-item[role="tab"]'));

    tabs.forEach(tab => {
      tab.addEventListener('click', () => showModule(tab.dataset.module));
    });

    // Left/Right/Home/End move between tabs, per the WAI-ARIA tabs pattern.
    document.querySelector('.nav[role="tablist"]').addEventListener('keydown', event => {
      const keys = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'];
      if (keys.indexOf(event.key) === -1) return;

      const index = tabs.findIndex(t => t.getAttribute('aria-selected') === 'true');
      let next = index;
      if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = (index - 1 + tabs.length) % tabs.length;
      else if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = (index + 1) % tabs.length;
      else if (event.key === 'Home') next = 0;
      else if (event.key === 'End') next = tabs.length - 1;

      event.preventDefault();
      showModule(tabs[next].dataset.module, { focus: true });
    });

    window.addEventListener('hashchange', () => {
      const fromHash = window.location.hash.replace(/^#\/?/, '');
      if (MODULES[fromHash]) showModule(fromHash, { updateHash: false });
    });

    const fromHash = window.location.hash.replace(/^#\/?/, '');
    let initial = 'order-report';
    if (MODULES[fromHash]) {
      initial = fromHash;
    } else {
      try {
        const remembered = localStorage.getItem('rpa-module');
        if (MODULES[remembered]) initial = remembered;
      } catch (e) { /* private mode */ }
    }
    showModule(initial);
  }

  // ---------------------------------------------------------------------------
  // File pickers (click + drag & drop)
  // ---------------------------------------------------------------------------

  function formatBytes(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(0) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  function initDropzone(dropId, inputId) {
    const drop = document.getElementById(dropId);
    const input = document.getElementById(inputId);
    if (!drop || !input) return;

    const nameEl = drop.querySelector('.drop-file .name');
    const sizeEl = drop.querySelector('.drop-file .size');

    function reflect() {
      const file = input.files && input.files[0];
      if (file) {
        drop.classList.add('has-file');
        nameEl.textContent = file.name;
        sizeEl.textContent = formatBytes(file.size);
      } else {
        drop.classList.remove('has-file');
        nameEl.textContent = '';
        sizeEl.textContent = '';
      }
    }

    input.addEventListener('change', reflect);

    ['dragenter', 'dragover'].forEach(type => {
      drop.addEventListener(type, e => {
        e.preventDefault();
        drop.classList.add('is-dragover');
      });
    });
    ['dragleave', 'drop'].forEach(type => {
      drop.addEventListener(type, e => {
        e.preventDefault();
        drop.classList.remove('is-dragover');
      });
    });

    drop.addEventListener('drop', e => {
      const files = e.dataTransfer && e.dataTransfer.files;
      if (!files || !files.length) return;
      // DataTransfer -> input.files is the only way to keep FormData in sync.
      const dt = new DataTransfer();
      dt.items.add(files[0]);
      input.files = dt.files;
      reflect();
    });
  }

  RPA.initDropzone = initDropzone;

  // ---------------------------------------------------------------------------
  // Requests
  // ---------------------------------------------------------------------------

  RPA.setBusy = function (button, busy, busyLabel) {
    if (!button) return;
    const text = button.querySelector('.btn-text');
    if (busy) {
      button.dataset.idleLabel = text.textContent;
      if (busyLabel) text.textContent = busyLabel;
      button.classList.add('is-busy');
      button.disabled = true;
    } else {
      if (button.dataset.idleLabel) text.textContent = button.dataset.idleLabel;
      button.classList.remove('is-busy');
      button.disabled = false;
    }
  };

  RPA.showError = function (alertId, message) {
    const el = document.getElementById(alertId);
    el.querySelector('.msg').textContent = message;
    el.classList.add('is-shown');
  };

  RPA.clearError = function (alertId) {
    document.getElementById(alertId).classList.remove('is-shown');
  };

  RPA.showSkeleton = function (skeletonId, resultsId) {
    document.getElementById(skeletonId).classList.add('is-shown');
    document.getElementById(resultsId).hidden = true;
  };

  RPA.hideSkeleton = function (skeletonId) {
    document.getElementById(skeletonId).classList.remove('is-shown');
  };

  /** POSTs a FormData and returns parsed JSON, turning a 400 { error } into a throw. */
  RPA.postJson = async function (url, formData) {
    const response = await fetch(url, { method: 'POST', body: formData });
    if (!response.ok) throw new Error(await readError(response));
    return response.json();
  };

  /** Same, for endpoints that take a JSON body — postJson only knows how to send a FormData. */
  RPA.sendJson = async function (url, payload) {
    const response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (!response.ok) throw new Error(await readError(response));
    return response.json();
  };

  /** POSTs a FormData and triggers a browser download of the binary response. */
  RPA.postDownload = function (url, formData, fallbackFileName) {
    return download(url, { method: 'POST', body: formData }, fallbackFileName);
  };

  /** Same, for endpoints that take a JSON body instead of an upload. */
  RPA.postDownloadJson = function (url, payload, fallbackFileName) {
    return download(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }, fallbackFileName);
  };

  async function download(url, init, fallbackFileName) {
    const response = await fetch(url, init);
    if (!response.ok) throw new Error(await readError(response));

    const blob = await response.blob();
    const objectUrl = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = fileNameFrom(response) || fallbackFileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(objectUrl);
  }

  function fileNameFrom(response) {
    const header = response.headers.get('content-disposition') || '';
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(header);
    return match ? decodeURIComponent(match[1]) : null;
  }

  async function readError(response) {
    const text = await response.text();
    try {
      const parsed = JSON.parse(text);
      if (parsed && parsed.error) return parsed.error;
      if (parsed && parsed.title) return parsed.title;
    } catch (e) { /* not JSON — fall through to the raw body */ }
    return text || ('Request failed with status ' + response.status + '.');
  }

  // ---------------------------------------------------------------------------

  document.addEventListener('DOMContentLoaded', function () {
    initTheme();
    initNav();
    RPA.applyChartDefaults();

    // One listener for every export button on every report; the button carries which section it is.
    document.addEventListener('click', function (event) {
      const button = event.target.closest('[data-export]');
      if (button && !button.disabled) downloadSection(button);
    });

    RPA.syncExportButtons();
  });

})(window.RPA);
