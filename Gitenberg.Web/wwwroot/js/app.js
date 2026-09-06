// Gitenberg Mini App: SPA state, screen routing (Register / Explorer / Editor)
// and Telegram WebApp integration.

import * as api from './api.js';
import * as editor from './editor.js';
import { getVaultIndex, invalidateVaultIndex } from './vault.js';
import { initI18n, t, getLang, setLang, applyStatic } from './i18n.js';

// ---------------------------------------------------------------------------
// UI strings (i18n: RU/EN, auto-detected on first run)
// ---------------------------------------------------------------------------

// Dynamic proxy: every STRINGS.x reads the current language at call time.
const STRINGS = new Proxy({}, { get: (_, key) => t(key) });

const FOLDER_ICON = '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M10 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-2z"/></svg>';
const FILE_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M9 13h6M9 17h6"/></svg>';
const TRASH_ICON = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/></svg>';
const KEBAB_ICON = '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="5" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="12" cy="19" r="2"/></svg>';

// ---------------------------------------------------------------------------
// Telegram helpers
// ---------------------------------------------------------------------------

const tg = () => window.Telegram?.WebApp ?? null;

function haptic(type, ...args) {
  try {
    if (type === 'success' || type === 'error' || type === 'warning') {
      tg()?.HapticFeedback?.notificationOccurred(type);
    } else if (type === 'select') {
      tg()?.HapticFeedback?.selectionChanged();
    } else if (type === 'impact') {
      tg()?.HapticFeedback?.impactOccurred(args[0] ?? 'light');
    }
  } catch {
    /* haptics unavailable */
  }
}

// Russian plural: plural(1, 'элемент', 'элемента', 'элементов') → 'элемент'.
function plural(n, one, few, many) {
  const mod100 = Math.abs(n) % 100;
  const mod10 = mod100 % 10;
  if (mod100 >= 11 && mod100 <= 14) return many;
  if (mod10 === 1) return one;
  if (mod10 >= 2 && mod10 <= 4) return few;
  return many;
}

function showConfirm(message) {
  const webApp = tg();
  // Outside Telegram the SDK still exposes a WebApp stub whose showConfirm
  // throws WebAppMethodUnsupported — detect the stub and fall back to
  // window.confirm, otherwise deletion would silently hang on an
  // unresolved promise.
  if (webApp?.showConfirm && webApp.platform !== 'unknown') {
    try {
      return new Promise((resolve) => webApp.showConfirm(message, resolve));
    } catch {
      /* fall through to window.confirm */
    }
  }
  return Promise.resolve(window.confirm(message));
}

// MainButton: editor-only "Сохранить". The click handler is swapped via
// offClick before every onClick so repeated open/save cycles never stack.
const mainButton = {
  show(label, onClick, options = {}) {
    const mb = tg()?.MainButton;
    if (!mb) return;
    if (typeof mb.offClick === 'function') mb.offClick(mainButton._handler);
    mainButton._handler = onClick;
    mb.setText(label);
    mb.onClick(onClick);
    if (options.color && mb.setParams) {
      mb.setParams({ color: options.color });
    }
    mb.show();
  },
  hide() {
    const mb = tg()?.MainButton;
    if (!mb) return;
    if (typeof mb.offClick === 'function' && mainButton._handler) {
      mb.offClick(mainButton._handler);
      mainButton._handler = null;
    }
    mb.hide();
  },
};

// BackButton: visible everywhere except the explorer root.
const backButton = {
  show(onClick) {
    const bb = tg()?.BackButton;
    if (!bb) return;
    if (typeof bb.offClick === 'function') bb.offClick(backButton._handler);
    backButton._handler = onClick;
    bb.onClick(onClick);
    bb.show();
  },
  hide() {
    const bb = tg()?.BackButton;
    if (!bb) return;
    if (typeof bb.offClick === 'function' && backButton._handler) {
      bb.offClick(backButton._handler);
      backButton._handler = null;
    }
    bb.hide();
  },
};

// ---------------------------------------------------------------------------
// DOM handles & shared state
// ---------------------------------------------------------------------------

const $ = (id) => document.getElementById(id);

const els = {
  devBar: $('dev-bar'),
  devTelegramId: $('dev-telegram-id'),
  views: {
    register: $('view-register'),
    explorer: $('view-explorer'),
    editor: $('view-editor'),
  },
  registerForm: $('register-form'),
  regToken: $('reg-token'),
  regOwner: $('reg-owner'),
  regRepo: $('reg-repo'),
  regInboxPath: $('reg-inbox-path'),
  regAttachmentsPath: $('reg-attachments-path'),
  registerError: $('register-error'),
  registerSubmit: $('register-submit'),
  explorerTitle: $('explorer-title'),
  btnCreateNote: $('btn-create-note'),
  fabCreate: $('fab-create'),
  breadcrumbs: $('breadcrumbs'),
  searchInput: $('search-input'),
  searchClear: $('search-clear'),
  searchFind: $('search-find'),
  searchResults: $('search-results'),
  notesList: $('notes-list'),
  explorerStatus: $('explorer-status'),
  editorTitle: $('editor-title'),
  btnEditorBack: $('btn-editor-back'),
  btnEditorDelete: $('btn-editor-delete'),
  editorStatus: $('editor-status'),
  notePath: $('note-path'),
  editorContainer: $('editor-container'),
  btnEditorSave: $('btn-editor-save'),
  toast: $('toast'),
  infoModal: $('info-modal'),
  infoTitle: $('info-title'),
  infoFields: $('info-fields'),
  infoGithubLink: $('info-github-link'),
  infoMove: $('info-move'),
  infoClose: $('info-close'),
  infoBackdrop: $('info-backdrop'),
  moveModal: $('move-modal'),
  moveFolder: $('move-folder'),
  moveNewFolderField: $('move-new-folder-field'),
  moveNewFolder: $('move-new-folder'),
  moveName: $('move-name'),
  moveError: $('move-error'),
  moveSubmit: $('move-submit'),
  moveCancel: $('move-cancel'),
  moveBackdrop: $('move-backdrop'),
  linkModal: $('link-modal'),
  linkSearch: $('link-search'),
  linkResults: $('link-results'),
  linkUrl: $('link-url'),
  linkError: $('link-error'),
  linkInsertUrl: $('link-insert-url'),
  linkCancel: $('link-cancel'),
  linkBackdrop: $('link-backdrop'),
  btnLang: $('btn-lang'),
  searchModal: $('search-modal'),
  searchModalResults: $('search-modal-results'),
  searchModalClear: $('search-modal-clear'),
  searchBackdrop: $('search-backdrop'),
  previewBanner: $('preview-banner'),
  previewBannerEdit: $('preview-banner-edit'),
  btnSync: $('btn-sync'),
  syncBadge: $('sync-badge'),
  btnSettings: $('btn-settings'),
  settingsOwner: $('settings-owner'),
  settingsRepo: $('settings-repo'),
  settingsToken: $('settings-token'),
  settingsInboxPath: $('settings-inbox-path'),
  settingsAttachmentsPath: $('settings-attachments-path'),
  settingsAutosave: $('settings-autosave'),
  settingsAutosync: $('settings-autosync'),
  settingsError: $('settings-error'),
  settingsSave: $('settings-save'),
  btnExport: $('btn-export'),
  settingsBack: $('btn-settings-back'),
  settingsAutosyncInterval: $('settings-autosync-interval'),
  autosyncIntervalField: $('autosync-interval-field'),
};

const state = {
  currentPath: '', // '' = repo root
  searchSeq: 0, // guards against out-of-order search responses
  infoItem: null, // item shown in the properties sheet
  infoItemIsDir: false,
  moveFrom: null, // path being moved/renamed
  moveOldDir: null,
  editor: {
    mode: 'create', // 'create' | 'edit'
    path: null, // original path in edit mode
    originalContent: null,
  },
};

// Unique #tags in a markdown text (headings excluded by construction: their #
// is followed by space or another #).
function extractTags(text) {
  const tags = new Set();
  const re = /(^|[\s>(])#([A-Za-zА-Яа-яЁё0-9][A-Za-zА-Яа-яЁё0-9_/-]*)/g;
  for (const match of text.matchAll(re)) {
    tags.add(match[2]);
    if (tags.size >= 20) break;
  }
  return [...tags];
}

els.views.settings = $('view-settings'); // extra view routed by showView

// ---------------------------------------------------------------------------
// Small UI utilities
// ---------------------------------------------------------------------------

function showView(name) {
  for (const [key, section] of Object.entries(els.views)) {
    section.hidden = key !== name;
  }
}

function setError(el, message) {
  el.textContent = message ?? '';
  el.hidden = !message;
}

function setButtonBusy(btn, busy, busyLabel, idleLabel) {
  btn.disabled = busy;
  btn.querySelector('.btn-label').textContent = busy ? busyLabel : idleLabel;
  let spinner = btn.querySelector('.spinner');
  if (busy && !spinner) {
    spinner = document.createElement('span');
    spinner.className = 'spinner';
    btn.prepend(spinner);
  } else if (!busy && spinner) {
    spinner.remove();
  }
}

let toastTimer = null;
function showToast(message, ms = 2200) {
  els.toast.textContent = message;
  els.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    els.toast.hidden = true;
  }, ms);
}

function showErrorToast(error) {
  let message = STRINGS.loadFailed;
  if (error instanceof Error && typeof error.message === 'string' && error.message.trim()) {
    message = error.message;
  } else if (typeof error === 'string' && error.trim()) {
    message = error;
  }
  showToast(`${STRINGS.errorPrefix}: ${message}`);
  haptic('error');
}

function escapeHtml(text) {
  return text
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

// Search snippets arrive with server-inserted <b>…</b> highlight markers.
// Escape everything first, then re-enable only those markers.
function renderSnippet(snippet) {
  return escapeHtml(snippet)
    .replaceAll('&lt;b&gt;', '<b>')
    .replaceAll('&lt;/b&gt;', '</b>');
}

function normPath(path) {
  return String(path ?? '').trim().replace(/^\/+/, '').replace(/\/{2,}/g, '/');
}

// ---------------------------------------------------------------------------
// Register view
// ---------------------------------------------------------------------------

function currentTelegramId() {
  const fromTg = tg()?.initDataUnsafe?.user?.id;
  if (Number.isFinite(fromTg) && fromTg > 0) return fromTg;
  return api.getDevTelegramId();
}

function syncDevBar() {
  els.devBar.hidden = api.isInsideTelegram();
  if (!els.devBar.hidden) {
    els.devTelegramId.value = String(api.getDevTelegramId());
  }
}

els.devTelegramId.addEventListener('change', () => {
  const value = Number(els.devTelegramId.value);
  if (!Number.isFinite(value) || value <= 0) {
    showToast(STRINGS.telegramIdInvalid);
    els.devTelegramId.value = String(api.getDevTelegramId());
    return;
  }
  api.setDevTelegramId(value);
  restartProbe();
});

els.registerForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  setError(els.registerError, '');

  const token = els.regToken.value.trim();
  const owner = els.regOwner.value.trim();
  const repo = els.regRepo.value.trim();
  const inboxPath = els.regInboxPath.value.trim().replace(/^\/+|\/+$/g, '');
  const attachmentsPath = els.regAttachmentsPath.value.trim().replace(/^\/+|\/+$/g, '');
  if (!token || !owner || !repo) {
    setError(els.registerError, STRINGS.fillAllFields);
    return;
  }

  setButtonBusy(els.registerSubmit, true, STRINGS.registerBtnBusy, STRINGS.registerBtnIdle);
  try {
    await api.registerUser(currentTelegramId(), token, owner, repo, {
      inboxPath: inboxPath || undefined,
      attachmentsPath: attachmentsPath || undefined,
    });
    haptic('success');
    await enterExplorer('');
  } catch (error) {
    setError(els.registerError, error instanceof api.ApiError ? error.message : STRINGS.registerFailed);
    haptic('error');
  } finally {
    setButtonBusy(els.registerSubmit, false, STRINGS.registerBtnBusy, STRINGS.registerBtnIdle);
  }
});

// ---------------------------------------------------------------------------
// Explorer view
// ---------------------------------------------------------------------------

function renderBreadcrumbs(path) {
  els.breadcrumbs.textContent = '';

  const root = document.createElement('button');
  root.className = path === '' ? 'crumb-current' : 'crumb';
  root.textContent = STRINGS.rootCrumb;
  root.addEventListener('click', () => enterExplorer(''));
  els.breadcrumbs.append(root);

  if (path === '') return;

  let accumulated = '';
  const segments = path.split('/').filter(Boolean);
  segments.forEach((segment, index) => {
    accumulated = accumulated ? `${accumulated}/${segment}` : segment;
    const isLast = index === segments.length - 1;

    els.breadcrumbs.append(Object.assign(document.createElement('span'), {
      className: 'crumb-sep',
      textContent: '/',
    }));

    const crumb = document.createElement('button');
    crumb.className = isLast ? 'crumb-current' : 'crumb';
    crumb.textContent = segment;
    if (!isLast) {
      const target = accumulated;
      crumb.addEventListener('click', () => enterExplorer(target));
    }
    els.breadcrumbs.append(crumb);
  });
}

function skeletonRows(count = 5) {
  els.notesList.textContent = '';
  for (let i = 0; i < count; i += 1) {
    const row = document.createElement('div');
    row.className = 'skeleton-row';
    row.innerHTML =
      '<span class="sk sk-icon"></span>' +
      '<span class="sk sk-line w60"></span>';
    els.notesList.append(row);
  }
}

function setStatus(message) {
  els.explorerStatus.textContent = message ?? '';
  els.explorerStatus.hidden = !message;
}

function renderNotes(items) {
  els.notesList.textContent = '';

  if (!items.length) {
    setStatus(STRINGS.emptyFolder);
    return;
  }
  setStatus('');

  const isDirectory = (item) => (item?.type || '').toLowerCase() === 'dir';

  const sorted = [...items].sort((a, b) => {
    const aDir = isDirectory(a) ? 0 : 1;
    const bDir = isDirectory(b) ? 0 : 1;
    if (aDir !== bDir) return aDir - bDir;
    return String(a.name).localeCompare(String(b.name), 'ru', { sensitivity: 'base' });
  });

  for (const item of sorted) {
    const isDir = isDirectory(item);
    const isMd = /\.md$/i.test(String(item.name));

    const row = document.createElement('button');
    row.type = 'button';
    row.className = `note-row ${isDir ? 'is-dir' : 'is-file'}`;
    row.dataset.path = item.path;

    const icon = document.createElement('span');
    icon.className = 'note-icon';
    // 📁 folders / 📄 files (SVG icons mirror the emoji affordance)
    icon.innerHTML = isDir ? FOLDER_ICON : FILE_ICON;
    icon.setAttribute('aria-label', isDir ? '📁' : '📄');

    const body = document.createElement('span');
    body.className = 'note-body';
    const name = document.createElement('span');
    name.className = 'note-name';
    name.textContent = item.name;
    const meta = document.createElement('span');
    meta.className = 'note-meta';
    meta.textContent = isDir ? 'Папка' : formatSize(item.size);
    body.append(name, meta);

    row.append(icon, body);

    const info = document.createElement('span');
    info.className = 'note-info';
    info.setAttribute('role', 'button');
    info.setAttribute('aria-label', `Свойства ${item.name}`);
    info.title = 'Свойства';
    info.innerHTML = KEBAB_ICON;
    info.addEventListener('click', (event) => {
      event.stopPropagation();
      openInfoModal(item, isDir);
    });
    row.append(info);

    const del = document.createElement('span');
    del.className = 'note-delete';
    del.setAttribute('role', 'button');
    del.setAttribute('aria-label', `Удалить ${item.name}`);
    del.title = 'Удалить';
    del.innerHTML = TRASH_ICON;
    del.addEventListener('click', (event) => {
      event.stopPropagation();
      void confirmAndDeleteItem(item.path, item.name, isDir);
    });
    row.append(del);

    if (isDir) {
      // Navigate into the folder (loadFolder + breadcrumbs via enterExplorer).
      row.addEventListener('click', () => {
        void enterExplorer(item.path);
      });
    } else if (isMd) {
      row.addEventListener('click', () => openEditor('edit', item.path));
    } else {
      // Non-markdown files are opened on GitHub instead of the editor.
      row.addEventListener('click', () => {
        if (item.htmlUrl) tg()?.openLink?.(item.htmlUrl);
      });
    }

    els.notesList.append(row);
  }

  if (sorted.some(isDirectory)) void loadFolderItemCounts(sorted.filter(isDirectory));
}

// GitHub reports no child count for directories, so fetch each folder's
// listing (cached server-side) and replace the meta line with the count.
async function loadFolderItemCounts(dirs) {
  await Promise.allSettled(dirs.map(async (item) => {
    let children;
    try {
      children = await api.listNotes(item.path);
    } catch {
      return; // keep the generic 'Папка' label on failure
    }
    const count = Array.isArray(children) ? children.length : 0;
    const row = els.notesList.querySelector(`.note-row[data-path="${CSS.escape(item.path)}"]`);
    const meta = row?.querySelector('.note-meta');
    if (meta && meta.textContent === 'Папка') {
      meta.textContent = STRINGS.folderItemsCount(count);
    }
  }));
}

function formatSize(size) {
  const n = Number(size) || 0;
  if (n < 1024) return `${n} Б`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} КБ`;
  return `${(n / (1024 * 1024)).toFixed(1)} МБ`;
}

async function loadFolder(path) {
  state.currentPath = path;
  renderBreadcrumbs(path);
  hideSearchResults();
  skeletonRows();
  setStatus('');
  updateBackButton();

  try {
    const items = await api.listNotes(path || undefined);
    if (state.currentPath !== path) return; // user navigated away meanwhile
    renderNotes(Array.isArray(items) ? items : []);
  } catch (error) {
    if (state.currentPath !== path) return;
    if (error instanceof api.ApiError && error.status === 404 && /not found/i.test(error.message)) {
      // Registered user missing (e.g. data reset) → back to registration.
      showView('register');
      return;
    }
    els.notesList.textContent = '';
    setStatus(`${STRINGS.loadFailed}`);
    showErrorToast(error);
  }
}

async function enterExplorer(path) {
  showView('explorer');
  mainButton.hide();
  await loadFolder(normPath(path));
}

function updateBackButton() {
  const inEditor = !els.views.editor.hidden;
  if (inEditor || state.currentPath !== '') {
    backButton.show(handleBackNavigation);
  } else {
    backButton.hide();
  }
}

function handleBackNavigation() {
  if (!els.views.editor.hidden) {
    void leaveEditor();
    return;
  }
  if (state.currentPath !== '') {
    const segments = state.currentPath.split('/').filter(Boolean);
    segments.pop();
    void enterExplorer(segments.join('/'));
  }
}

// ---------------------------------------------------------------------------
// Search — results are shown in a popup sheet above the explorer
// ---------------------------------------------------------------------------

function hideSearchResults() {
  els.searchResults.hidden = true;
  els.searchResults.textContent = '';
  els.searchModal.hidden = true;
  els.searchModalResults.textContent = '';
}

function renderSearchResults(container, results) {
  container.textContent = '';
  if (!results.length) {
    const empty = document.createElement('div');
    empty.className = 'status-text';
    empty.textContent = STRINGS.emptySearch;
    container.append(empty);
    return;
  }
  for (const result of results) {
    const card = document.createElement('button');
    card.type = 'button';
    card.className = 'search-result';

    const path = document.createElement('div');
    path.className = 'path';
    path.textContent = result.notePath;

    const snippet = document.createElement('div');
    snippet.className = 'snippet';
    snippet.innerHTML = renderSnippet(result.snippet ?? '');

    card.append(path, snippet);
    card.addEventListener('click', () => {
      hideSearchResults();
      openEditor('edit', result.notePath);
    });
    container.append(card);
  }
}

async function runSearch(query) {
  const seq = (state.searchSeq += 1);
  els.searchClear.hidden = query === '';

  if (query === '') {
    if (seq === state.searchSeq) hideSearchResults();
    return;
  }

  // Busy indicator on the "Find" button plus a placeholder in the popup.
  els.searchFind.hidden = false;
  els.searchFind.disabled = true;
  els.searchFind.classList.add('busy');
  els.searchModal.hidden = false;
  els.searchModalResults.textContent = '';
  const pending = document.createElement('div');
  pending.className = 'status-text';
  pending.textContent = STRINGS.searching;
  els.searchModalResults.append(pending);

  try {
    const results = await api.searchNotes(query);
    if (seq !== state.searchSeq) return;

    // Show matches in a popup sheet instead of the inline results block.
    els.searchModal.hidden = false;
    renderSearchResults(els.searchModalResults, results);
  } catch (error) {
    if (seq !== state.searchSeq) return;
    els.searchModal.hidden = false;
    els.searchModalResults.textContent = '';
    const failed = document.createElement('div');
    failed.className = 'status-text';
    failed.textContent = error instanceof api.ApiError ? error.message : STRINGS.loadFailed;
    els.searchModalResults.append(failed);
  } finally {
    if (seq === state.searchSeq) {
      els.searchFind.disabled = false;
      els.searchFind.classList.remove('busy');
    }
  }
}

// The search only runs on the "Find" button (or Enter) — not while typing,
// so a slow typist never triggers half-finished queries.
els.searchInput.addEventListener('input', () => {
  const hasQuery = els.searchInput.value.trim() !== '';
  els.searchFind.hidden = !hasQuery;
  if (!hasQuery) {
    state.searchSeq += 1; // invalidate any in-flight request
    els.searchFind.disabled = false;
    els.searchFind.classList.remove('busy');
    hideSearchResults();
  }
});

els.searchInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    const query = els.searchInput.value.trim();
    if (query !== '') void runSearch(query);
  }
});

els.searchFind.addEventListener('click', () => {
  const query = els.searchInput.value.trim();
  if (query !== '') void runSearch(query);
});

function clearSearch() {
  els.searchInput.value = '';
  els.searchClear.hidden = true;
  els.searchFind.hidden = true;
  els.searchFind.disabled = false;
  els.searchFind.classList.remove('busy');
  hideSearchResults();
  els.searchInput.focus();
}

els.searchClear.addEventListener('click', clearSearch);
els.searchModalClear.addEventListener('click', clearSearch);
els.searchBackdrop.addEventListener('click', hideSearchResults);
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !els.searchModal.hidden) hideSearchResults();
});

// ---------------------------------------------------------------------------
// Pull-to-refresh: swipe down from the top of the explorer to force-refresh
// the listing and the vault index (external changes become visible).
// ---------------------------------------------------------------------------

const pullIndicator = document.createElement('div');
pullIndicator.className = 'pull-indicator';
pullIndicator.textContent = '↓';
document.getElementById('view-explorer').prepend(pullIndicator);

let pullStartY = null;
let pullActive = false;

document.getElementById('view-explorer').addEventListener('touchstart', (event) => {
  if (els.views.explorer.hidden) return;
  if (window.scrollY > 0) return;
  pullStartY = event.touches[0].clientY;
  pullActive = true;
}, { passive: true });

document.getElementById('view-explorer').addEventListener('touchmove', (event) => {
  if (!pullActive || pullStartY == null) return;
  const delta = event.touches[0].clientY - pullStartY;
  if (delta <= 0) {
    pullIndicator.classList.remove('ready');
    pullIndicator.style.transform = '';
    return;
  }
  const clamped = Math.min(delta, 90);
  pullIndicator.style.transform = `translateY(${clamped}px)`;
  pullIndicator.textContent = delta > 60 ? '↻' : '↓';
  pullIndicator.classList.toggle('ready', delta > 60);
}, { passive: true });

document.getElementById('view-explorer').addEventListener('touchend', (event) => {
  if (!pullActive || pullStartY == null) return;
  const delta = event.changedTouches[0].clientY - pullStartY;
  pullActive = false;
  pullStartY = null;
  if (delta > 60 && !els.views.explorer.hidden) {
    pullIndicator.textContent = '…';
    void (async () => {
      invalidateVaultIndex();
      await loadFolder(state.currentPath);
      pullIndicator.style.transform = '';
      pullIndicator.classList.remove('ready');
      pullIndicator.textContent = '↓';
    })();
  } else {
    pullIndicator.style.transform = '';
    pullIndicator.classList.remove('ready');
    pullIndicator.textContent = '↓';
  }
});

// ---------------------------------------------------------------------------
// Editor view
// ---------------------------------------------------------------------------

function updatePreviewBanner() {
  // Make an active preview state explicit: the note looks read-only.
  els.previewBanner.hidden = !editor.isPreviewActive();
}

function openEditor(mode, path) {
  state.editor = { mode, path: path ?? null, originalContent: null };
  els.editorTitle.textContent = mode === 'edit'
    ? path.split('/').pop()
    : t('newNoteTitle');
  els.btnEditorDelete.hidden = mode !== 'edit';
  els.notePath.value = mode === 'edit' ? path : suggestNewPath();
  setError(els.editorStatus, '');

  showView('editor');
  els.searchInput.blur();
  updateBackButton();

  mainButton.show(STRINGS.saveBtnIdle, () => void saveCurrentNote());
  setButtonBusy(els.btnEditorSave, false, STRINGS.saveBtnBusy, STRINGS.saveBtnIdle);

  if (mode === 'edit') {
    editor.clear();
    void loadNoteIntoEditor(path);
  } else {
    editor.setValue('');
    editor.get(); // ensure instance exists before user types
  }
  // The EasyMDE instance is reused across notes — if the previous session
  // ended in preview mode, say so loudly instead of a silent read-only note.
  updatePreviewBanner();
}

function suggestNewPath() {
  const prefix = state.currentPath ? `${state.currentPath}/` : '';
  return `${prefix}note.md`;
}

async function loadNoteIntoEditor(path) {
  setButtonBusy(els.btnEditorSave, true, STRINGS.saveBtnBusy, STRINGS.saveBtnIdle);
  setError(els.editorStatus, '');
  try {
    const data = await api.getNoteContent(path);
    if (state.editor.path !== path || els.views.editor.hidden) return;
    if (data?.content === null || data?.content === undefined) {
      showToast(STRINGS.tooLarge);
      void leaveEditor({ force: true });
      return;
    }
    state.editor.originalContent = data.content;
    editor.setValue(data.content);
  } catch (error) {
    if (state.editor.path !== path || els.views.editor.hidden) return;
    showErrorToast(error);
    void leaveEditor({ force: true });
  } finally {
    setButtonBusy(els.btnEditorSave, false, STRINGS.saveBtnBusy, STRINGS.saveBtnIdle);
  }
}

function isEditorDirty() {
  if (state.editor.mode === 'create') {
    return editor.getValue() !== '' || els.notePath.value.trim() !== suggestNewPath();
  }
  return state.editor.originalContent !== null
    && editor.getValue() !== state.editor.originalContent;
}

async function leaveEditor({ force = false } = {}) {
  if (!force && isEditorDirty()) {
    // Autosave: silently push the change to the local queue on exit.
    if (isAutosaveEnabled() && state.editor.mode === 'edit' && editor.getValue().trim() !== '') {
      await saveCurrentNote();
      return; // saveCurrentNote already returns to the explorer
    }
    const ok = await showConfirm(STRINGS.confirmDiscard);
    if (!ok) return;
  }
  mainButton.hide();
  await enterExplorer(state.currentPath);
}

function validateNotePath(rawPath) {
  const path = normPath(rawPath);
  if (!path || path.endsWith('/')) return null;
  if (!/\.[^/]+$/.test(path)) return `${path}.md`; // auto-append extension
  return path;
}

async function saveCurrentNote() {
  const path = validateNotePath(els.notePath.value);
  if (!path) {
    setError(els.editorStatus, STRINGS.invalidPath);
    haptic('error');
    return;
  }

  const content = editor.getValue();
  setButtonBusy(els.btnEditorSave, true, STRINGS.saveBtnBusy, STRINGS.saveBtnIdle);
  mainButton.show(STRINGS.saveBtnBusy, () => {}, { color: '#999999' });

  try {
    const originalPath = state.editor.mode === 'edit' ? state.editor.path : null;
    if (originalPath && path !== originalPath) {
      // Path changed in the editor → move (rename): write the new content to
      // the new location and remove the old file.
      await api.moveNote(originalPath, path, content);
      haptic('success');
      showToast(STRINGS.moved);
    } else {
      await api.saveNote(path, content);
      haptic('success');
      showToast(STRINGS.saved);
    }
    invalidateVaultIndex();
    void refreshSyncBadge();
    state.editor.originalContent = content;
    state.editor.mode = 'edit';
    state.editor.path = path;
    els.notePath.value = path;
    const folder = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '';
    state.currentPath = folder;
    await enterExplorer(folder);
  } catch (error) {
    mainButton.show(STRINGS.saveBtnIdle, () => void saveCurrentNote());
    setButtonBusy(els.btnEditorSave, false, STRINGS.saveBtnBusy, STRINGS.saveBtnIdle);
    showErrorToast(error);
  }
}

function openInfoModal(item, isDir) {
  state.infoItem = item;
  state.infoItemIsDir = isDir;
  els.infoTitle.textContent = STRINGS.infoTitle;
  els.infoFields.textContent = '';

  const addField = (label, value) => {
    const dt = document.createElement('dt');
    dt.textContent = label;
    const dd = document.createElement('dd');
    dd.textContent = value;
    els.infoFields.append(dt, dd);
  };

  addField(t('infoTypeLabel'), isDir ? STRINGS.infoTypeFolder : STRINGS.infoTypeFile);
  addField(STRINGS.infoTypeName, item.name ?? '');
  addField(STRINGS.infoTypePath, item.path ?? '');
  if (isDir) {
    // Best-effort count; keep it silent when the listing is unavailable.
    void api.listNotes(item.path)
      .then((children) => {
        if (!els.infoModal.hidden) {
          addField(STRINGS.infoTypeContains, STRINGS.folderItemsCount(Array.isArray(children) ? children.length : 0));
        }
      })
      .catch(() => {});
  } else {
    addField(STRINGS.infoTypeSize, formatSize(item.size));
    // Best-effort tag list from the note content; silent on failure.
    void api.getNoteContent(item.path)
      .then((data) => {
        if (els.infoModal.hidden) return;
        const tags = extractTags(String(data?.content ?? ''));
        if (tags.length) addField(STRINGS.infoTypeTags, tags.map((x) => `#${x}`).join(' '));
      })
      .catch(() => {});
  }

  if (item.htmlUrl) {
    els.infoGithubLink.href = item.htmlUrl;
    els.infoGithubLink.hidden = false;
  } else {
    els.infoGithubLink.hidden = true;
  }

  // Moving / renaming works for files and folders (folders move recursively).
  els.infoMove.hidden = false;

  els.infoModal.hidden = false;
}

function closeInfoModal() {
  els.infoModal.hidden = true;
  els.infoGithubLink.hidden = true;
}

els.infoClose.addEventListener('click', closeInfoModal);
els.infoBackdrop.addEventListener('click', closeInfoModal);
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !els.infoModal.hidden) closeInfoModal();
});

// ---------------------------------------------------------------------------
// Move / rename sheet: pick the destination folder from the vault index or
// type a new one, then edit the name. No guessing whether a folder exists.
// ---------------------------------------------------------------------------

async function openMoveModal(fromPath, parentDir) {
  state.moveFrom = fromPath;
  const isDir = state.infoItemIsDir === true;
  const oldDir = parentDir !== undefined ? parentDir : fromPath.includes('/') ? fromPath.slice(0, fromPath.lastIndexOf('/')) : '';
  state.moveOldDir = oldDir;

  setError(els.moveError, '');
  els.moveName.value = fromPath.split('/').pop();
  els.moveNewFolder.value = '';
  els.moveNewFolderField.hidden = true;
  els.moveFolder.textContent = '';
  els.moveFolder.disabled = false;

  // Populate the folder select from the vault index: Корень + existing
  // folders + "New folder…" — the user never has to guess what exists.
  let folders = [];
  try {
    folders = (await getVaultIndex()).folders;
  } catch {
    /* keep just the root + new-folder option */
  }
  for (const folder of ['', ...folders]) {
    const option = document.createElement('option');
    option.value = folder;
    option.textContent = folder === '' ? STRINGS.moveRootFolder : folder;
    els.moveFolder.append(option);
  }
  const newOption = document.createElement('option');
  newOption.value = '__new__';
  newOption.textContent = STRINGS.moveNewFolderOption;
  els.moveFolder.append(newOption);
  if (!isDir) {
    els.moveFolder.value = oldDir;
  } else {
    // For folder moves the destination cannot be inside itself; preselect root.
    els.moveFolder.value = '';
  }

  els.infoModal.hidden = true;
  els.moveModal.hidden = false;
  els.moveName.focus();
  els.moveName.select();
}

function closeMoveModal() {
  els.moveModal.hidden = true;
  state.moveFrom = null;
  state.moveOldDir = null;
}

els.moveFolder.addEventListener('change', () => {
  els.moveNewFolderField.hidden = els.moveFolder.value !== '__new__';
  if (!els.moveNewFolderField.hidden) els.moveNewFolder.focus();
});

async function submitMove() {
  const fromPath = state.moveFrom;
  if (!fromPath) return;

  const folderChoice = els.moveFolder.value;
  let targetDir;
  if (folderChoice === '__new__') {
    targetDir = normPath(els.moveNewFolder.value);
    if (!targetDir || targetDir.endsWith('/')) {
      setError(els.moveError, STRINGS.invalidName);
      return;
    }
  } else {
    targetDir = folderChoice;
  }

  const rawName = els.moveName.value.trim();
  if (!rawName || rawName.includes('/') || rawName === '.' || rawName === '..') {
    setError(els.moveError, STRINGS.invalidName);
    return;
  }
  // Keep the extension when the user typed the name without it (files only).
  const oldName = fromPath.split('/').pop();
  const name = !/\.[^/]+$/.test(rawName) && /\.[^/]+$/.test(oldName) ? `${rawName}${oldName.match(/\.[^/]+$/)[0]}` : rawName;

  const toPath = targetDir ? `${targetDir}/${name}` : name;
  if (normPath(toPath) === normPath(fromPath)) {
    closeMoveModal();
    return;
  }

  try {
    await api.moveNote(fromPath, toPath);
    haptic('success');
    showToast(STRINGS.moved);
    closeMoveModal();
    invalidateVaultIndex();
    void refreshSyncBadge();
    // Keep the editor in sync when the open note was moved from the sheet.
    if (state.editor.mode === 'edit' && state.editor.path === fromPath) {
      state.editor.path = toPath;
      els.notePath.value = toPath;
      els.editorTitle.textContent = toPath.split('/').pop();
    }
    if (!els.views.editor.hidden) {
      await enterExplorer(state.currentPath);
    } else {
      await loadFolder(state.currentPath);
    }
  } catch (error) {
    setError(els.moveError, error instanceof Error && error.message ? error.message : STRINGS.loadFailed);
    haptic('error');
  }
}

els.infoMove.addEventListener('click', () => {
  if (state.infoItem) openMoveModal(state.infoItem.path);
});
els.moveSubmit.addEventListener('click', () => void submitMove());
els.moveCancel.addEventListener('click', closeMoveModal);
els.moveBackdrop.addEventListener('click', closeMoveModal);
els.moveName.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    void submitMove();
  }
});
els.moveNewFolder.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    void submitMove();
  }
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !els.moveModal.hidden) closeMoveModal();
});

// ---------------------------------------------------------------------------
// Link picker: insert [[note]] from a searchable list, or an external URL.
// Opened from the editor toolbar (the old "link" button's useless
// "[ ](https://)" template is gone).
// ---------------------------------------------------------------------------

let linkPickerSeq = 0;

async function openLinkPicker() {
  state.linkTargetDir = state.currentPath;
  els.linkUrl.value = '';
  setError(els.linkError, '');
  els.linkSearch.value = '';
  els.linkResults.textContent = '';
  els.linkModal.hidden = false;
  els.linkSearch.focus();

  const seq = ++linkPickerSeq;
  try {
    const { notes } = await getVaultIndex();
    if (seq !== linkPickerSeq || els.linkModal.hidden) return;
    renderLinkResults(notes, '');
  } catch {
    if (seq === linkPickerSeq && !els.linkModal.hidden) {
      els.linkResults.textContent = STRINGS.linkPickerNoNotes;
    }
  }
}

function renderLinkResults(notes, query) {
  const q = query.trim().toLowerCase();
  const matches = q
    ? notes.filter((n) => n.path.toLowerCase().includes(q))
    : notes;
  els.linkResults.textContent = '';

  if (!matches.length) {
    const empty = document.createElement('div');
    empty.className = 'link-empty';
    empty.textContent = STRINGS.linkPickerNoNotes;
    els.linkResults.append(empty);
    return;
  }
  for (const note of matches.slice(0, 50)) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'link-result';
    const name = document.createElement('span');
    name.className = 'link-result-name';
    name.textContent = note.name;
    const dir = document.createElement('span');
    dir.className = 'link-result-dir';
    dir.textContent = note.dir || STRINGS.moveRootFolder;
    btn.append(name, dir);
    btn.addEventListener('click', () => {
      const target = note.path.replace(/\.md$/i, '');
      editor.insertAtCursor(`[[${target}]]`);
      closeLinkPicker();
    });
    els.linkResults.append(btn);
  }
}

function closeLinkPicker() {
  els.linkModal.hidden = true;
}

els.linkSearch.addEventListener('input', async () => {
  const seq = ++linkPickerSeq;
  try {
    const { notes } = await getVaultIndex();
    if (seq === linkPickerSeq && !els.linkModal.hidden) renderLinkResults(notes, els.linkSearch.value);
  } catch {
    /* keep current list */
  }
});

els.linkInsertUrl.addEventListener('click', () => {
  const url = els.linkUrl.value.trim();
  if (!/^(https?:\/\/|mailto:)/i.test(url)) {
    setError(els.linkError, STRINGS.linkInvalid);
    return;
  }
  const selected = editor.getSelection().trim();
  editor.insertAtCursor(`[${selected || url}](${url})`);
  closeLinkPicker();
});

els.linkCancel.addEventListener('click', closeLinkPicker);
els.linkBackdrop.addEventListener('click', closeLinkPicker);
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !els.linkModal.hidden) closeLinkPicker();
});
document.addEventListener('link-picker-open', () => {
  void openLinkPicker();
});

// Tag chips rendered in the preview (#tag) run a notes search on click.
document.addEventListener('tag-click', async (event) => {
  const tag = String(event.detail?.tag || '').trim();
  if (!tag) return;
  mainButton.hide();
  showView('explorer');
  els.searchInput.value = `#${tag}`;
  els.searchFind.hidden = false;
  await runSearch(`#${tag}`);
});

// Wikilinks rendered in the preview ([[note]] / [[note|label]]) open the
// target note in the editor. Resolution order: current folder, repo root,
// then a breadth-first search across the whole vault (like Obsidian).
document.addEventListener('wiki-open', async (event) => {
  const raw = normPath(String(event.detail?.target || ''));
  if (!raw) return;
  const folder = state.currentPath;
  const candidates = raw.includes('/')
    ? [raw, `${raw}.md`]
    : [...(folder ? [`${folder}/${raw}`, `${folder}/${raw}.md`] : []), raw, `${raw}.md`];

  for (const candidate of candidates) {
    try {
      await api.getNoteContent(candidate);
      openEditor('edit', candidate);
      return;
    } catch (error) {
      if (error instanceof api.ApiError && error.status !== 404) {
        showErrorToast(error);
        return;
      }
    }
  }

  const vaultPath = await searchVaultForNote(raw);
  if (vaultPath) {
    openEditor('edit', vaultPath);
  } else {
    showToast(STRINGS.noteNotFound(raw));
  }
});

// Breadth-first lookup of a note by name across the repository. Folder
// listings are cached server-side, so repeated searches are cheap.
const VAULT_SEARCH_BUDGET = 40;
async function searchVaultForNote(raw) {
  const wanted = new Set([raw.toLowerCase(), `${raw}.md`.toLowerCase()]);
  const queue = [''];
  let budget = VAULT_SEARCH_BUDGET;

  while (queue.length && budget > 0) {
    budget -= 1;
    const dir = queue.shift();
    let items;
    try {
      items = await api.listNotes(dir || undefined);
    } catch {
      continue;
    }
    if (!Array.isArray(items)) continue;
    for (const item of items) {
      if ((item.type || '').toLowerCase() === 'dir') {
        queue.push(item.path);
      } else if (wanted.has(String(item.path).toLowerCase()) || wanted.has(String(item.name).toLowerCase())) {
        return item.path;
      }
    }
  }
  return null;
}

async function confirmAndDeleteItem(path, displayName, isDir) {
  const message = isDir
    ? STRINGS.confirmDeleteFolder(displayName ?? path)
    : STRINGS.confirmDelete(displayName ?? path);
  const ok = await showConfirm(message);
  if (!ok) return;
  try {
    if (isDir) showToast(STRINGS.deleting, 10000);
    await api.deleteNote(path);
    invalidateVaultIndex();
    haptic('success');
    showToast(isDir ? STRINGS.deletedFolder : STRINGS.deleted);
    void refreshSyncBadge();
    if (!els.views.editor.hidden) {
      mainButton.hide();
      await enterExplorer(state.currentPath);
    } else {
      await loadFolder(state.currentPath);
    }
  } catch (error) {
    showErrorToast(error);
  }
}

// ---------------------------------------------------------------------------
// Settings (token / repository / autosave) & manual sync
// ---------------------------------------------------------------------------

function isAutosaveEnabled() {
  return localStorage.getItem('gitenberg.autosave') === '1';
}

function autosyncIntervalMinutes() {
  const v = Number(localStorage.getItem('gitenberg.autosync.interval'));
  return Number.isFinite(v) && v > 0 ? v : 5;
}

async function openSettings() {
  els.settingsError.hidden = true;
  els.settingsToken.value = '';
  els.settingsAutosave.checked = isAutosaveEnabled();
  const autosync = localStorage.getItem('gitenberg.autosync') === '1';
  els.settingsAutosync.checked = autosync;
  els.autosyncIntervalField.hidden = !autosync;
  els.settingsAutosyncInterval.value = String(autosyncIntervalMinutes());
  showView('settings');
  backButton.show(handleBackNavigation);
  mainButton.hide();
  try {
    const settings = await api.getSettings();
    els.settingsOwner.value = settings.repositoryOwner ?? '';
    els.settingsRepo.value = settings.repositoryName ?? '';
    els.settingsInboxPath.value = settings.inboxPath ?? '';
    els.settingsAttachmentsPath.value = settings.attachmentsPath ?? '';
  } catch (error) {
    setError(els.settingsError, error instanceof api.ApiError ? error.message : STRINGS.loadFailed);
  }
}

async function saveSettings() {
  setError(els.settingsError, '');
  const owner = els.settingsOwner.value.trim();
  const repo = els.settingsRepo.value.trim();
  const token = els.settingsToken.value.trim();
  const inboxPath = els.settingsInboxPath.value.trim().replace(/^\/+|\/+$/g, '');
  const attachmentsPath = els.settingsAttachmentsPath.value.trim().replace(/^\/+|\/+$/g, '');
  if (!owner || !repo) {
    setError(els.settingsError, STRINGS.fillAllFields);
    return;
  }
  try {
    await api.saveSettings({
      githubToken: token || undefined,
      repositoryOwner: owner,
      repositoryName: repo,
      inboxPath: inboxPath || undefined,
      attachmentsPath: attachmentsPath || undefined,
    });
    localStorage.setItem('gitenberg.autosave', els.settingsAutosave.checked ? '1' : '0');
    localStorage.setItem('gitenberg.autosync', els.settingsAutosync.checked ? '1' : '0');
    localStorage.setItem('gitenberg.autosync.interval', els.settingsAutosyncInterval.value);
    restartAutosyncTimer();
    haptic('success');
    showToast(STRINGS.saved);
    void enterExplorer(state.currentPath);
  } catch (error) {
    setError(els.settingsError, error instanceof api.ApiError ? error.message : STRINGS.loadFailed);
    haptic('error');
  }
}

els.btnSettings.addEventListener('click', () => void openSettings());
els.settingsSave.addEventListener('click', () => void saveSettings());
els.settingsBack.addEventListener('click', () => void enterExplorer(state.currentPath));
els.settingsAutosync.addEventListener('change', () => {
  els.autosyncIntervalField.hidden = !els.settingsAutosync.checked;
});

// Export: download a ZIP of the whole repository via the backend.
async function exportArchive() {
  const btn = els.btnExport;
  if (btn.classList.contains('busy')) return;
  btn.disabled = true;
  btn.classList.add('busy');
  try {
    const { blob, fileName } = await api.downloadExportArchive();
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
    haptic('success');
    showToast(STRINGS.exportSuccess);
  } catch (error) {
    haptic('error');
    showErrorToast(error);
  } finally {
    btn.disabled = false;
    btn.classList.remove('busy');
  }
}

els.btnExport.addEventListener('click', () => void exportArchive());

let syncBadgeTimer = null;
async function refreshSyncBadge() {
  try {
    const { pending } = await api.syncStatus();
    els.syncBadge.hidden = pending === 0;
    els.syncBadge.textContent = String(pending);
    els.syncBadge.title = t('syncPendingHint');
  } catch {
    /* status probe is best-effort */
  }
}

let autosyncTimerId = null;
function restartAutosyncTimer() {
  if (autosyncTimerId) clearInterval(autosyncTimerId);
  if (localStorage.getItem('gitenberg.autosync') !== '1') return;
  const ms = autosyncIntervalMinutes() * 60_000;
  autosyncTimerId = setInterval(() => {
    if (els.syncBadge.hidden) return; // nothing to sync
    els.btnSync.click();
  }, ms);
}

els.btnSync.addEventListener('click', () => {
  els.syncBadge.hidden = true;
  showToast(STRINGS.syncing, 8000);
  void (async () => {
    try {
      const { applied, remaining } = await api.syncNow();
      haptic('success');
      showToast(remaining > 0
        ? `${t('syncDonePartial')(applied, remaining)}`
        : t('syncDone')(applied));
      invalidateVaultIndex();
      if (!els.views.explorer.hidden) await loadFolder(state.currentPath);
    } catch (error) {
      showErrorToast(error);
    } finally {
      void refreshSyncBadge();
    }
  })();
});

// ---------------------------------------------------------------------------
// Wiring & startup
// ---------------------------------------------------------------------------

// Language toggle (RU ⇄ EN); the initial language was auto-detected in i18n.
els.btnLang.addEventListener('click', () => {
  setLang(getLang() === 'ru' ? 'en' : 'ru');
  els.btnLang.textContent = getLang() === 'ru' ? 'EN' : 'RU';
  // Re-render dynamic texts of the visible view.
  if (!els.views.explorer.hidden) {
    renderBreadcrumbs(state.currentPath);
    if (els.notesList.querySelector('.note-row')) void loadFolder(state.currentPath);
  }
});
els.btnLang.textContent = getLang() === 'ru' ? 'EN' : 'RU';
document.addEventListener('language-changed', () => {
  els.explorerTitle.textContent = STRINGS.explorerTitle;
  els.btnLang.textContent = getLang() === 'ru' ? 'EN' : 'RU';
  if (!els.views.editor.hidden) {
    mainButton.show(STRINGS.saveBtnIdle, () => void saveCurrentNote());
  }
});

els.btnCreateNote.addEventListener('click', () => {
  haptic('select');
  openEditor('create');
});
els.fabCreate.addEventListener('click', () => {
  haptic('select');
  openEditor('create');
});

els.btnEditorBack.addEventListener('click', () => void leaveEditor());
els.previewBannerEdit.addEventListener('click', () => {
  editor.exitPreview();
  updatePreviewBanner();
});
document.addEventListener('preview-toggled', (event) => {
  els.previewBanner.hidden = !event.detail?.active;
});
els.btnEditorDelete.addEventListener('click', () => {
  if (state.editor.mode === 'edit' && state.editor.path) {
    void confirmAndDeleteItem(state.editor.path, state.editor.path.split('/').pop(), false);
  }
});
els.btnEditorSave.addEventListener('click', () => void saveCurrentNote());

els.views.register.hidden = true;
els.views.explorer.hidden = true;
els.views.editor.hidden = true;

function restartProbe() {
  void bootstrap();
}

async function bootstrap() {
  syncDevBar();
  showView('explorer');
  skeletonRows();
  setStatus(STRINGS.loading);

  try {
    const items = await api.listNotes();
    renderNotes(Array.isArray(items) ? items : []);
    state.currentPath = '';
    renderBreadcrumbs('');
    updateBackButton();
  } catch (error) {
    if (error instanceof api.ApiError && error.status === 404) {
      // User not registered yet → registration screen.
      state.currentPath = '';
      showView('register');
      backButton.hide();
      return;
    }
    if (error instanceof api.ApiError && error.status === 400 && /Telegram ID/.test(error.message)) {
      // Dev mode without a valid test id — stay on explorer with a hint.
      setStatus(els.devBar.hidden ? STRINGS.loadFailed : t('devBarHint'));
      return;
    }
    showView('register');
    setError(els.registerError, error instanceof api.ApiError ? error.message : STRINGS.loadFailed);
  }
}

function initTelegram() {
  const webApp = tg();
  if (!webApp) return;
  try {
    webApp.ready();
    webApp.expand();
  } catch {
    /* SDK quirks outside Telegram */
  }
}

initTelegram();
initI18n();
restartAutosyncTimer();
void bootstrap();
void refreshSyncBadge();
setInterval(() => void refreshSyncBadge(), 60000);
