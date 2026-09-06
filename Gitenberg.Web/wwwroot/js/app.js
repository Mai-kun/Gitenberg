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
const PIN_ICON = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 17v5"/><path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z"/></svg>';

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
  infoHistory: $('info-history'),
  infoMove: $('info-move'),
  infoPin: $('info-pin'),
  infoPinLabel: $('info-pin-label'),
  infoClose: $('info-close'),
  infoBackdrop: $('info-backdrop'),
  historyModal: $('history-modal'),
  historyTitle: $('history-title'),
  historyPath: $('history-path'),
  historyPending: $('history-pending'),
  historyList: $('history-list'),
  historyClose: $('history-close'),
  historyBackdrop: $('history-backdrop'),
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
  repoList: $('repo-list'),
  btnRepoAdd: $('btn-repo-add'),
  repoFormTitle: $('repo-form-title'),
  settingsRepoName: $('settings-repo-name'),
  repoFormActions: $('repo-form-actions'),
  btnRepoCancelEdit: $('btn-repo-cancel-edit'),
  btnRepoDelete: $('btn-repo-delete'),
  settingsAutosave: $('settings-autosave'),
  settingsAutosync: $('settings-autosync'),
  settingsError: $('settings-error'),
  settingsSave: $('settings-save'),
  btnExport: $('btn-export'),
  settingsBack: $('btn-settings-back'),
  settingsAutosyncInterval: $('settings-autosync-interval'),
  autosyncIntervalField: $('autosync-interval-field'),
  explorerTabs: $('explorer-tabs'),
  tabNotes: $('tab-notes'),
  tabTasks: $('tab-tasks'),
  tasksTabCount: $('tasks-tab-count'),
  activityCard: $('activity-card'),
  activityToggle: $('activity-toggle'),
  activitySummary: $('activity-summary'),
  activityChevron: $('activity-chevron'),
  activityBody: $('activity-body'),
  heatmapGrid: $('heatmap-grid'),
  tasksToolbar: $('tasks-toolbar'),
  tasksFilterActive: $('tasks-filter-active'),
  tasksFilterAll: $('tasks-filter-all'),
  tasksList: $('tasks-list'),
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
  historyPath: null, // note path shown in the history sheet
  historySeq: 0, // guards against out-of-order history responses
  currentTab: 'notes', // 'notes' | 'tasks' (root-level tab switch)
  tasks: [], // aggregated checklist from GET /api/notes/tasks
  tasksFilter: 'active', // 'active' | 'all'
  heatmapOk: false, // heatmap data loaded successfully at least once
  repositories: [], // user's repositories (GET /api/repositories)
  repoFormRepoId: null, // repository bound to the settings form; null = new one
  pinnedPaths: null, // Set of pinned paths of the active repository; null = not loaded yet
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

// fetch() rejects with a TypeError on network failures (server unreachable,
// offline). The browser message ("Failed to fetch") is cryptic — show a
// friendly localized hint instead.
function isNetworkError(error) {
  return error instanceof TypeError && /fetch|network|load failed/i.test(error?.message || '');
}

function friendlyErrorText(error) {
  if (isNetworkError(error)) return STRINGS.networkError;
  return error instanceof Error && error.message ? error.message : STRINGS.loadFailed;
}

function showErrorToast(error) {
  if (isNetworkError(error)) {
    showToast(`${STRINGS.errorPrefix}: ${STRINGS.networkError}`);
    haptic('error');
    return;
  }
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

function renderNotes(items, pinnedPaths = new Set()) {
  els.notesList.textContent = '';

  if (!items.length) {
    setStatus(STRINGS.emptyFolder);
    return;
  }
  setStatus('');

  const isDirectory = (item) => (item?.type || '').toLowerCase() === 'dir';
  const isPinned = (item) => pinnedPaths.has(normPath(item.path));

  // Pinned folders, pinned files, regular folders, regular files.
  const sorted = [...items].sort((a, b) => {
    const aPin = isPinned(a) ? 0 : 1;
    const bPin = isPinned(b) ? 0 : 1;
    if (aPin !== bPin) return aPin - bPin;
    const aDir = isDirectory(a) ? 0 : 1;
    const bDir = isDirectory(b) ? 0 : 1;
    if (aDir !== bDir) return aDir - bDir;
    return String(a.name).localeCompare(String(b.name), 'ru', { sensitivity: 'base' });
  });

  for (const item of sorted) {
    const isDir = isDirectory(item);
    const isMd = /\.md$/i.test(String(item.name));
    const pinned = isPinned(item);

    const row = document.createElement('button');
    row.type = 'button';
    row.className = `note-row ${isDir ? 'is-dir' : 'is-file'}${pinned ? ' is-pinned' : ''}`;
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
    if (pinned) {
      const pin = document.createElement('span');
      pin.className = 'note-pin';
      pin.innerHTML = PIN_ICON;
      pin.setAttribute('aria-label', '📌');
      name.prepend(pin);
    }
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
  const atRoot = path === '';
  els.explorerTabs.hidden = !atRoot;
  els.activityCard.hidden = !atRoot || !state.heatmapOk;
  showTab(atRoot ? state.currentTab : 'notes');
  renderBreadcrumbs(path);
  hideSearchResults();
  skeletonRows();
  setStatus('');
  updateBackButton();

  try {
    const [items, pinnedPaths] = await Promise.all([
      api.listNotes(path || undefined),
      getPinnedPaths(),
    ]);
    if (state.currentPath !== path) return; // user navigated away meanwhile
    renderNotes(Array.isArray(items) ? items : [], pinnedPaths);
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
// Activity heatmap (GitHub contribution style) — root level only
// ---------------------------------------------------------------------------

let heatmapCollapsed = localStorage.getItem('gitenberg.activity.collapsed') === '1';

function localDateKey(date) {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

function heatmapLevel(count) {
  if (count <= 0) return 'level-0';
  if (count === 1) return 'level-1';
  if (count <= 3) return 'level-2';
  if (count <= 6) return 'level-3';
  return 'level-4';
}

function renderHeatmap(days) {
  const counts = new Map(days.map((d) => [d.date, d.count]));
  const grid = els.heatmapGrid;
  grid.textContent = '';

  const today = new Date();
  // Align columns to weeks, Monday first row (RU convention).
  const mondayOffset = (today.getDay() + 6) % 7;
  const currentWeekMonday = new Date(today);
  currentWeekMonday.setDate(today.getDate() - mondayOffset);

  const weeks = 53;
  for (let w = weeks - 1; w >= 0; w -= 1) {
    for (let dow = 0; dow < 7; dow += 1) {
      const cellDate = new Date(currentWeekMonday);
      cellDate.setDate(currentWeekMonday.getDate() - w * 7 + dow);
      const key = localDateKey(cellDate);
      const count = counts.get(key) ?? 0;

      const cell = document.createElement('span');
      cell.className = `heatmap-cell ${heatmapLevel(count)}`;
      cell.title = `${key}: ${count}`;
      if (cellDate > today) cell.classList.add('is-future');
      grid.append(cell);
    }
  }
}

function computeStreak(days) {
  const active = new Set(days.filter((d) => d.count > 0).map((d) => d.date));
  const cursor = new Date();
  if (!active.has(localDateKey(cursor))) {
    cursor.setDate(cursor.getDate() - 1); // today not yet active — count up to yesterday
  }
  let streak = 0;
  while (active.has(localDateKey(cursor))) {
    streak += 1;
    cursor.setDate(cursor.getDate() - 1);
  }
  return streak;
}

function applyActivityCollapsed() {
  els.activityBody.hidden = heatmapCollapsed;
  els.activityChevron.textContent = heatmapCollapsed ? '▸' : '▾';
  els.activityToggle.setAttribute('aria-expanded', heatmapCollapsed ? 'false' : 'true');
}

async function refreshHeatmap() {
  try {
    const data = await api.getActivityHeatmap();
    const days = Array.isArray(data?.days) ? data.days : [];
    renderHeatmap(days);
    els.activitySummary.textContent = STRINGS.activitySummary(data?.total ?? 0, computeStreak(days));
    state.heatmapOk = true;
  } catch {
    state.heatmapOk = false;
  }
  if (!els.views.explorer.hidden) {
    els.activityCard.hidden = state.currentPath !== '' || !state.heatmapOk;
  }
}

els.activityToggle.addEventListener('click', () => {
  heatmapCollapsed = !heatmapCollapsed;
  localStorage.setItem('gitenberg.activity.collapsed', heatmapCollapsed ? '1' : '0');
  applyActivityCollapsed();
});

// ---------------------------------------------------------------------------
// Tasks tab — all markdown checkboxes across the vault, toggle = new commit
// ---------------------------------------------------------------------------

// Short-lived per-note content cache: quick consecutive toggles must modify
// the already-flipped text, not re-read the stale one (race between clicks).
const taskContentCache = new Map();
// Per-path serialization: toggle jobs for one note run strictly in order.
const taskQueues = new Map();

function invalidateCaches() {
  invalidateVaultIndex();
  taskContentCache.clear();
  invalidatePins();
}

// ---------------------------------------------------------------------------
// Pins — "закрепить наверху" markers of the active repository. The backend
// re-points pins on move/delete and drops them with the repository, so after
// every mutation (which calls invalidateCaches()) a lazy re-read is enough.
// ---------------------------------------------------------------------------

function invalidatePins() {
  state.pinnedPaths = null;
}

// Resolves the pinned-path Set; never rejects (a failed probe just retries on
// the next listing render).
function getPinnedPaths() {
  if (state.pinnedPaths) return Promise.resolve(state.pinnedPaths);
  return api.listPins()
    .then((pins) => {
      state.pinnedPaths = new Set((Array.isArray(pins) ? pins : []).map((p) => normPath(p.path)));
      return state.pinnedPaths;
    })
    .catch(() => new Set());
}

// Mirrors the backend TaskListBuilder regex exactly (bullets, ordered lists,
// code-fence skipping) so occurrence indexes stay aligned.
const TASK_LINE_RE = /^(\s*(?:[-*+]|\d+[.)])\s+\[)([ xX])(\]\s*.*)$/;

function flipCheckboxOccurrence(content, occurrenceIndex) {
  const lines = content.split('\n');
  let occurrence = 0;
  let inFence = false;
  for (let i = 0; i < lines.length; i += 1) {
    if (/^\s*(```|~~~)/.test(lines[i])) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;
    const match = lines[i].match(TASK_LINE_RE);
    if (!match) continue;
    if (occurrence !== occurrenceIndex) {
      occurrence += 1;
      continue;
    }
    lines[i] = match[1] + (match[2] === ' ' ? 'x' : ' ') + match[3];
    return lines.join('\n');
  }
  throw new Error('Task line not found');
}

function updateTasksBadge() {
  const active = state.tasks.filter((task) => !task.checked).length;
  els.tasksTabCount.hidden = active === 0;
  els.tasksTabCount.textContent = String(active);
}

function renderTasks() {
  els.tasksList.textContent = '';
  const all = state.tasks;
  const visible = state.tasksFilter === 'active' ? all.filter((task) => !task.checked) : all;

  if (!visible.length) {
    const empty = document.createElement('div');
    empty.className = 'status-text';
    empty.textContent = state.tasksFilter === 'active' && all.length > 0 ? STRINGS.emptyTasks : STRINGS.emptyTasksAll;
    els.tasksList.append(empty);
    return;
  }

  const groups = new Map();
  for (const task of visible) {
    if (!groups.has(task.notePath)) groups.set(task.notePath, []);
    groups.get(task.notePath).push(task);
  }

  for (const [notePath, items] of groups) {
    const group = document.createElement('section');
    group.className = 'task-group';

    const header = document.createElement('button');
    header.type = 'button';
    header.className = 'task-group-header';
    const name = document.createElement('span');
    name.className = 'task-group-name';
    name.textContent = notePath.split('/').pop();
    const count = document.createElement('span');
    count.className = 'task-group-count';
    count.textContent = String(items.length);
    header.append(name, count);
    header.addEventListener('click', () => {
      showTab('notes');
      void openEditor('edit', notePath);
    });
    group.append(header);

    for (const task of items) {
      const row = document.createElement('div');
      row.className = `task-row${task.checked ? ' is-done' : ''}`;

      const check = document.createElement('button');
      check.type = 'button';
      check.className = 'task-check';
      check.setAttribute('role', 'checkbox');
      check.setAttribute('aria-checked', task.checked ? 'true' : 'false');
      check.setAttribute('aria-label', task.text || notePath);
      check.textContent = task.checked ? '✓' : '';
      check.addEventListener('click', () => toggleTask(task));

      const text = document.createElement('button');
      text.type = 'button';
      text.className = 'task-text';
      text.textContent = task.text || STRINGS.taskNoText;
      text.addEventListener('click', () => {
        showTab('notes');
        void openEditor('edit', task.notePath);
      });

      row.append(check, text);
      group.append(row);
    }

    els.tasksList.append(group);
  }
}

async function loadTasks() {
  els.tasksList.textContent = '';
  const pending = document.createElement('div');
  pending.className = 'status-text';
  pending.textContent = STRINGS.loading;
  els.tasksList.append(pending);

  try {
    const tasks = await api.getTasks();
    state.tasks = Array.isArray(tasks) ? tasks : [];
    updateTasksBadge();
    renderTasks();
  } catch (error) {
    els.tasksList.textContent = '';
    const failed = document.createElement('div');
    failed.className = 'status-text';
    failed.textContent = error instanceof api.ApiError ? error.message : STRINGS.loadFailed;
    els.tasksList.append(failed);
  }
}

// Background refresh: keeps the tab badge current without stealing the view.
async function refreshTasksData() {
  try {
    const tasks = await api.getTasks();
    state.tasks = Array.isArray(tasks) ? tasks : [];
    updateTasksBadge();
    if (!els.views.explorer.hidden && state.currentTab === 'tasks' && state.currentPath === '') {
      renderTasks();
    }
  } catch {
    /* badge stays stale until the next refresh */
  }
}

function showTab(tab) {
  state.currentTab = tab;
  els.tabNotes.classList.toggle('is-active', tab === 'notes');
  els.tabTasks.classList.toggle('is-active', tab === 'tasks');

  const notes = tab === 'notes';
  const atRoot = state.currentPath === '';
  els.notesList.hidden = !notes;
  els.tasksToolbar.hidden = notes || !atRoot;
  els.tasksList.hidden = notes || !atRoot;
  els.fabCreate.hidden = !notes;
  if (!notes && atRoot) void loadTasks();
}

els.tabNotes.addEventListener('click', () => {
  haptic('select');
  showTab('notes');
});
els.tabTasks.addEventListener('click', () => {
  haptic('select');
  showTab('tasks');
});
els.tasksFilterActive.addEventListener('click', () => {
  state.tasksFilter = 'active';
  els.tasksFilterActive.classList.add('is-active');
  els.tasksFilterAll.classList.remove('is-active');
  renderTasks();
});
els.tasksFilterAll.addEventListener('click', () => {
  state.tasksFilter = 'all';
  els.tasksFilterAll.classList.add('is-active');
  els.tasksFilterActive.classList.remove('is-active');
  renderTasks();
});

function toggleTask(task) {
  haptic('select');
  const wasChecked = task.checked;
  task.checked = !wasChecked; // optimistic flip
  updateTasksBadge();
  renderTasks();

  const previous = taskQueues.get(task.notePath) ?? Promise.resolve();
  const job = previous.catch(() => {}).then(async () => {
    try {
      let content = taskContentCache.get(task.notePath);
      if (content == null) {
        const data = await api.getNoteContent(task.notePath);
        content = data?.content ?? '';
      }
      const flipped = flipCheckboxOccurrence(content, task.occurrenceIndex);
      taskContentCache.set(task.notePath, flipped);
      await api.saveNote(task.notePath, flipped, `Toggle task: ${task.text || task.notePath}`);
      invalidateCaches();
      void refreshHeatmap();
      void refreshSyncBadge();
      haptic('success');
    } catch (error) {
      task.checked = wasChecked; // revert on failure
      updateTasksBadge();
      if (!els.views.explorer.hidden && state.currentTab === 'tasks' && state.currentPath === '') renderTasks();
      showErrorToast(error instanceof api.ApiError ? error : new Error(STRINGS.taskToggleFailed));
    } finally {
      if (taskQueues.get(task.notePath) === job) taskQueues.delete(task.notePath);
    }
  });
  taskQueues.set(task.notePath, job);
}

// ---------------------------------------------------------------------------
// Reminder deep link (t.me startapp payload) → open the note directly
// ---------------------------------------------------------------------------

function decodeStartParam(raw) {
  try {
    let base64 = String(raw).replace(/-/g, '+').replace(/_/g, '/');
    while (base64.length % 4) base64 += '=';
    const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
    return new TextDecoder().decode(bytes);
  } catch {
    return null;
  }
}

// Returns the note target encoded in the launch link: { repositoryId, path }
// for the new "note:<repoId>:<path>" payload (legacy "note:<path>" →
// repositoryId null, resolved to the active repository), or null.
function noteTargetFromStartParam() {
  const raw = tg()?.initDataUnsafe?.start_param;
  if (!raw) return null;
  const payload = decodeStartParam(raw);
  if (!payload || !payload.startsWith('note:')) return null;
  const body = payload.slice(5);
  const sep = body.indexOf(':');
  if (sep > 0) {
    const repositoryId = Number(body.slice(0, sep));
    const path = normPath(body.slice(sep + 1));
    if (Number.isInteger(repositoryId) && repositoryId > 0 && path) {
      return { repositoryId, path };
    }
  }
  const path = normPath(body);
  return path ? { repositoryId: null, path } : null;
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
      invalidateCaches();
      await loadFolder(state.currentPath);
      void refreshHeatmap();
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
  resetEditorDraft();
  await enterExplorer(state.currentPath);
}

// Clears the editor draft so a stale unsaved note can never leak into the
// next opened note (openEditor always reloads content anyway).
function resetEditorDraft() {
  state.editor = { mode: 'create', path: null, originalContent: null };
  editor.setValue('');
  els.notePath.value = '';
  els.editorStatus.hidden = true;
  els.previewBanner.hidden = true;
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
    invalidateCaches();
    void refreshSyncBadge();
    void refreshHeatmap();
    void refreshTasksData();
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
  // Version history is per-file: folders have no single content to restore.
  els.infoHistory.hidden = isDir;
  // Pinning works for notes and folders alike.
  els.infoPin.hidden = false;
  els.infoPinLabel.textContent = state.pinnedPaths?.has(normPath(item.path)) ? STRINGS.unpinAction : STRINGS.pinAction;

  els.infoModal.hidden = false;
}

function closeInfoModal() {
  els.infoModal.hidden = true;
  els.infoGithubLink.hidden = true;
}

els.infoClose.addEventListener('click', closeInfoModal);
els.infoBackdrop.addEventListener('click', closeInfoModal);
document.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') return;
  // History sits on top of the properties sheet — close it first.
  if (!els.historyModal.hidden) closeHistoryModal();
  else if (!els.infoModal.hidden) closeInfoModal();
});

// ---------------------------------------------------------------------------
// Pin / unpin from the properties sheet: the item floats to the top of its
// folder listing. The backend re-points pins on move and drops them on delete,
// so a toggle only flips the flag and refreshes the listing.
// ---------------------------------------------------------------------------

els.infoPin.addEventListener('click', () => {
  if (state.infoItem) void togglePin(state.infoItem);
});

async function togglePin(item) {
  const path = normPath(item.path);
  const isPinned = state.pinnedPaths?.has(path) ?? false;
  try {
    await (isPinned ? api.unpinItem(path) : api.pinItem(path));
    invalidatePins();
    haptic('success');
    showToast(isPinned ? STRINGS.unpinnedToast : STRINGS.pinnedToast);
    closeInfoModal();
    await loadFolder(state.currentPath);
  } catch (error) {
    showErrorToast(error);
  }
}

// ---------------------------------------------------------------------------
// History sheet: commit list for the note file with per-version preview and
// one-click restore (a new commit with the old content, via the sync queue).
// ---------------------------------------------------------------------------

els.infoHistory.addEventListener('click', () => {
  if (state.infoItem?.path) openHistoryModal(state.infoItem.path);
});

function formatHistoryDate(iso) {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString(getLang() === 'ru' ? 'ru-RU' : 'en-US', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

async function openHistoryModal(path) {
  state.historyPath = path;
  state.historySeq = (state.historySeq ?? 0) + 1;
  const seq = state.historySeq;

  els.historyPath.textContent = path;
  els.historyPending.hidden = true;
  els.historyList.textContent = '';
  const loading = document.createElement('div');
  loading.className = 'status-text';
  loading.textContent = STRINGS.loading;
  els.historyList.append(loading);
  els.historyModal.hidden = false;

  try {
    const data = await api.listNoteHistory(path);
    if (seq !== state.historySeq || els.historyModal.hidden) return;
    renderHistory(data);
  } catch (error) {
    if (seq !== state.historySeq || els.historyModal.hidden) return;
    els.historyList.textContent = '';
    const failed = document.createElement('div');
    failed.className = 'status-text';
    failed.textContent = friendlyErrorText(error);
    els.historyList.append(failed);
  }
}

function renderHistory(data) {
  els.historyList.textContent = '';
  els.historyPending.hidden = !data?.hasPendingChanges;

  const commits = Array.isArray(data?.commits) ? data.commits : [];
  if (!commits.length) {
    const empty = document.createElement('div');
    empty.className = 'status-text';
    empty.textContent = STRINGS.historyEmpty;
    els.historyList.append(empty);
    return;
  }
  for (const commit of commits) {
    els.historyList.append(buildHistoryItem(commit));
  }
}

function buildHistoryItem(commit) {
  const item = document.createElement('div');
  item.className = 'history-item';

  const head = document.createElement('button');
  head.type = 'button';
  head.className = 'history-head';

  const meta = document.createElement('div');
  meta.className = 'history-meta';
  if (commit.authorAvatarUrl) {
    const avatar = document.createElement('img');
    avatar.className = 'history-avatar';
    avatar.src = commit.authorAvatarUrl;
    avatar.alt = '';
    avatar.loading = 'lazy';
    meta.append(avatar);
  }
  const who = document.createElement('span');
  who.className = 'history-author';
  who.textContent = commit.authorLogin || commit.authorName || STRINGS.historyUnknownAuthor;
  const when = document.createElement('span');
  when.className = 'history-date';
  when.textContent = formatHistoryDate(commit.date);
  const sha = document.createElement('span');
  sha.className = 'history-sha';
  sha.textContent = (commit.sha || '').slice(0, 7);
  meta.append(who, when, sha);

  const message = document.createElement('div');
  message.className = 'history-message';
  message.textContent = commit.message || STRINGS.historyNoMessage;

  head.append(meta, message);

  const body = document.createElement('div');
  body.className = 'history-preview';
  body.hidden = true;

  head.addEventListener('click', () => void toggleHistoryPreview(head, body, commit));
  item.append(head, body);
  return item;
}

async function toggleHistoryPreview(head, body, commit) {
  const isOpen = !body.hidden;

  // Only one version stays expanded at a time.
  for (const other of els.historyList.querySelectorAll('.history-preview')) {
    if (other !== body) other.hidden = true;
  }
  for (const other of els.historyList.querySelectorAll('.history-head.open')) {
    if (other !== head) other.classList.remove('open');
  }

  body.hidden = isOpen;
  head.classList.toggle('open', !isOpen);
  if (isOpen || body.dataset.loading === '1') return;

  body.textContent = '';
  body.dataset.loading = '1';
  const loading = document.createElement('div');
  loading.className = 'status-text';
  loading.textContent = STRINGS.loading;
  body.append(loading);

  const path = state.historyPath;
  try {
    const data = await api.getNoteVersion(path, commit.sha);
    body.textContent = '';
    if (data?.content === null || data?.content === undefined) {
      const tooLarge = document.createElement('div');
      tooLarge.className = 'status-text';
      tooLarge.textContent = STRINGS.tooLarge;
      body.append(tooLarge);
      return;
    }
    const pre = document.createElement('pre');
    pre.className = 'history-pre';
    pre.textContent = data.content;

    const restoreBtn = document.createElement('button');
    restoreBtn.type = 'button';
    restoreBtn.className = 'btn btn-primary history-restore';
    const label = document.createElement('span');
    label.className = 'btn-label';
    label.textContent = STRINGS.historyRestore;
    restoreBtn.append(label);
    restoreBtn.addEventListener('click', () => void restoreVersion(path, commit, restoreBtn));

    body.append(pre, restoreBtn);
  } catch (error) {
    body.textContent = '';
    const failed = document.createElement('div');
    failed.className = 'status-text';
    failed.textContent = friendlyErrorText(error);
    body.append(failed);
  } finally {
    delete body.dataset.loading;
  }
}

async function restoreVersion(path, commit, btn) {
  const shortSha = (commit.sha || '').slice(0, 7);
  const ok = await showConfirm(STRINGS.historyRestoreConfirm(shortSha));
  if (!ok) return;

  setButtonBusy(btn, true, STRINGS.historyRestoring, STRINGS.historyRestore);
  try {
    await api.restoreNoteVersion(path, commit.sha);
    closeHistoryModal();
    showToast(STRINGS.historyRestored);
    invalidateCaches();
    void refreshSyncBadge();
    void refreshHeatmap();
    void refreshTasksData();

    // A note open in the editor must show the restored content, not the stale one.
    if (!els.views.editor.hidden && state.editor.mode === 'edit' && state.editor.path === path) {
      await loadNoteIntoEditor(path);
    }
  } catch (error) {
    setButtonBusy(btn, false, STRINGS.historyRestoring, STRINGS.historyRestore);
    showErrorToast(error);
  }
}

function closeHistoryModal() {
  els.historyModal.hidden = true;
  els.historyList.textContent = '';
  els.historyPending.hidden = true;
  state.historySeq = (state.historySeq ?? 0) + 1; // invalidate in-flight loads
}

els.historyClose.addEventListener('click', closeHistoryModal);
els.historyBackdrop.addEventListener('click', closeHistoryModal);

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
    invalidateCaches();
    void refreshSyncBadge();
    void refreshHeatmap();
    void refreshTasksData();
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
    invalidateCaches();
    haptic('success');
    showToast(isDir ? STRINGS.deletedFolder : STRINGS.deleted);
    void refreshSyncBadge();
    void refreshHeatmap();
    void refreshTasksData();
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
    await reloadRepositories();
    const active = state.repositories.find((repo) => repo.isActive) ?? state.repositories[0] ?? null;
    bindRepoForm(active ? active.id : null);
  } catch (error) {
    renderRepoList();
    setError(els.settingsError, error instanceof api.ApiError ? error.message : STRINGS.loadFailed);
  }
}

async function reloadRepositories() {
  state.repositories = await api.listRepositories();
  renderRepoList();
}

function repoLabel(repo) {
  return repo.displayName || `${repo.repositoryOwner}/${repo.repositoryName}`;
}

function renderRepoList() {
  const container = els.repoList;
  container.textContent = '';
  if (!state.repositories.length) {
    return; // the form below is bound to "new repository" mode
  }

  const pencilSvg = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a2.85 2.85 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/></svg>';

  for (const repo of state.repositories) {
    const row = document.createElement('div');
    row.className = 'repo-item' + (repo.isActive ? ' active' : '');

    const main = document.createElement('button');
    main.type = 'button';
    main.className = 'repo-item-main';
    if (!repo.isActive) {
      main.addEventListener('click', () => void switchToRepository(repo));
    }

    const nameEl = document.createElement('span');
    nameEl.className = 'repo-item-name';
    nameEl.textContent = repoLabel(repo);
    if (repo.isActive) {
      const check = document.createElement('span');
      check.className = 'repo-item-check';
      check.textContent = '✓';
      nameEl.append(check);
    }

    const subEl = document.createElement('span');
    subEl.className = 'repo-item-sub';
    subEl.textContent = `${repo.repositoryOwner}/${repo.repositoryName}`;

    main.append(nameEl, subEl);

    const editBtn = document.createElement('button');
    editBtn.type = 'button';
    editBtn.className = 'repo-item-edit';
    editBtn.innerHTML = pencilSvg;
    editBtn.setAttribute('aria-label', t('repoEditTitle'));
    editBtn.addEventListener('click', () => {
      haptic('select');
      bindRepoForm(repo.id);
    });

    row.append(main, editBtn);
    container.append(row);
  }
}

// Binds the repository edit form to a repository id (null = new repository).
function bindRepoForm(repoId) {
  state.repoFormRepoId = repoId;
  const repo = state.repositories.find((item) => item.id === repoId) ?? null;

  els.repoFormTitle.textContent = t(repo ? 'repoEditTitle' : 'repoNewTitle');
  els.settingsRepoName.value = repo ? (repo.displayName ?? '') : '';
  els.settingsOwner.value = repo?.repositoryOwner ?? '';
  els.settingsRepo.value = repo?.repositoryName ?? '';
  els.settingsToken.value = '';
  els.settingsInboxPath.value = repo?.inboxPath ?? 'inbox';
  els.settingsAttachmentsPath.value = repo?.attachmentsPath ?? 'inbox/attachments';
  els.repoFormActions.hidden = !repo;
}

// Activates another repository: drops all explorer/editor state that belongs
// to the previous one, then reloads the notes list.
async function switchToRepository(repo) {
  if (!els.views.editor.hidden) {
    if (isEditorDirty() && !(await showConfirm(STRINGS.confirmRepoSwitchDirty))) {
      return;
    }
    mainButton.hide();
  }
  resetEditorDraft();
  try {
    await api.activateRepository(repo.id);
  } catch (error) {
    showErrorToast(error);
    return;
  }
  state.repositories = state.repositories.map((item) => ({ ...item, isActive: item.id === repo.id }));
  state.currentPath = '';
  els.searchInput.value = '';
  hideSearchResults();
  invalidateCaches();
  renderRepoList();
  bindRepoForm(repo.id);
  haptic('success');
  showToast(STRINGS.repoSwitched(repoLabel(repo)));
  void refreshSyncBadge();
  void refreshTasksData();
}

async function saveSettings() {
  setError(els.settingsError, '');
  const displayName = els.settingsRepoName.value.trim();
  const owner = els.settingsOwner.value.trim();
  const repo = els.settingsRepo.value.trim();
  const token = els.settingsToken.value.trim();
  const inboxPath = els.settingsInboxPath.value.trim().replace(/^\/+|\/+$/g, '');
  const attachmentsPath = els.settingsAttachmentsPath.value.trim().replace(/^\/+|\/+$/g, '');
  if (!owner || !repo) {
    setError(els.settingsError, STRINGS.fillAllFields);
    return;
  }

  const editingId = state.repoFormRepoId;
  const wasActive = state.repositories.find((item) => item.isActive)?.id;
  try {
    if (editingId == null) {
      if (!token) {
        setError(els.settingsError, STRINGS.repoTokenRequired);
        return;
      }
      const created = await api.createRepository({
        displayName: displayName || undefined,
        githubToken: token,
        repositoryOwner: owner,
        repositoryName: repo,
        inboxPath: inboxPath || undefined,
        attachmentsPath: attachmentsPath || undefined,
      });
      localStorage.setItem('gitenberg.autosave', els.settingsAutosave.checked ? '1' : '0');
      localStorage.setItem('gitenberg.autosync', els.settingsAutosync.checked ? '1' : '0');
      localStorage.setItem('gitenberg.autosync.interval', els.settingsAutosyncInterval.value);
      restartAutosyncTimer();
      await reloadRepositories();
      bindRepoForm(created.id);
      haptic('success');
      showToast(STRINGS.repoCreated);
      return;
    }

    await api.updateRepository(editingId, {
      displayName: displayName || undefined,
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
    await reloadRepositories();
    bindRepoForm(editingId);
    haptic('success');
    showToast(STRINGS.saved);
    if (wasActive === editingId) {
      // The active repository may point somewhere else now — start from the root.
      state.currentPath = '';
      invalidateCaches();
    }
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
els.btnRepoAdd.addEventListener('click', () => {
  haptic('select');
  bindRepoForm(null);
  els.settingsRepoName.focus();
});
els.btnRepoCancelEdit.addEventListener('click', () => {
  const active = state.repositories.find((repo) => repo.isActive) ?? state.repositories[0] ?? null;
  bindRepoForm(active ? active.id : null);
});
els.btnRepoDelete.addEventListener('click', () => void deleteCurrentRepoForm());

async function deleteCurrentRepoForm() {
  const repoId = state.repoFormRepoId;
  if (repoId == null) return;
  const repo = state.repositories.find((item) => item.id === repoId);
  if (!repo) return;

  const ok = await showConfirm(STRINGS.confirmRepoDelete(repoLabel(repo)));
  if (!ok) return;
  try {
    await api.deleteRepository(repoId);
    state.currentPath = '';
    els.searchInput.value = '';
    hideSearchResults();
    invalidateCaches();
    await reloadRepositories();
    const active = state.repositories.find((item) => item.isActive) ?? state.repositories[0] ?? null;
    bindRepoForm(active ? active.id : null);
    haptic('success');
    showToast(STRINGS.repoDeleted);
    void refreshSyncBadge();
    void refreshTasksData();
  } catch (error) {
    if (error instanceof api.ApiError && error.status === 400) {
      setError(els.settingsError, STRINGS.repoCannotDeleteLast);
    } else {
      setError(els.settingsError, error instanceof api.ApiError ? error.message : STRINGS.loadFailed);
    }
    haptic('error');
  }
}

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
      invalidateCaches();
      if (!els.views.explorer.hidden) await loadFolder(state.currentPath);
      void refreshHeatmap();
      void refreshTasksData();
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
  state.currentPath = '';
  els.explorerTabs.hidden = false;
  els.activityCard.hidden = true;
  applyActivityCollapsed();
  showTab(state.currentTab);
  skeletonRows();
  setStatus(STRINGS.loading);

  // Reminder messages deep-link into a note via the startapp payload; the
  // payload may also carry the repository the note lives in.
  const deepLink = noteTargetFromStartParam();

  try {
    const [items, pinnedPaths] = await Promise.all([api.listNotes(), getPinnedPaths()]);
    renderNotes(Array.isArray(items) ? items : [], pinnedPaths);
    state.currentPath = '';
    renderBreadcrumbs('');
    updateBackButton();
    void refreshHeatmap();
    void refreshTasksData();

    if (deepLink) {
      try {
        if (deepLink.repositoryId) {
          // Make sure the note's repository is active before opening it.
          await api.activateRepository(deepLink.repositoryId);
          invalidateCaches();
        }
        // Quiet existence check so a stale link doesn't flash an error toast.
        await api.getNoteContent(deepLink.path);
        openEditor('edit', deepLink.path);
      } catch {
        /* note is gone — stay in the explorer */
      }
    }
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
