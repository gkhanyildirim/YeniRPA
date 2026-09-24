/* =============================================================================
   Cargo Seller Report.

   Upload the cargo-invoice export -> it is analyzed as soon as it is chosen, to
   find the tracking-code and seller columns. If the seller column cannot be
   told apart from the file's headers, a manual column picker appears and
   choosing a column re-runs analyze with that column forced.

   Unlike a per-seller lookup, "Generate Report" processes every seller in the
   file in one pass (this runs once a month over the whole file) and the
   result is a single .zip: one workbook per seller plus one overview
   workbook. The cargo file is re-uploaded at generate time alongside the
   other two — there is no server-side cache between analyze and generate, the
   same way a small file upload does not need one anywhere else in this app.
   ============================================================================= */

(function (RPA) {
  'use strict';

  let kargoAnalysis = null; // last successful /analyze response.data
  let batchId = null;

  function el(id) { return document.getElementById(id); }

  // ---------------------------------------------------------------------------
  // Ready state
  // ---------------------------------------------------------------------------

  function filesChosen() {
    return el('csr-kargo-file').files.length > 0 &&
      el('csr-marketplace-file').files.length > 0 &&
      el('csr-mm-file').files.length > 0;
  }

  function updateGenerateButton() {
    el('csr-generate').disabled = !(filesChosen() && !!kargoAnalysis && kargoAnalysis.sellerColumnResolved);
  }

  // ---------------------------------------------------------------------------
  // Analyze
  // ---------------------------------------------------------------------------

  function showColumnPicker(headers) {
    const select = el('csr-seller-column-select');
    select.innerHTML = '<option value="">Choose a column&hellip;</option>' +
      headers.map(function (h) { return '<option value="' + RPA.escapeHtml(h) + '">' + RPA.escapeHtml(h) + '</option>'; }).join('');
    el('csr-seller-column-field').hidden = false;
    el('csr-seller-note').hidden = true;
  }

  function showSellerNote(analysis) {
    el('csr-seller-column-field').hidden = true;
    el('csr-seller-note').hidden = false;
    el('csr-seller-note').textContent =
      'Seller column: ' + analysis.sellerColumn + ' · tracking column: ' + analysis.trackingColumn +
      ' · ' + RPA.fmtInt(analysis.sellers.length) + ' seller(s) found.';
  }

  async function runAnalyze(sellerColumnOverride) {
    const file = el('csr-kargo-file').files[0];
    if (!file) return;

    RPA.clearError('csr-alert');
    kargoAnalysis = null;
    updateGenerateButton();

    const status = el('csr-analyze-status');
    status.hidden = false;
    status.textContent = 'Reading the cargo invoice file…';

    const form = new FormData();
    form.append('kargoFile', file);
    if (sellerColumnOverride) form.append('sellerColumn', sellerColumnOverride);

    try {
      const body = await RPA.postJson('/api/cargo-seller-report/analyze', form);
      kargoAnalysis = body.data;
      status.hidden = true;

      if (kargoAnalysis.sellerColumnResolved) {
        showSellerNote(kargoAnalysis);
      } else {
        showColumnPicker(kargoAnalysis.headers);
      }
    } catch (err) {
      status.hidden = true;
      el('csr-seller-note').hidden = true;
      el('csr-seller-column-field').hidden = true;
      RPA.showError('csr-alert', err.message);
    }

    updateGenerateButton();
  }

  // ---------------------------------------------------------------------------
  // Generate + download
  // ---------------------------------------------------------------------------

  function sellerColumns() {
    return [
      { label: 'Seller', filter: 'text', value: r => r.sellerName, render: r => RPA.escapeHtml(r.sellerName) },
      { label: 'Records', value: r => r.totalRecords, render: r => RPA.fmtInt(r.totalRecords) },
      { label: 'Matched', value: r => r.totalMatched, render: r => RPA.fmtInt(r.totalMatched) },
      {
        label: 'Unmatched', value: r => r.unmatched,
        render: r => r.unmatched ? '<span class="badge amber">' + RPA.fmtInt(r.unmatched) + '</span>' : RPA.fmtInt(r.unmatched)
      },
      {
        label: 'Conflicted', value: r => r.conflicted,
        render: r => r.conflicted ? '<span class="badge red">' + RPA.fmtInt(r.conflicted) + '</span>' : RPA.fmtInt(r.conflicted)
      }
    ];
  }

  function renderSummary(summary) {
    const items = [
      ['Sellers processed', RPA.fmtInt(summary.sellerCount), ''],
      ['Total cargo records', RPA.fmtInt(summary.totalRecords), ''],
      ['Matched via Marketplace', RPA.fmtInt(summary.matchedViaMarketplace), 'green'],
      ['Matched via MM Pazaryeri Kargo Datası', RPA.fmtInt(summary.matchedViaMmCargoData), 'green'],
      ['Total matched', RPA.fmtInt(summary.totalMatched), 'green'],
      ['Unmatched', RPA.fmtInt(summary.unmatched), summary.unmatched ? 'amber' : 'green'],
      ['Conflicted', RPA.fmtInt(summary.conflicted), summary.conflicted ? 'red' : 'green']
    ];

    RPA.renderKpis('csr-kpis', items);
    RPA.resetDataTables();
    RPA.renderDataTable('csr-seller-table', summary.sellers, sellerColumns(), 'No sellers were found in the file.');
    el('csr-summary').hidden = false;
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('csr-kargo-drop', 'csr-kargo-file');
    RPA.initDropzone('csr-marketplace-drop', 'csr-marketplace-file');
    RPA.initDropzone('csr-mm-drop', 'csr-mm-file');

    el('csr-kargo-file').addEventListener('change', function () {
      batchId = null;
      el('csr-summary').hidden = true;
      if (el('csr-kargo-file').files.length > 0) runAnalyze(null);
      updateGenerateButton();
    });

    el('csr-marketplace-file').addEventListener('change', updateGenerateButton);
    el('csr-mm-file').addEventListener('change', updateGenerateButton);

    el('csr-seller-column-select').addEventListener('change', function () {
      const column = el('csr-seller-column-select').value;
      if (column) runAnalyze(column);
    });

    el('csr-generate').addEventListener('click', async function () {
      if (!kargoAnalysis || !kargoAnalysis.sellerColumnResolved) return;

      RPA.clearError('csr-alert');
      el('csr-summary').hidden = true;
      batchId = null;

      const form = new FormData();
      form.append('kargoFile', el('csr-kargo-file').files[0]);
      form.append('marketplaceFile', el('csr-marketplace-file').files[0]);
      form.append('mmFile', el('csr-mm-file').files[0]);
      form.append('sellerColumn', kargoAnalysis.sellerColumn);

      const button = el('csr-generate');
      RPA.setBusy(button, true, 'Generating…');
      try {
        const body = await RPA.postJson('/api/cargo-seller-report/generate', form);
        batchId = body.data.batchId;
        renderSummary(body.data.summary);
      } catch (err) {
        RPA.showError('csr-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    el('csr-download').addEventListener('click', async function () {
      if (!batchId) return;
      RPA.clearError('csr-alert');

      const button = el('csr-download');
      RPA.setBusy(button, true, 'Preparing…');
      try {
        await RPA.postDownloadJson('/api/cargo-seller-report/download', { batchId: batchId }, 'kargo_raporlari.zip');
      } catch (err) {
        RPA.showError('csr-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });
  });

})(window.RPA);
