/* =============================================================================
   POS Reconciliation.

   Bulut Tahsilat's own "Sipariş Numarası" comes back blank for Akbank (and a
   few other rows) -> backfilled from Craftgate by joining "Provizyon No"
   against "authCode" and taking "externalId". The merged data is then
   pivoted: rows = POS Banka, columns = Taksit, values = İşlem Tutarı and
   Toplam Komisyon Tutarı summed.

   Mirakl is optional: when uploaded, its per-order Amount becomes Marketplace
   GMV. Only Bulut Tahsilat and Craftgate are required to generate.

   Section 3 edits the stored bank rate table and the "on us" family rules
   (GET/PUT /api/pos-reconciliation/rates). Rates are shown as percentages and
   stored as fractions.
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
    if (s.totalMarketplaceGmv != null) {
      items.push(['Marketplace GMV', RPA.fmtMoney(s.totalMarketplaceGmv, 'TRY'), '']);
      items.push(['Retail GMV', RPA.fmtMoney(s.totalRetailGmv, 'TRY'), '']);
    }

    RPA.renderKpis('pr-kpis', items);
    RPA.resetDataTables();
    RPA.renderDataTable('pr-pivot-table', data.pivot.rows, pivotColumns(data.pivot.labels), 'No records were found.');
    RPA.renderDataTable('pr-unmatched-table', data.unmatched, unmatchedColumns(), 'Every row was matched.');

    el('pr-unmatched-count').textContent = RPA.fmtInt(data.unmatched.length);
    el('pr-results').hidden = false;
  }

  // ---------------------------------------------------------------------------
  // Bank rates + on us rules editor
  // ---------------------------------------------------------------------------

  let rates = { banks: [], onUsRules: [] };

  async function ratesRequest(method, url, body) {
    const init = { method: method };
    if (body !== undefined) {
      init.headers = { 'Content-Type': 'application/json' };
      init.body = JSON.stringify(body);
    }
    const response = await fetch(url, init);
    let payload = null;
    try { payload = await response.json(); } catch (e) { /* non-JSON error body */ }
    if (!response.ok || !payload || payload.success === false)
      throw new Error((payload && payload.message) || 'The request failed.');
    return payload;
  }

  function toPercent(fraction) {
    return fraction == null ? '' : String(Number((fraction * 100).toFixed(6)));
  }

  function fromPercent(text) {
    const trimmed = String(text).trim().replace(',', '.');
    if (trimmed === '') return null;
    const value = Number(trimmed);
    if (Number.isNaN(value)) throw new Error('"' + text + '" is not a valid rate.');
    return value / 100;
  }

  function installmentCounts() {
    const set = new Set();
    rates.banks.forEach(b => b.installments.forEach(i => set.add(i.installment)));
    return Array.from(set).sort((a, b) => a - b);
  }

  function rateInput(bankIndex, field, value) {
    return '<input type="text" inputmode="decimal" size="7" data-bank="' + bankIndex + '" data-field="' + field +
      '" value="' + RPA.escapeHtml(toPercent(value)) + '" aria-label="' + field + '" />';
  }

  function renderRates() {
    const counts = installmentCounts();
    let html = '<table class="data-table"><thead><tr>' +
      '<th>POS Banka</th><th>Foreign Card %</th><th>1 taksit on us debit %</th>' +
      '<th>1 taksit on us credit %</th><th>1 taksit not on us debit %</th>' +
      '<th>1 taksit not on us credit %</th>';
    counts.forEach(n => { html += '<th>' + n + ' taksit %</th>'; });
    html += '<th></th></tr></thead><tbody>';

    rates.banks.forEach(function (bank, i) {
      html += '<tr><td><input type="text" size="18" data-bank="' + i + '" data-field="posBank" value="' +
        RPA.escapeHtml(bank.posBank) + '" aria-label="POS Banka" /></td>' +
        '<td>' + rateInput(i, 'foreignRate', bank.foreignRate) + '</td>' +
        '<td>' + rateInput(i, 'oneOnUsDebit', bank.oneOnUsDebit) + '</td>' +
        '<td>' + rateInput(i, 'oneOnUsCredit', bank.oneOnUsCredit) + '</td>' +
        '<td>' + rateInput(i, 'oneNotOnUsDebit', bank.oneNotOnUsDebit) + '</td>' +
        '<td>' + rateInput(i, 'oneNotOnUsCredit', bank.oneNotOnUsCredit) + '</td>';
      counts.forEach(function (n) {
        const found = bank.installments.find(x => x.installment === n);
        html += '<td>' + rateInput(i, 'inst:' + n, found ? found.rate : null) + '</td>';
      });
      html += '<td><button type="button" class="btn btn-ghost" data-remove-bank="' + i + '">Remove</button></td></tr>';
    });

    html += '</tbody></table>';
    el('pr-rates-table').innerHTML = html;
  }

  function renderOnUs() {
    let html = '<table class="data-table"><thead><tr><th>POS Banka</th>' +
      '<th>Also on us (card banks, comma separated)</th><th></th></tr></thead><tbody>';
    rates.onUsRules.forEach(function (rule, i) {
      html += '<tr><td><input type="text" size="18" data-rule="' + i + '" data-field="posBank" value="' +
        RPA.escapeHtml(rule.posBank) + '" aria-label="POS Banka" /></td>' +
        '<td><input type="text" size="50" data-rule="' + i + '" data-field="cardBanks" value="' +
        RPA.escapeHtml(rule.cardBanks.join(', ')) + '" aria-label="Card banks" /></td>' +
        '<td><button type="button" class="btn btn-ghost" data-remove-rule="' + i + '">Remove</button></td></tr>';
    });
    html += '</tbody></table>';
    el('pr-onus-table').innerHTML = html;
  }

  function renderAll() {
    renderRates();
    renderOnUs();
  }

  /** Reads every input back into `rates`; throws on an unparsable rate. */
  function collectRates() {
    el('pr-rates-table').querySelectorAll('input').forEach(function (input) {
      const bank = rates.banks[Number(input.dataset.bank)];
      const field = input.dataset.field;
      if (field === 'posBank') { bank.posBank = input.value; return; }
      const value = fromPercent(input.value);
      if (field.startsWith('inst:')) {
        const n = Number(field.slice(5));
        const existing = bank.installments.find(x => x.installment === n);
        if (existing) existing.rate = value;
        else if (value != null) bank.installments.push({ installment: n, rate: value });
      } else {
        bank[field] = value;
      }
    });

    el('pr-onus-table').querySelectorAll('input').forEach(function (input) {
      const rule = rates.onUsRules[Number(input.dataset.rule)];
      if (input.dataset.field === 'posBank') rule.posBank = input.value;
      else rule.cardBanks = input.value.split(',').map(s => s.trim()).filter(s => s.length > 0);
    });
  }

  function setRatesStatus(text) { el('pr-rates-status').textContent = text || ''; }

  async function loadRates() {
    try {
      rates = (await ratesRequest('GET', '/api/pos-reconciliation/rates')).data;
      renderAll();
    } catch (err) {
      RPA.showError('pr-rates-alert', err.message);
    }
  }

  function initRatesEditor() {
    el('pr-rates-table').addEventListener('click', function (e) {
      const remove = e.target.closest('[data-remove-bank]');
      if (!remove) return;
      try { collectRates(); } catch (err) { RPA.showError('pr-rates-alert', err.message); return; }
      rates.banks.splice(Number(remove.dataset.removeBank), 1);
      renderAll();
    });

    el('pr-onus-table').addEventListener('click', function (e) {
      const remove = e.target.closest('[data-remove-rule]');
      if (!remove) return;
      try { collectRates(); } catch (err) { RPA.showError('pr-rates-alert', err.message); return; }
      rates.onUsRules.splice(Number(remove.dataset.removeRule), 1);
      renderAll();
    });

    el('pr-rates-add-bank').addEventListener('click', function () {
      try { collectRates(); } catch (err) { RPA.showError('pr-rates-alert', err.message); return; }
      rates.banks.push({
        posBank: '', foreignRate: null, oneOnUsDebit: null, oneOnUsCredit: null, oneNotOnUsDebit: null,
        oneNotOnUsCredit: null,
        installments: []
      });
      renderAll();
    });

    el('pr-rates-add-installment').addEventListener('click', function () {
      try { collectRates(); } catch (err) { RPA.showError('pr-rates-alert', err.message); return; }
      const answer = window.prompt('Installment count for the new column (e.g. 6):');
      const n = Number(answer);
      if (!answer || !Number.isInteger(n) || n < 2) return;
      rates.banks.forEach(function (b) {
        if (!b.installments.some(x => x.installment === n)) b.installments.push({ installment: n, rate: null });
      });
      renderAll();
    });

    el('pr-onus-add').addEventListener('click', function () {
      try { collectRates(); } catch (err) { RPA.showError('pr-rates-alert', err.message); return; }
      rates.onUsRules.push({ posBank: '', cardBanks: [] });
      renderAll();
    });

    el('pr-rates-save').addEventListener('click', async function () {
      RPA.clearError('pr-rates-alert');
      setRatesStatus('');
      const button = el('pr-rates-save');
      RPA.setBusy(button, true, 'Saving…');
      try {
        collectRates();
        const body = await ratesRequest('PUT', '/api/pos-reconciliation/rates', rates);
        rates = body.data;
        renderAll();
        setRatesStatus('Saved ' + new Date().toLocaleTimeString() + '. The next download uses these rates.');
      } catch (err) {
        RPA.showError('pr-rates-alert', err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    el('pr-rates-export').addEventListener('click', async function () {
      RPA.clearError('pr-rates-alert');
      try {
        await RPA.postDownloadJson('/api/pos-reconciliation/rates/excel', {}, 'pos_banka_oranlar.xlsx');
      } catch (err) {
        RPA.showError('pr-rates-alert', err.message);
      }
    });

    el('pr-rates-import').addEventListener('change', async function (e) {
      const file = e.target.files[0];
      e.target.value = '';
      if (!file) return;
      RPA.clearError('pr-rates-alert');
      try {
        const form = new FormData();
        form.append('file', file);
        const body = await RPA.postJson('/api/pos-reconciliation/rates/import', form);
        rates = body.data;
        renderAll();
        setRatesStatus(body.message);
      } catch (err) {
        RPA.showError('pr-rates-alert', err.message);
      }
    });

    loadRates();
  }

  document.addEventListener('DOMContentLoaded', function () {
    initRatesEditor();

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
