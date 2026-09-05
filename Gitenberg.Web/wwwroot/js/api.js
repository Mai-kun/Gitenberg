// Gitenberg API client.
//
// Auth model (must match TelegramAuthFilter on the backend):
//  - Inside Telegram (initData present): send "Authorization: tma <initData>".
//  - Outside Telegram (local development): omit Authorization entirely and send
//    "X-Telegram-Id" instead. A header of "tma " with an empty payload would fail
//    initData validation and return 401 even in Development — the dev fallback in
//    the backend only activates when the Authorization header is ABSENT.

const DEV_ID_STORAGE_KEY = 'gitenberg.devTelegramId';

export class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

function tg() {
  return window.Telegram?.WebApp ?? null;
}

export function isInsideTelegram() {
  const initData = tg()?.initData;
  return typeof initData === 'string' && initData.length > 0;
}

export function getDevTelegramId() {
  const stored = localStorage.getItem(DEV_ID_STORAGE_KEY);
  if (stored && Number.isFinite(Number(stored)) && Number(stored) > 0) {
    return Number(stored);
  }
  return 123456;
}

export function setDevTelegramId(id) {
  localStorage.setItem(DEV_ID_STORAGE_KEY, String(id));
}

function authHeaders() {
  if (isInsideTelegram()) {
    return { Authorization: `tma ${tg().initData}` };
  }
  return { 'X-Telegram-Id': String(getDevTelegramId()) };
}

function buildUrl(path, params) {
  const url = new URL(path, window.location.origin);
  for (const [key, value] of Object.entries(params || {})) {
    if (value !== undefined && value !== null && value !== '') {
      url.searchParams.set(key, String(value));
    }
  }
  return url.toString();
}

async function extractError(response) {
  let text = '';
  try {
    text = await response.text();
  } catch {
    /* body unavailable */
  }

  const fallback = `HTTP ${response.status}: ${response.statusText || ''}`.trim()
    || 'Неизвестная ошибка';

  if (!text) return fallback;

  try {
    const data = JSON.parse(text);
    if (data && typeof data === 'object') {
      // Prefer ProblemDetails detail/title, then custom error/message payloads.
      const message =
        (typeof data.detail === 'string' && data.detail) ||
        (typeof data.title === 'string' && data.title) ||
        (typeof data.error === 'string' && data.error) ||
        (typeof data.message === 'string' && data.message) ||
        (typeof data.Error === 'string' && data.Error) ||
        (typeof data.Message === 'string' && data.Message);
      if (message) return message;
    }
  } catch {
    /* not JSON — fall through to raw text */
  }

  // Avoid surfacing a bare "Ошибка" with no useful context.
  const trimmed = text.trim();
  if (!trimmed || trimmed === 'Ошибка') return fallback;
  return trimmed;
}

async function request(method, path, { params, body } = {}) {
  const response = await fetch(buildUrl(path, params), {
    method,
    headers: {
      ...authHeaders(),
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
    cache: 'no-store',
  });

  if (!response.ok) {
    throw new ApiError(response.status, await extractError(response));
  }

  if (response.status === 204) return null;
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

// ---------------------------------------------------------------------------
// Typed helpers (exact backend contract, camelCase wire names)
// ---------------------------------------------------------------------------

export function registerUser(telegramId, githubToken, repositoryOwner, repositoryName) {
  return request('POST', '/api/register', {
    body: { telegramId, githubToken, repositoryOwner, repositoryName },
  });
}

export function listNotes(path) {
  return request('GET', '/api/notes', { params: { path } });
}

export function getNoteContent(path) {
  return request('GET', '/api/notes/content', { params: { path } });
}

export function saveNote(path, content, commitMessage) {
  return request('POST', '/api/notes', {
    body: { path, content, ...(commitMessage ? { commitMessage } : {}) },
  });
}

export function moveNote(fromPath, toPath, content, commitMessage) {
  return request('POST', '/api/notes/move', {
    body: {
      fromPath,
      toPath,
      ...(content !== undefined && content !== null ? { content } : {}),
      ...(commitMessage ? { commitMessage } : {}),
    },
  });
}

export function deleteNote(path, commitMessage) {
  return request('DELETE', '/api/notes', {
    params: { path, ...(commitMessage ? { commitMessage } : {}) },
  });
}

export function searchNotes(query) {
  return request('GET', '/api/notes/search', { params: { query } });
}

export function syncStatus() {
  return request('GET', '/api/sync/status');
}

export function syncNow() {
  return request('POST', '/api/sync');
}

export function getSettings() {
  return request('GET', '/api/register');
}

export function saveSettings({ githubToken, repositoryOwner, repositoryName }) {
  return request('POST', '/api/register', {
    body: {
      ...(githubToken ? { githubToken } : {}),
      repositoryOwner,
      repositoryName,
    },
  });
}
