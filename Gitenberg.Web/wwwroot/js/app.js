// Gitenberg Mini App: SPA state, screen routing (Register / Explorer / Editor)
// and Telegram WebApp integration.

import * as api from './api.js';
import * as editor from './editor.js';

// ---------------------------------------------------------------------------
// UI strings (Russian)
// ---------------------------------------------------------------------------

const STRINGS = {
  appName: 'Gitenberg',
  explorerTitle: 'Заметки',
  rootCrumb: 'Корень',
  searchPlaceholder: 'Поиск по заметкам…',
  searchHint: 'Поисковый индекс обновляется примерно раз в час — новые заметки могут появиться не сразу.',
  loading: 'Загрузка…',
  emptyFolder: 'Папка пуста. Создайте заметку кнопкой «+».',
  emptySearch: 'Ничего не найдено.',
  registerFailed: 'Не удалось подключить GitHub',
  confirmDelete: (path) => `Удалить заметку «${path}»?\n\nФайл будет удалён из репозитория GitHub.`,
  confirmDiscard: 'Есть несохранённые изменения. Выйти без сохранения?',
  confirmLeaveRegister: 'Выйти без сохранения настроек?',
  saved: 'Заметка сохранена',
  deleted: 'Заметка удалена',
  errorPrefix: 'Ошибка',
  registerBtnBusy: 'Подключение…',
  registerBtnIdle: 'Подключить GitHub',
  saveBtnBusy: 'Сохранение…',
  saveBtnIdle: 'Сохранить',
  invalidPath: 'Укажите имя файла (например, notes/idea.md).',
  tooLarge: 'Файл слишком большой (> 1 МБ) — содержимое недоступно для редактирования.',
  loadFailed: 'Не удалось загрузить данные',
  telegramIdInvalid: 'Укажите корректный Telegram ID (положительное число).',
};

const FOLDER_ICON = '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M10 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-2z"/></svg>';
const FILE_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M9 13h6M9 17h6"/></svg>';
const TRASH_ICON = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/></svg>';

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

function showConfirm(message) {
  const webApp = tg();
  if (webApp?.showConfirm) {
    return new Promise((resolve) => webApp.showConfirm(message, resolve));
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
  registerError: $('register-error'),
  registerSubmit: $('register-submit'),
  explorerTitle: $('explorer-title'),
  btnCreateNote: $('btn-create-note'),
  fabCreate: $('fab-create'),
  breadcrumbs: $('breadcrumbs'),
  searchInput: $('search-input'),
  searchClear: $('search-clear'),
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
};

const state = {
  currentPath: '', // '' = repo root
  searchSeq: 0, // guards against out-of-order search responses
  editor: {
    mode: 'create', // 'create' | 'edit'
    path: null, // original path in edit mode
    originalContent: null,
  },
};

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
  const message = error instanceof api.ApiError ? error.message : STRINGS.errorPrefix;
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
  if (!token || !owner || !repo) {
    setError(els.registerError, 'Заполните все поля.');
    return;
  }

  setButtonBusy(els.registerSubmit, true, STRINGS.registerBtnBusy, STRINGS.registerBtnIdle);
  try {
    await api.registerUser(currentTelegramId(), token, owner, repo);
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

  const sorted = [...items].sort((a, b) => {
    const aDir = a.type === 'Dir' ? 0 : 1;
    const bDir = b.type === 'Dir' ? 0 : 1;
    if (aDir !== bDir) return aDir - bDir;
    return String(a.name).localeCompare(String(b.name), 'ru', { sensitivity: 'base' });
  });

  for (const item of sorted) {
    const isDir = item.type === 'Dir';
    const isMd = /\.md$/i.test(String(item.name));

    const row = document.createElement('button');
    row.type = 'button';
    row.className = `note-row ${isDir ? 'is-dir' : 'is-file'}`;

    const icon = document.createElement('span');
    icon.className = 'note-icon';
    icon.innerHTML = isDir ? FOLDER_ICON : FILE_ICON;

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

    if (!isDir) {
      const del = document.createElement('span');
      del.className = 'note-delete';
      del.setAttribute('role', 'button');
      del.setAttribute('aria-label', `Удалить ${item.name}`);
      del.innerHTML = TRASH_ICON;
      del.addEventListener('click', (event) => {
        event.stopPropagation();
        void confirmAndDeleteNote(item.path, item.name);
      });
      row.append(del);
    }

    if (isDir) {
      row.addEventListener('click', () => enterExplorer(item.path));
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
// Search
// ---------------------------------------------------------------------------

function hideSearchResults() {
  els.searchResults.hidden = true;
  els.searchResults.textContent = '';
}

async function runSearch(query) {
  const seq = (state.searchSeq += 1);
  els.searchClear.hidden = query === '';

  if (query === '') {
    if (seq === state.searchSeq) hideSearchResults();
    return;
  }

  try {
    const results = await api.searchNotes(query);
    if (seq !== state.searchSeq) return;

    els.searchResults.hidden = false;
    els.searchResults.textContent = '';

    if (!results.length) {
      const empty = document.createElement('div');
      empty.className = 'status-text';
      empty.textContent = STRINGS.emptySearch;
      els.searchResults.append(empty);
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
      card.addEventListener('click', () => openEditor('edit', result.notePath));
      els.searchResults.append(card);
    }
  } catch (error) {
    if (seq !== state.searchSeq) return;
    els.searchResults.hidden = false;
    els.searchResults.textContent = '';
    const failed = document.createElement('div');
    failed.className = 'status-text';
    failed.textContent = error instanceof api.ApiError ? error.message : STRINGS.loadFailed;
    els.searchResults.append(failed);
  }
}

let searchTimer = null;
els.searchInput.addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => void runSearch(els.searchInput.value.trim()), 350);
});

els.searchClear.addEventListener('click', () => {
  els.searchInput.value = '';
  clearTimeout(searchTimer);
  void runSearch('');
  els.searchInput.focus();
});

// ---------------------------------------------------------------------------
// Editor view
// ---------------------------------------------------------------------------

function openEditor(mode, path) {
  state.editor = { mode, path: path ?? null, originalContent: null };
  els.editorTitle.textContent = mode === 'edit'
    ? path.split('/').pop()
    : 'Новая заметка';
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
    await api.saveNote(path, content);
    haptic('success');
    showToast(STRINGS.saved);
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

async function confirmAndDeleteNote(path, displayName) {
  const ok = await showConfirm(STRINGS.confirmDelete(displayName ?? path));
  if (!ok) return;
  try {
    await api.deleteNote(path);
    haptic('success');
    showToast(STRINGS.deleted);
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
// Wiring & startup
// ---------------------------------------------------------------------------

els.btnCreateNote.addEventListener('click', () => {
  haptic('select');
  openEditor('create');
});
els.fabCreate.addEventListener('click', () => {
  haptic('select');
  openEditor('create');
});

els.btnEditorBack.addEventListener('click', () => void leaveEditor());
els.btnEditorDelete.addEventListener('click', () => {
  if (state.editor.mode === 'edit' && state.editor.path) {
    void confirmAndDeleteNote(state.editor.path, state.editor.path.split('/').pop());
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
      setStatus(els.devBar.hidden ? STRINGS.loadFailed : 'Укажите тестовый Telegram ID в панели сверху.');
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
void bootstrap();
