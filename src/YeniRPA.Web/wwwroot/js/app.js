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

    // The eight categorical slots, in the order the design system fixed them. Charts take colours
    // from the front of this list by series identity — never by rank, so narrowing a filter cannot
    // repaint the series that survive. Status colours are separate on purpose.
    const series = [];
    for (let i = 1; i <= 8; i++) series.push(read('--series-' + i, '#2A78D6'));

    return {
      accent: read('--accent', '#0B5FC4'),
      accentGlow: read('--accent-glow', 'rgba(11,95,196,.2)'),
      red: read('--red', '#C0342E'),
      green: read('--green', '#0A6B45'),
      amber: read('--amber', '#96590A'),
      ink: read('--ink', '#0C141C'),
      ink2: read('--ink-2', '#46586A'),
      ink3: read('--ink-3', '#7A8B9C'),

      // Status colours for filled marks. The text-grade red/amber above are tuned for contrast on
      // the page, and a bar filled with them reads brown rather than "this is a problem".
      markCritical: read('--mark-critical', '#D03B3B'),
      markSerious: read('--mark-serious', '#EC835A'),
      markWarning: read('--mark-warning', '#FAB219'),
      markGood: read('--mark-good', '#0CA30C'),
      line: read('--line', '#DEE5EC'),
      surface: read('--surface', '#FFFFFF'),
      surface2: read('--surface-2', '#F6F8FB'),
      surface3: read('--surface-3', '#EAF0F6'),
      series
    };
  };

  RPA.reducedMotion = () =>
    window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

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

  /**
   * Draws the value at the end of every bar of a horizontal bar chart. Chart.js ships no data-label
   * plugin and this app has no external runtime dependencies, so the twelve lines live here rather
   * than pulling in chartjs-plugin-datalabels.
   *
   * Configured per chart through `options.plugins.rpaBarLabels.labels` — an array parallel to the
   * dataset. A chart that supplies none draws nothing, which is how the charts that only want bars
   * keep their old look. Leave room for the text with `layout.padding.right`, or it is drawn
   * outside the canvas and clipped.
   */
  RPA.barLabelPlugin = {
    id: 'rpaBarLabels',
    afterDatasetsDraw(chart, args, opts) {
      const labels = (opts && opts.labels) || null;
      if (!labels || !labels.length) return;

      const meta = chart.getDatasetMeta(0);
      const ctx = chart.ctx;
      ctx.save();
      ctx.fillStyle = RPA.palette().ink2;
      ctx.font = '600 11px ' + Chart.defaults.font.family;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      meta.data.forEach((bar, i) => {
        if (!labels[i]) return;
        ctx.fillText(labels[i], bar.x + 7, bar.y);
      });
      ctx.restore();
    }
  };

  /**
   * Prints the total in the hole of a doughnut. The ring answers "how does this split"; the number
   * in the middle answers "out of how many", which otherwise only exists in a tooltip.
   * Configure with `options.plugins.rpaDoughnutCenter = { value: '33,841', label: 'order lines' }`.
   */
  RPA.doughnutCenterPlugin = {
    id: 'rpaDoughnutCenter',
    afterDatasetsDraw(chart, args, opts) {
      if (!opts || !opts.value) return;

      const p = RPA.palette();
      const area = chart.chartArea;
      if (!area) return;

      const x = (area.left + area.right) / 2;
      const y = (area.top + area.bottom) / 2;
      const ctx = chart.ctx;

      ctx.save();
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillStyle = p.ink;
      ctx.font = "600 20px 'IBM Plex Mono', ui-monospace, monospace";
      ctx.fillText(opts.value, x, y - 6);
      if (opts.label) {
        ctx.fillStyle = p.ink3;
        ctx.font = "600 9px 'IBM Plex Mono', ui-monospace, monospace";
        ctx.fillText(opts.label.toUpperCase(), x, y + 13);
      }
      ctx.restore();
    }
  };

  // Applied on every (re)render so charts follow the theme.
  RPA.applyChartDefaults = function () {
    const p = RPA.palette();
    const sans = "'IBM Plex Sans VF', ui-sans-serif, 'Segoe UI', system-ui, sans-serif";
    const mono = "'IBM Plex Mono', ui-monospace, 'Cascadia Mono', Consolas, monospace";

    Chart.defaults.font.family = sans;
    Chart.defaults.font.size = 11;
    Chart.defaults.color = p.ink2;
    Chart.defaults.borderColor = p.line;
    Chart.defaults.animation.duration = RPA.reducedMotion() ? 0 : 480;
    Chart.defaults.animation.easing = 'easeOutQuart';

    // One tooltip design for every chart in the app: a small panel in the page's own surface
    // colours, figures in the same mono face the tables use.
    const tooltip = Chart.defaults.plugins.tooltip;
    tooltip.backgroundColor = p.surface3;
    tooltip.titleColor = p.ink;
    tooltip.bodyColor = p.ink2;
    tooltip.borderColor = p.line;
    tooltip.borderWidth = 1;
    tooltip.cornerRadius = 8;
    tooltip.padding = 10;
    tooltip.caretSize = 5;
    tooltip.usePointStyle = true;
    tooltip.boxWidth = 8;
    tooltip.boxHeight = 8;
    tooltip.boxPadding = 6;
    tooltip.titleFont = { family: sans, size: 11.5, weight: '600' };
    tooltip.bodyFont = { family: mono, size: 11 };
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
   * Comparison key for anything an operator types into a filter box. The Turkish i family is folded
   * to a plain `i` before lowercasing, because no built-in comparison gets it right: "İ" lowercases
   * to i + a combining dot, and "ı" is a letter of its own. Without this, typing "firsat" misses
   * "FırsatKurdu" and typing "YURTİÇİ" misses "Yurtiçi". Accents and punctuation follow, so
   * "sürat kargo" and "SURAT-KARGO" fold together too.
   *
   * Mirrors SellerGroupMap.FoldName / CarrierNames.Fold on the server.
   */
  RPA.fold = function (value) {
    if (value === null || value === undefined) return '';
    return String(value)
      .replace(/[İIıi]/g, 'i')
      .toLowerCase()
      .replace(/ç/g, 'c').replace(/ş/g, 's').replace(/ğ/g, 'g')
      .replace(/ö/g, 'o').replace(/ü/g, 'u')
      .replace(/[^a-z0-9]+/g, ' ')
      .trim();
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

  // ---------------------------------------------------------------------------
  // Sortable / filterable table
  //
  // Same column contract as renderTable above, plus `filter` and `sortable`. Sorting and column
  // filters are the operator's own view of a section and outlive a re-render: the dashboard
  // recomputes every table whenever the date or seller filter changes, and losing the column filter
  // each time would make the two filters unusable together. State is therefore kept per wrapper id.
  //
  // Only the <tbody> is redrawn when a filter changes, so the text box the operator is typing into
  // keeps focus and caret. The whole table is rebuilt only when new data arrives.
  // ---------------------------------------------------------------------------

  const tableData = new Map();
  const tableState = new Map();

  function cellText(column, row) {
    return column.value ? column.value(row) : textFromHtml(column.render(row));
  }

  /** Pulls a number out of a cell value that may already be one, or may be "12.4%" / "1,240". */
  function cellNumber(column, row) {
    const raw = cellText(column, row);
    if (typeof raw === 'number') return raw;
    const parsed = parseFloat(String(raw).replace(/[^0-9.\-]/g, ''));
    return isNaN(parsed) ? null : parsed;
  }

  function distinctValues(rows, column) {
    return Array.from(new Set(rows.map(r => String(cellText(column, r)))))
      .filter(v => v !== '')
      .sort((a, b) => a.localeCompare(b, 'tr'));
  }

  function passesFilters(data, state, row) {
    return data.columns.every((column, i) => {
      const filter = state.filters[i];
      if (!filter || !column.filter) return true;

      if (column.filter === 'number') {
        const value = cellNumber(column, row);
        if (filter.min !== '' && filter.min !== undefined) {
          if (value === null || value < parseFloat(filter.min)) return false;
        }
        if (filter.max !== '' && filter.max !== undefined) {
          if (value === null || value > parseFloat(filter.max)) return false;
        }
        return true;
      }

      if (column.filter === 'select') {
        return !filter.value || String(cellText(column, row)) === filter.value;
      }

      return !filter.value ||
        RPA.fold(cellText(column, row)).indexOf(RPA.fold(filter.value)) !== -1;
    });
  }

  function visibleRows(wrapperId) {
    const data = tableData.get(wrapperId);
    const state = tableState.get(wrapperId);
    const matched = data.rows.filter(row => passesFilters(data, state, row));

    if (state.sortIndex >= 0) {
      const column = data.columns[state.sortIndex];
      matched.sort((a, b) => {
        const av = column.numeric ? cellNumber(column, a) : cellText(column, a);
        const bv = column.numeric ? cellNumber(column, b) : cellText(column, b);
        if (typeof av === 'number' && typeof bv === 'number') return (av - bv) * state.sortDir;
        if (av === null) return 1;
        if (bv === null) return -1;
        return String(av).localeCompare(String(bv), 'tr') * state.sortDir;
      });
    }

    // Capping *after* filtering and sorting is the point of the cap here: a 200-row limit applied
    // to the raw list would leave the filter searching only those 200 rows.
    const capped = data.options.maxRows ? matched.slice(0, data.options.maxRows) : matched;
    return { matched, capped };
  }

  function filterCell(column, index, state) {
    if (!column.filter) return '<th></th>';

    const current = state.filters[index] || {};
    const label = textFromHtml(column.label);

    if (column.filter === 'number') {
      return '<th class="num"><span class="range">' +
        '<input type="number" step="any" data-filter="' + index + '" data-kind="min" ' +
          'placeholder="≥" aria-label="' + RPA.escapeHtml(label) + ' minimum" ' +
          'value="' + RPA.escapeHtml(current.min || '') + '" />' +
        '<input type="number" step="any" data-filter="' + index + '" data-kind="max" ' +
          'placeholder="≤" aria-label="' + RPA.escapeHtml(label) + ' maximum" ' +
          'value="' + RPA.escapeHtml(current.max || '') + '" />' +
        '</span></th>';
    }

    if (column.filter === 'select') {
      // A value the operator picked can disappear from the data when the date/seller filter moves.
      // Keep it in the list rather than showing "All" while still filtering by it.
      const values = (column.filterOptions || []).slice();
      if (current.value && values.indexOf(current.value) === -1) values.unshift(current.value);

      const options = ['<option value="">All</option>'].concat(
        values.map(v =>
          '<option value="' + RPA.escapeHtml(v) + '"' +
          (current.value === v ? ' selected' : '') + '>' + RPA.escapeHtml(v) + '</option>'));
      return '<th><select data-filter="' + index + '" data-kind="select" aria-label="' +
        RPA.escapeHtml(label) + ' filter">' + options.join('') + '</select></th>';
    }

    return '<th><input type="search" data-filter="' + index + '" data-kind="text" ' +
      'placeholder="Filter" aria-label="' + RPA.escapeHtml(label) + ' filter" value="' +
      RPA.escapeHtml(current.value || '') + '" /></th>';
  }

  function headerCell(column, index, state) {
    const classes = column.numeric ? ' class="num"' : '';
    if (column.sortable === false) return '<th' + classes + '>' + column.label + '</th>';

    const sorted = state.sortIndex === index;
    const sort = sorted ? (state.sortDir === 1 ? 'ascending' : 'descending') : 'none';
    return '<th' + classes + ' aria-sort="' + sort + '">' +
      '<button type="button" class="th-sort" data-sort="' + index + '">' +
        column.label + '<span class="sort-mark" aria-hidden="true"></span>' +
      '</button></th>';
  }

  function bodyHtml(data, rows) {
    if (!rows.length) {
      return '<tr><td class="empty-cell" colspan="' + data.columns.length + '">' +
        'No rows match the column filters.</td></tr>';
    }
    return rows.map(r =>
      '<tr>' + data.columns.map(c =>
        '<td' + (c.numeric ? ' class="num"' : '') + '>' + c.render(r) + '</td>').join('') + '</tr>'
    ).join('');
  }

  function countText(data, matched, capped) {
    const total = data.rows.length;
    const parts = [RPA.fmtInt(matched.length) + ' of ' + RPA.fmtInt(total) + ' rows'];
    if (capped.length < matched.length) parts.push('showing the first ' + RPA.fmtInt(capped.length));
    return parts.join(' · ');
  }

  function syncTable(wrapperId) {
    const wrap = document.getElementById(wrapperId);
    const data = tableData.get(wrapperId);
    const state = tableState.get(wrapperId);
    if (!wrap || !data) return;

    const { matched, capped } = visibleRows(wrapperId);

    RPA.registerExport(wrapperId, {
      columns: data.columns.map(c => ({ label: textFromHtml(c.label), numeric: !!c.numeric })),
      rows: capped.map(r => data.columns.map(c => (c.value ? c.value(r) : textFromHtml(c.render(r)))))
    });

    const tbody = wrap.querySelector('tbody');
    if (tbody) tbody.innerHTML = bodyHtml(data, capped);

    const count = wrap.querySelector('.table-count');
    if (count) count.textContent = countText(data, matched, capped);

    wrap.querySelectorAll('thead tr:first-child th').forEach((th, i) => {
      if (data.columns[i] && data.columns[i].sortable === false) return;
      th.setAttribute('aria-sort',
        state.sortIndex === i ? (state.sortDir === 1 ? 'ascending' : 'descending') : 'none');
    });

    RPA.syncExportButtons();
  }

  function drawTable(wrapperId) {
    const wrap = document.getElementById(wrapperId);
    const data = tableData.get(wrapperId);
    const state = tableState.get(wrapperId);
    if (!wrap || !data) return;

    if (!data.rows.length) {
      RPA.registerExport(wrapperId, null);
      wrap.innerHTML = '<div class="empty-state">' + RPA.escapeHtml(data.emptyMessage) + '</div>';
      wrap.style.border = 'none';
      RPA.syncExportButtons();
      return;
    }

    wrap.style.border = '';
    const hasFilters = data.columns.some(c => c.filter);
    const head = '<thead><tr>' +
      data.columns.map((c, i) => headerCell(c, i, state)).join('') + '</tr>' +
      (hasFilters
        ? '<tr class="table-filters">' + data.columns.map((c, i) => filterCell(c, i, state)).join('') + '</tr>'
        : '') +
      '</thead>';

    // The class carries the second header row's offset in CSS: both rows are sticky, so the filter
    // row has to sit exactly one header height down or it covers the labels.
    wrap.innerHTML = '<table' + (hasFilters ? ' class="data-table"' : '') + '>' +
      head + '<tbody></tbody></table>' +
      '<div class="table-count"></div>';

    // Both header rows are sticky, so the filter row has to be pushed down by exactly the height of
    // the row above it. That height depends on the font the browser actually used, so it is
    // measured rather than guessed — a hard-coded offset leaves the filters covering the labels.
    if (hasFilters) {
      const labelRow = wrap.querySelector('thead tr');
      const height = labelRow ? labelRow.getBoundingClientRect().height : 0;
      if (height > 0) wrap.style.setProperty('--head-h', height + 'px');
    }

    syncTable(wrapperId);
  }

  function wireTable(wrapperId) {
    const wrap = document.getElementById(wrapperId);
    if (!wrap || wrap.dataset.dataTable === 'on') return;
    wrap.dataset.dataTable = 'on';

    wrap.addEventListener('click', function (event) {
      const button = event.target.closest('[data-sort]');
      if (!button) return;
      const index = Number(button.dataset.sort);
      const state = tableState.get(wrapperId);
      if (state.sortIndex === index) state.sortDir = -state.sortDir;
      else { state.sortIndex = index; state.sortDir = 1; }
      syncTable(wrapperId);
    });

    function readFilter(event) {
      const input = event.target.closest('[data-filter]');
      if (!input) return;
      const state = tableState.get(wrapperId);
      const index = Number(input.dataset.filter);
      const filter = state.filters[index] || (state.filters[index] = {});
      if (input.dataset.kind === 'min' || input.dataset.kind === 'max') filter[input.dataset.kind] = input.value;
      else filter.value = input.value;
      syncTable(wrapperId);
    }

    wrap.addEventListener('input', readFilter);
    wrap.addEventListener('change', readFilter);
  }

  /**
   * Columns take the renderTable shape plus:
   *   filter: 'text' | 'number' | 'select'   — control drawn under the header, omitted when absent
   *   filterOptions: string[]                — values for a 'select' filter; defaults to the column's
   *                                            own distinct values
   *   sortable: false                        — header stays plain text
   * Options: { maxRows } caps the rendered rows *after* filtering and sorting.
   */
  /**
   * Forgets every table's sort and column filters. Called when a new file is uploaded: the state is
   * meant to survive a change of date or seller on the same data, not to quietly carry a filter
   * over onto a different upload.
   */
  RPA.resetDataTables = function () { tableState.clear(); };

  RPA.renderDataTable = function (wrapperId, rows, columns, emptyMessage, options) {
    const opts = options || {};
    const prepared = columns.map(c => (c.filter === 'select' && !c.filterOptions
      ? Object.assign({}, c, { filterOptions: distinctValues(rows, c) })
      : c));

    tableData.set(wrapperId, { rows, columns: prepared, emptyMessage, options: opts });
    if (!tableState.has(wrapperId)) tableState.set(wrapperId, { sortIndex: -1, sortDir: 1, filters: {} });

    wireTable(wrapperId);
    drawTable(wrapperId);
  };

  /**
   * Renders a KPI grid.
   *
   * An item is either a tile — `[label, value, tone, context]`, where `tone` is ''/red/amber/green
   * and `context` is the optional line under the figure — or a band label, `{ group: 'Speed' }`,
   * which splits the grid into the groups the metrics actually belong to.
   *
   * `options.exportRows` overrides what the section's Excel button hands over. The order report
   * uses it to keep exporting all twelve key metrics while four of them are shown in the hero
   * above rather than as tiles here.
   */
  RPA.renderKpis = function (gridId, items, options) {
    const opts = options || {};
    const tiles = items.filter(item => !item.group);

    // A KPI block is a label/value readout rather than something anyone sums, so the exported value
    // is the formatted string that is on screen ("8.3%", "36.7h") instead of a bare number.
    RPA.registerExport(gridId, {
      columns: [{ label: 'Metric' }, { label: 'Value', numeric: true }],
      rows: (opts.exportRows || tiles).map(([label, value]) => [label, value])
    });

    let tileIndex = 0;
    const grid = document.getElementById(gridId);
    grid.innerHTML = items.map(item => {
      if (item.group) {
        return '<div class="kpi-band">' + RPA.escapeHtml(item.group) + '</div>';
      }

      const [label, value, tone, context] = item;

      // Values are never broken mid-number, so a long one (e.g. "124,652,748.41 TRY")
      // has to be stepped down in size instead of wrapping or clipping.
      const longest = String(value).split(' ')
        .reduce((max, part) => Math.max(max, part.length), 0);
      const sizeClass = longest > 17 ? ' is-xlong' : longest > 11 ? ' is-long' : '';

      return '<div class="kpi' + (tone ? ' is-' + tone : '') +
               '" style="animation-delay:' + (tileIndex++ * 26) + 'ms">' +
               '<div class="kpi-label">' + RPA.escapeHtml(label) + '</div>' +
               '<div class="kpi-value ' + (tone || '') + sizeClass + '">' +
                 RPA.escapeHtml(value) +
               '</div>' +
               (context ? '<div class="kpi-context">' + RPA.escapeHtml(context) + '</div>' : '') +
             '</div>';
    }).join('');
  };

  // ---------------------------------------------------------------------------
  // Hero, sparkline, chart legend, section index
  // ---------------------------------------------------------------------------

  /**
   * Counts a figure up to its final value. The value handed in is already formatted ("33,841",
   * "94.2%", "36.7h"), so the number inside it is animated in place and everything around it —
   * separators, unit, percent sign — is preserved exactly.
   */
  function countUp(el, text) {
    const match = /-?[\d.,]+/.exec(text);
    if (!match || RPA.reducedMotion()) { el.textContent = text; return; }

    const raw = match[0];
    const target = parseFloat(raw.replace(/,/g, ''));
    if (!isFinite(target)) { el.textContent = text; return; }

    const decimals = (raw.split('.')[1] || '').length;
    const before = text.slice(0, match.index);
    const after = text.slice(match.index + raw.length);
    const started = performance.now();

    (function frame(now) {
      const t = Math.min(1, (now - started) / 620);
      if (t >= 1) { el.textContent = text; return; }
      const eased = 1 - Math.pow(1 - t, 3);
      el.textContent = before + (target * eased).toLocaleString('en-US', {
        minimumFractionDigits: decimals, maximumFractionDigits: decimals
      }) + after;
      requestAnimationFrame(frame);
    })(started);
  }

  /**
   * The headline row of a report: a few figures set large, with an optional sparkline beside them.
   * Figures are `{ value, label, context, tone }`; `options.spark` is the series to draw and
   * `options.sparkLabel` / `options.sparkRange` caption it.
   */
  RPA.renderHero = function (elId, figures, options) {
    const el = document.getElementById(elId);
    if (!el) return;
    const opts = options || {};

    const figuresHtml = figures.map(f =>
      '<div class="hero-fig">' +
        '<div class="hero-value ' + (f.tone || '') + '" data-value="' + RPA.escapeHtml(f.value) + '">' +
          RPA.escapeHtml(f.value) +
        '</div>' +
        '<div class="hero-label">' + RPA.escapeHtml(f.label) + '</div>' +
        (f.context ? '<div class="hero-context">' + RPA.escapeHtml(f.context) + '</div>' : '') +
      '</div>').join('');

    el.innerHTML =
      '<div class="hero-figs">' + figuresHtml + '</div>' +
      (opts.spark && opts.spark.length > 1
        ? '<div class="hero-spark">' + sparkSvg(opts.spark) +
            '<div class="hero-spark-foot">' +
              '<span>' + RPA.escapeHtml(opts.sparkLabel || '') + '</span>' +
              '<span>' + RPA.escapeHtml(opts.sparkRange || '') + '</span>' +
            '</div>' +
          '</div>'
        : '');

    el.querySelectorAll('.hero-value').forEach(node => countUp(node, node.dataset.value));
  };

  /**
   * A sparkline as inline SVG rather than a chart: no instance to create and destroy on every
   * re-filter, and it stays sharp at any width. The stroke is exempted from the viewBox scaling,
   * which is what lets the box stretch without the line thickening with it.
   */
  function sparkSvg(values) {
    const w = 240;
    const h = 56;
    const max = Math.max.apply(null, values);
    const min = Math.min.apply(null, values);
    const span = max - min || 1;
    const step = values.length > 1 ? w / (values.length - 1) : w;

    const points = values.map((v, i) => {
      const x = +(i * step).toFixed(2);
      const y = +(h - ((v - min) / span) * (h - 6) - 3).toFixed(2);
      return x + ',' + y;
    });

    const last = points[points.length - 1].split(',');
    return '<svg viewBox="0 0 ' + w + ' ' + h + '" preserveAspectRatio="none" aria-hidden="true">' +
      '<polygon class="spark-area" points="0,' + h + ' ' + points.join(' ') + ' ' + w + ',' + h + '" />' +
      '<polyline class="spark-line" points="' + points.join(' ') + '" vector-effect="non-scaling-stroke" />' +
      '<circle class="spark-dot" cx="' + last[0] + '" cy="' + last[1] + '" r="2.5" vector-effect="non-scaling-stroke" />' +
      '</svg>';
  }

  /** Legend for a chart, as HTML: the page's own type, and the value beside the label. */
  RPA.chartLegend = function (elId, items) {
    const el = document.getElementById(elId);
    if (!el) return;
    el.innerHTML = items.map(item =>
      '<span class="legend-item">' +
        '<span class="legend-dot" style="background:' + RPA.escapeHtml(item.color) + '"></span>' +
        '<span class="legend-name" title="' + RPA.escapeHtml(item.label) + '">' +
          RPA.escapeHtml(item.label) + '</span>' +
        (item.value ? '<span class="legend-value">' + RPA.escapeHtml(item.value) + '</span>' : '') +
      '</span>').join('');
  };

  /**
   * Builds the section index for a long report out of the sections themselves: every element inside
   * `rootId` carrying `data-section` becomes a chip, numbered in document order — the same numbers
   * the headings print via a CSS counter, so the two can never disagree.
   */
  RPA.initSectionNav = function (navId, rootId) {
    const nav = document.getElementById(navId);
    const root = document.getElementById(rootId);
    if (!nav || !root) return;

    const sections = Array.prototype.slice.call(root.querySelectorAll('[data-section][id]'));
    if (!sections.length) { nav.innerHTML = ''; return; }

    nav.innerHTML = sections.map((section, i) =>
      '<button type="button" class="section-chip" data-target="' + section.id + '">' +
        '<span class="n">' + String(i + 1).padStart(2, '0') + '</span>' +
        RPA.escapeHtml(section.dataset.section) +
      '</button>').join('');

    nav.onclick = function (event) {
      const chip = event.target.closest('[data-target]');
      if (!chip) return;
      const target = document.getElementById(chip.dataset.target);
      if (target) target.scrollIntoView({ block: 'start', behavior: RPA.reducedMotion() ? 'auto' : 'smooth' });
    };

    // Which section is being read = the last one whose heading has passed under the control deck.
    const chips = Array.prototype.slice.call(nav.querySelectorAll('.section-chip'));
    let queued = false;
    function sync() {
      queued = false;
      let active = 0;
      sections.forEach((section, i) => {
        if (section.getBoundingClientRect().top <= 150) active = i;
      });
      chips.forEach((chip, i) => chip.classList.toggle('is-active', i === active));
    }

    if (nav._rpaScroll) window.removeEventListener('scroll', nav._rpaScroll);
    nav._rpaScroll = function () {
      if (queued) return;
      queued = true;
      requestAnimationFrame(sync);
    };
    window.addEventListener('scroll', nav._rpaScroll, { passive: true });
    sync();
  };

  /** Plays the entrance cascade once per report — a re-filter redraws the same page, it does not
   *  re-introduce it. */
  RPA.revealResults = function (elId) {
    const el = document.getElementById(elId);
    if (!el || el.dataset.revealed === 'yes' || RPA.reducedMotion()) return;
    el.dataset.revealed = 'yes';

    Array.prototype.slice.call(el.children).forEach((child, i) => {
      child.style.setProperty('--d', Math.min(i, 14) * 45 + 'ms');
    });
    el.classList.add('is-entering');
    setTimeout(() => el.classList.remove('is-entering'), 1600);
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

  /** Fills a <datalist> with the distinct values of a column, so its input types ahead. */
  RPA.fillDatalist = function (listId, values) {
    const list = document.getElementById(listId);
    if (!list) return;
    list.innerHTML = Array.from(new Set(values.filter(Boolean)))
      .sort((a, b) => a.localeCompare(b, 'tr'))
      .map(v => '<option value="' + RPA.escapeHtml(v) + '"></option>')
      .join('');
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
    // Lookup rather than a report: joins the Oracle case list to the orders export by order number.
    'ticket-seller': { tab: 'tab-ticket-seller', panel: 'panel-ticket-seller' },
    // Browser automation rather than a report: writes to Mirakl instead of reading a file.
    'create-return': { tab: 'tab-create-return', panel: 'panel-create-return' },
    // Also drives Mirakl, but the input is nothing more than a list of order IDs.
    'mark-received': { tab: 'tab-mark-received', panel: 'panel-mark-received' },
    // Reads an export like the reports do, but its output is messages to external parties.
    'late-orders': { tab: 'tab-late-orders', panel: 'panel-late-orders' },
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
