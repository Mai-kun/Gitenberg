// Gitenberg API client.
//
// Auth model (must match WebAuthFilter on the backend): a signed-in browser
// holds an HTTP-only session cookie set by POST /api/auth/login. Requests
// carry no credentials explicitly — same-origin cookies are attached by the
// browser automatically; a 401 answer means "sign in again".

export class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

function authHeaders() {
  // The client's UTC offset (JS getTimezoneOffset semantics: UTC+3 → -180)
  // lets the backend bucket activity into the user's local day.
  return { 'X-Timezone-Offset': String(new Date().getTimezoneOffset()) };
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
    credentials: 'same-origin',
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

// ---------------------------------------------------------------------------
// Session auth: sign in with a GitHub token (the backend validates it against
// GitHub, binds a repository on first use and sets the session cookie)
// ---------------------------------------------------------------------------

export function login(githubToken, repositoryOwner, repositoryName, { inboxPath, attachmentsPath } = {}) {
  return request('POST', '/api/auth/login', {
    body: {
      gitHubToken: githubToken,
      repositoryOwner,
      repositoryName,
      ...(inboxPath ? { inboxPath } : {}),
      ...(attachmentsPath ? { attachmentsPath } : {}),
    },
  });
}

export function logout() {
  return request('POST', '/api/auth/logout');
}

export function getSession() {
  return request('GET', '/api/auth/session');
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

// Public share links: create-or-get is idempotent — the same note keeps its
// URL until the link is revoked.
export function createShareLink(path) {
  return request('POST', '/api/notes/share', { body: { path } });
}

export function getShareLink(path) {
  return request('GET', '/api/notes/share', { params: { path } });
}

export function revokeShareLink(token) {
  return request('DELETE', `/api/notes/share/${encodeURIComponent(token)}`);
}

// Version history: every edit is a commit, so history is the commit list of
// the file; restoring queues a save op with the old content (a new commit).
export function listNoteHistory(path) {
  return request('GET', '/api/notes/history', { params: { path } });
}

export function getNoteVersion(path, sha) {
  return request('GET', '/api/notes/history/content', { params: { path, sha } });
}

export function restoreNoteVersion(path, sha) {
  return request('POST', '/api/notes/history/restore', { body: { path, sha } });
}

export function searchNotes(query) {
  return request('GET', '/api/notes/search', { params: { query } });
}

// Note templates: .md files of the repository's templates/ folder, each with
// its content so a new note can be filled from one request.
export function listTemplates() {
  return request('GET', '/api/templates');
}

// Pins (per active repository; the server normalizes the path).
export function listPins() {
  return request('GET', '/api/pins');
}

export function pinItem(path) {
  return request('PUT', '/api/pins', { body: { path } });
}

export function unpinItem(path) {
  return request('DELETE', '/api/pins', { params: { path } });
}

export function syncStatus() {
  return request('GET', '/api/sync/status');
}

export function syncNow() {
  return request('POST', '/api/sync');
}

// from/to: optional 'YYYY-MM-DD' bounds; without them the backend returns the
// last ~year of daily counts.
export function getActivityHeatmap({ from, to } = {}) {
  return request('GET', '/api/activity/heatmap', { params: { from, to } });
}

export function getTasks() {
  return request('GET', '/api/notes/tasks');
}

// Whole-vault link graph: notes as nodes, resolved [[WikiLink]]s as edges.
export function getGraph() {
  return request('GET', '/api/notes/graph');
}

// ---------------------------------------------------------------------------
// Multi-repo: manage the user's repositories and switch the active one
// ---------------------------------------------------------------------------

export function listRepositories() {
  return request('GET', '/api/repositories');
}

export function createRepository({ displayName, githubToken, repositoryOwner, repositoryName, inboxPath, attachmentsPath }) {
  return request('POST', '/api/repositories', {
    body: {
      ...(displayName ? { displayName } : {}),
      githubToken,
      repositoryOwner,
      repositoryName,
      ...(inboxPath ? { inboxPath } : {}),
      ...(attachmentsPath ? { attachmentsPath } : {}),
    },
  });
}

export function updateRepository(id, { displayName, githubToken, repositoryOwner, repositoryName, inboxPath, attachmentsPath }) {
  return request('PUT', `/api/repositories/${id}`, {
    body: {
      ...(displayName ? { displayName } : {}),
      ...(githubToken ? { githubToken } : {}),
      repositoryOwner,
      repositoryName,
      ...(inboxPath ? { inboxPath } : {}),
      ...(attachmentsPath ? { attachmentsPath } : {}),
    },
  });
}

export function deleteRepository(id) {
  return request('DELETE', `/api/repositories/${id}`);
}

export function activateRepository(id) {
  return request('POST', `/api/repositories/${id}/activate`);
}

// Top-level folders of a repository — storage-folder suggestions for the
// registration and settings forms. Either githubToken (typed into the form)
// or repositoryId (stored token is used) must be provided.
export function listRepositoryFolders({ repositoryOwner, repositoryName, githubToken, repositoryId }) {
  return request('POST', '/api/repositories/folders', {
    body: {
      repositoryOwner,
      repositoryName,
      ...(githubToken ? { githubToken } : {}),
      ...(repositoryId !== undefined && repositoryId !== null ? { repositoryId } : {}),
    },
  });
}

// Binary download — request() assumes JSON, so this is a separate code path.
// Returns { blob, fileName } with the server-provided file name when present.
export async function downloadExportArchive() {
  const response = await fetch(buildUrl('/api/export/archive'), {
    headers: authHeaders(),
    credentials: 'same-origin',
    cache: 'no-store',
  });

  if (!response.ok) {
    throw new ApiError(response.status, await extractError(response));
  }

  const blob = await response.blob();
  const disposition = response.headers.get('Content-Disposition') || '';
  const match = disposition.match(/filename\*?=(?:UTF-8'')?"?([^";]+)"?/i);
  let fileName = 'gitenberg-export.zip';
  if (match) {
    try {
      fileName = decodeURIComponent(match[1]);
    } catch {
      fileName = match[1];
    }
  }
  return { blob, fileName };
}
