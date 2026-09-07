// Public share page: renders the note behind a /share/{token} link in
// read-only mode, using the same preview pipeline as the Mini App editor.
// No Telegram SDK, no authentication — the unguessable token is the credential.

import { renderMarkdownPipeline } from './md-preview.js';
import { initI18n, t } from './i18n.js';

const STRINGS = new Proxy({}, { get: (_, key) => t(key) });

function getToken() {
  const match = window.location.pathname.match(/^\/share\/([^/?#]+)\/?$/i);
  return match ? decodeURIComponent(match[1]) : '';
}

function setState(text, { error = false } = {}) {
  const state = document.getElementById('share-state');
  state.classList.toggle('is-error', error);
  document.getElementById('share-state-text').textContent = text;
}

function showError(message) {
  setState(message, { error: true });
}

function render(data) {
  document.title = `${data.title ?? 'Note'} · Gitenberg`;
  document.getElementById('share-title').textContent = String(data.title ?? '');

  const container = document.querySelector('#editor-container .editor-preview');
  // dataview: false — queries need the authenticated API, so they stay code blocks.
  const { html } = renderMarkdownPipeline(
    String(data.content ?? ''),
    (text) => window.marked.parse(text),
    { dataview: false },
  );
  container.innerHTML = html;

  // Code coloring, as EasyMDE's codeSyntaxHighlighting does in the editor.
  container.querySelectorAll('pre code').forEach((el) => {
    try {
      window.hljs?.highlightElement(el);
    } catch {
      /* unknown language — leave uncolored */
    }
  });

  // Wiki links and tags only make sense inside the owner's vault.
  container.addEventListener('click', (event) => {
    if (event.target.closest('a.wiki-link, a.md-tag')) {
      event.preventDefault();
    }
  });

  document.getElementById('share-state').hidden = true;
  document.getElementById('share-note').hidden = false;
}

async function load() {
  initI18n();

  const token = getToken();
  if (!token) {
    showError(STRINGS.shareNotFound);
    return;
  }

  try {
    const response = await fetch(`/api/share/${encodeURIComponent(token)}`, { cache: 'no-store' });
    if (response.status === 404) {
      showError(STRINGS.shareNotFound);
      return;
    }
    if (!response.ok) {
      showError(STRINGS.shareError);
      return;
    }
    render(await response.json());
  } catch {
    showError(STRINGS.networkError);
  }
}

void load();
