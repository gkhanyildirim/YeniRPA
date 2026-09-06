/* =============================================================================
   Settings — backup/restore for this install's LiteDB-backed data (the seller/
   WhatsApp group mapping, Title Cleaner's rule sets and reference lists, the
   offer/VAT warning templates).

   Export is a plain download link — the browser handles it, nothing to wire up
   here. Import posts the chosen file and reads back { success, message, data },
   the envelope new endpoints in this app use (see CLAUDE.md); the shared
   RPA.postJson/readError pair expects the older { error } shape the rest of the
   app's endpoints carry, so this module talks to its own endpoint directly
   instead of forcing that mismatch.
   ============================================================================= */

(function (RPA) {
  'use strict';

  const MODULE = 'settings';

  function el(id) { return document.getElementById(id); }

  function showSuccess(message) {
    RPA.clearError('settings-error');
    const box = el('settings-success');
    box.querySelector('.msg').textContent = message;
    box.classList.add('is-shown');
  }

  function clearSuccess() {
    el('settings-success').classList.remove('is-shown');
  }

  function showError(message) {
    clearSuccess();
    RPA.showError('settings-error', message);
  }

  async function importDatabase(file) {
    const form = new FormData();
    form.append('file', file);

    const response = await fetch('/Settings/ImportDatabase', { method: 'POST', body: form });

    let body;
    try {
      body = await response.json();
    } catch (e) {
      throw new Error('Request failed with status ' + response.status + '.');
    }

    if (!body || body.success !== true) {
      throw new Error((body && body.message) || 'Import failed.');
    }

    return body;
  }

  function activate() {
    // Nothing to load on first view — Export is a static link and Import starts empty.
  }

  document.addEventListener('DOMContentLoaded', function () {
    RPA.initDropzone('settings-drop', 'settings-import-file');

    el('settings-import-file').addEventListener('change', function () {
      el('settings-import-btn').disabled = el('settings-import-file').files.length === 0;
      RPA.clearError('settings-error');
      clearSuccess();
    });

    el('settings-import-btn').addEventListener('click', async function () {
      const file = el('settings-import-file').files[0];
      if (!file) return;

      if (!window.confirm(
        'Import ' + file.name + '? Every section this backup carries replaces this install’s ' +
        'current one — this cannot be undone from here.'))
        return;

      const button = el('settings-import-btn');
      RPA.clearError('settings-error');
      clearSuccess();
      RPA.setBusy(button, true, 'İçe aktarılıyor…');

      try {
        const body = await importDatabase(file);
        showSuccess(body.message);
        el('settings-import-file').value = '';
        el('settings-import-btn').disabled = true;
        el('settings-drop').classList.remove('has-file');
      } catch (err) {
        showError(err.message);
      } finally {
        RPA.setBusy(button, false);
      }
    });

    document.addEventListener('rpa:modulechange', function (event) {
      if (event.detail.module === MODULE) activate();
    });

    if (el('tab-settings').getAttribute('aria-selected') === 'true') activate();
  });

})(window.RPA);
