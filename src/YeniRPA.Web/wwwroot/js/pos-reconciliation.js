/* =============================================================================
   POS Reconciliation.

   Bulut Tahsilat's own "Sipariş Numarası" comes back blank for Akbank (and a
   few other rows) -> backfilled from Craftgate by joining "Provizyon No"
   against "authCode" and taking "externalId". The merged data is then
   pivoted: rows = POS Banka, columns = Taksit, values = İşlem Tutarı and
   Toplam Komisyon Tutarı summed.

   Mirakl is uploaded here too (the operator pulls all 3 platforms together)
   but is not read yet - its role in this merge isn't defined. Only Bulut
   Tahsilat and Craftgate are required to generate.
   ============================================================================= */

(function (RPA) {
  'use strict';

  let batchId = null;

  function el(id) { return document.getElementById(id); }

  function filesReady() {
    return el('pr-bulut-file').files.length > 0 && el('pr-craftgate-file').files.length > 0;
  }

  function updateGenerateButton() {
    el('pr-generate').disabled = !filesReady();
  }

  // ---------------------------------------------------------------------------
  // Pivot table (rows = POS Banka, two columns per Taksit label: Tutar / Komisyon)
  // ---------------------------------------------------------------------------

  function pivotColumns(labels) {
    const columns = [{
      label: 'POS Banka',
      filter: 'text',
      value: r => r.posBanka,
      render: r => RPA.escapeHtml(r.posBanka)
    }];

    labels.forEach(function (label, index) {
      columns.push({
        label: label + ' · Tutar',
        numeric: true,
        value: r => r.cells[index].islemTutari,
        render: r => RPA.fmtMoney(r.cells[index].islemTutari, 'TRY')
      });
      columns.push({
        label: label + ' · Komisyon',
        numeric: true,
        value: r => r.cells[index].toplamKomisyonTutari,
        render: r => RPA.fmtMoney(r.cells[index].toplamKomisyonTutari, 'TRY')
      });
    });

    return columns;
  }

  function unmatchedColumns() {
    return [
      { label: 'Provizyon No', filter: 'text', value: r => r.provizyonNo, render: r => RPA.escapeHtml(r.provizyonNo) },
      { label: 'POS Banka', filter: 'text', value: r => r.posBanka, render: r => RPA.escapeHtml(r.posBanka) },
      { label: 'Kart Banka', filter: 'text', value: r => r.kartBanka, render: r => RPA.escapeHtml(r.kartBanka) },
      { label: 'İşlem Tutarı', numeric: true, value: r => r.islemTutari, render: r => RPA.fmtMoney(r.islemTutari, 'TRY') },
      { label: 'Taksit', numeric: true, value: r => r.taksit, render: r => RPA.fmtInt(r.taksit) },
      { label: 'Neden', filter: 'text', value: r => r.reason, render: r => RPA.escapeHtml(r.reason) }
    ];
  }

  function renderResult(data) {
    const s = data.summary;
    const items = [
      ['Toplam kayıt', RPA.fmtInt(s.totalRecords), ''],
      ['Zaten sipariş numarası vardı', RPA.fmtInt(s.originalOrderNumbers), ''],
      ['Craftgate ile eşleşti', RPA.fmtInt(s.backfilledViaCraftgate), 'green'],
      ['Eşleşmeyen', RPA.fmtInt(s.unmatched), s.unmatched ? 'amber' : 'green'],
      ['Çakışmalı', RPA.fmtInt(s.conflicted), s.conflicted ? 'red' : 'green'],
      ['Toplam İşlem Tutarı', RPA.fmtMoney(s.totalIslemTutari, 'TRY'), ''],
      ['Toplam Komisyon Tutarı', RPA.fmtMoney(s.totalKomisyonTutari, 'TRY'), '']
    ];

    RPA.renderKpis('pr-kpis', items);
    RPA.resetDataTables();
    RPA.renderDataTable('pr-pivot-table', data.pivot.rows, pivotColumns(data.pivot.labels), 'No records were found.');
    RPA.renderDataTable('pr-unmatched-table', data.unmatched, unmatchedColumns(), 'Every row was matched.');

    el('pr-unmatched-count').textContent = RPA.fmtInt(data.unmatched.length);
    el('pr-results').hidden = false;
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('pr-bulut-drop', 'pr-bulut-file');
    RPA.initDropzone('pr-craftgate-drop', 'pr-craftgate-file');
    RPA.initDropzone('pr-mirakl-drop', 'pr-mirakl-file');

    el('pr-bulut-file').addEventListener('change', updateGenerateButton);
    el('pr-craftgate-file').addEventListener('change', updateGenerateButton);

    el('pr-generate').addEventListener('click', async function () {
      if (!filesReady()) return;

      RPA.clearError('pr-alert');
      el('pr-results').hidden = true;
      batchId = null;

      const form = new FormData();
      form.append('bulutTahsilatFile', el('pr-bulut-file').files[0]);
      form.append('craftgateFile', el('pr-craftgate-file').files[0]);
      if (el('pr-mirakl-file').files.length > 0) form.append('miraklFile', el('pr-mirakl-file').files[0]);

      const button = el('pr-generate');
      RPA.setBusy(button, true, 'Oluşturuluyor…');
      try {
        const body = await RPA.postJson('/api/pos-reconciliation/generate', form);
        batchId = body.data.batchId;
        renderResult(body.data);
      } catch (err) {
        RPA.showError('pr-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    el('pr-download').addEventListener('click', async function () {
      if (!batchId) return;
      RPA.clearError('pr-alert');

      const button = el('pr-download');
      RPA.setBusy(button, true, 'Hazırlanıyor…');
      try {
        await RPA.postDownloadJson('/api/pos-reconciliation/download', { batchId: batchId }, 'pos_mutabakat.xlsx');
      } catch (err) {
        RPA.showError('pr-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });
  });

})(window.RPA);
