// EasyMDE wrapper: lazily creates a single editor instance on the #note-markdown
// textarea and reuses it for every note (re-initializing EasyMDE on the same
// element leaks DOM and codemirror instances).

import { t } from './i18n.js';
import { openMdHelp } from './md-help.js';
import * as api from './api.js';
import { getVaultIndex } from './vault.js';
import { renderMarkdownPipeline } from './md-preview.js';

let instance = null;

// --- Dataview (minimal): LIST / TABLE [fields] [FROM #tag | FROM "folder"] ---
const dataviewContentCache = new Map(); // path → { at, fields }
const DATACACHE_TTL_MS = 120_000;
  const columns = [];
  document.querySelectorAll('.editor-preview .kanban-col').forEach((colEl) => {
    const col = { title: colEl.querySelector('.kanban-col-title')?.textContent ?? '', cards: [] };
    colEl.querySelectorAll('.kanban-card').forEach((cardEl) => {
      col.cards.push({
        text: cardEl.dataset.text ?? '',
        done: cardEl.dataset.done === '1',
        hasCheckbox: cardEl.dataset.checkbox === '1',
        desc: cardEl.dataset.desc || '',
      });
    });
    columns.push(col);
  });
  return columns;
}

function boardMarkdownFromModel(columns) {
  return columns.map((col) => {
    const lines = [`## ${col.title}`];
    for (const c of col.cards) {
      lines.push(`- [${c.done ? 'x' : ' '}] ${c.text}`);
      if (c.desc) {
        for (const line of c.desc.split('\n')) lines.push(`  > ${line}`);
      }
    }
    return lines.join('\n');
  }).join('\n\n');
}

// How the current preview's board is stored in the source note.
let currentBoardType = null; // 'frontmatter' | 'fenced' | null

function writeBoardToSource(boardMarkdown) {
  if (!instance || !currentBoardType) return;
  const cm = instance.codemirror;
  const text = cm.getValue();

  if (currentBoardType === 'fenced') {
    const fenced = /```kanban\r?\n[\s\S]*?```/i.exec(text);
    if (fenced) {
      cm.setValue(text.slice(0, fenced.index) + '```kanban\n' + boardMarkdown + '\n```' + text.slice(fenced.index + fenced[0].length));
    }
  } else if (currentBoardType === 'frontmatter') {
    const head = /^---\r?\n[\s\S]*?\r?\n---\r?\n?/.exec(text);
    if (head) {
      cm.setValue(head[0] + boardMarkdown + '\n');
    }
  }
  cm.focus();
}

function showBoardToast(message) {
  const toast = document.getElementById('toast');
  if (!toast) return;
  toast.textContent = message;
  toast.hidden = false;
  clearTimeout(showBoardToast._timer);
  showBoardToast._timer = setTimeout(() => {
    toast.hidden = true;
  }, 1800);
}

// --- Kanban card modal state ---
const kanbanModal = {
  colIndex: null, // target column for new cards
  cardIndex: null, // null = new card
};

function openKanbanCardModal(colIndex, cardIndex) {
  const modal = document.getElementById('kanban-modal');
  const columns = boardModelFromDom();
  const col = columns[colIndex];
  if (!col) return;
  kanbanModal.colIndex = colIndex;
  kanbanModal.cardIndex = cardIndex;

  const isNew = cardIndex == null;
  const card = isNew ? { text: '', done: false, desc: '' } : col.cards[cardIndex];
  document.getElementById('kanban-title').textContent =
    isNew ? t('kanbanAddCard') : t('kanbanCardTitle');
  document.getElementById('kanban-card-text').value = card.text;
  document.getElementById('kanban-card-desc').value = card.desc || '';
  document.getElementById('kanban-card-done').checked = card.hasCheckbox && card.done;
  document.getElementById('kanban-card-done-field').hidden = isNew;
  document.getElementById('kanban-delete').hidden = isNew;
  document.getElementById('kanban-error').hidden = true;
  modal.hidden = false;
  document.getElementById('kanban-card-text').focus();
}

function closeKanbanCardModal() {
  document.getElementById('kanban-modal').hidden = true;
  kanbanModal.colIndex = null;
  kanbanModal.cardIndex = null;
}

function saveKanbanCard() {
  const text = document.getElementById('kanban-card-text').value.trim();
  if (!text) {
    const err = document.getElementById('kanban-error');
    err.textContent = t('invalidName');
    err.hidden = false;
    return;
  }
  const desc = document.getElementById('kanban-card-desc').value.replace(/\r?\n/g, '\n').trim();
  const doneChecked = document.getElementById('kanban-card-done').checked;

  const columns = boardModelFromDom();
  const col = columns[kanbanModal.colIndex];
  if (!col) return closeKanbanCardModal();

  if (kanbanModal.cardIndex == null) {
    col.cards.push({ text, done: false, hasCheckbox: true, desc });
  } else {
    const card = col.cards[kanbanModal.cardIndex];
    if (card) {
      card.text = text;
      card.desc = desc;
      if (card.hasCheckbox) card.done = doneChecked;
    }
  }
  closeKanbanCardModal();
  writeBoardToSource(boardMarkdownFromModel(columns));
}

function deleteKanbanCard() {
  if (kanbanModal.cardIndex == null) return closeKanbanCardModal();
  const columns = boardModelFromDom();
  const col = columns[kanbanModal.colIndex];
  if (col) col.cards.splice(kanbanModal.cardIndex, 1);
  closeKanbanCardModal();
  writeBoardToSource(boardMarkdownFromModel(columns));
}

// --- Dataview (minimal): LIST / TABLE [fields] [FROM #tag | FROM "folder"] ---
async function fetchNoteFields(path) {
  const cached = dataviewContentCache.get(path);
  if (cached && Date.now() - cached.at < DATACACHE_TTL_MS) return cached.fields;
  let text = '';
  try {
    const data = await api.getNoteContent(path);
    text = String(data?.content ?? '');
  } catch {
    text = '';
  }
  const fields = {};
  for (const line of text.split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Za-zА-Яа-яЁё0-9_-]+)::\s*(.+)$/);
    if (m) fields[m[1].toLowerCase()] = m[2].trim();
  }
  dataviewContentCache.set(path, { at: Date.now(), fields });
  return fields;
}

function parseDataviewQuery(query) {
  // TABLE field1, field2 FROM "folder" | LIST FROM #tag
  const mode = /^LIST\b/i.test(query) ? 'list' : /^TABLE\b/i.test(query) ? 'table' : null;
  if (!mode) return null;
  const fromMatch = query.match(/FROM\s+(#("[^"]+"|[\w/-]+)|"[^"]+")/i);
  let filter = null;
  if (fromMatch) {
    const spec = fromMatch[1];
    filter = spec.startsWith('#')
      ? { kind: 'tag', value: spec.slice(1).replace(/^"|"$/g, '').toLowerCase() }
      : { kind: 'folder', value: spec.replace(/^"|"$/g, '').replace(/\/?$/, '/') };
  }
  let fields = [];
  if (mode === 'table') {
    const body = query.slice(query.toUpperCase().indexOf('TABLE') + 5, fromMatch ? fromMatch.index : undefined);
    fields = body.split(',').map((f) => f.trim()).filter(Boolean);
  }
  return { mode, fields, filter };
}

async function runDataviewQuery(container) {
  const query = decodeURIComponent(container.dataset.query || '');
  const parsed = parseDataviewQuery(query);
  if (!parsed) {
    container.textContent = t('dataviewUnsupported');
    return;
  }

  const index = await getVaultIndex();
  let notes = index.notes;
  if (parsed.filter?.kind === 'folder') {
    notes = notes.filter((n) => n.path.toLowerCase().startsWith(parsed.filter.value.toLowerCase()));
  }

  const rows = [];
  let budget = 60;
  for (const note of notes) {
    if (budget <= 0) break;
    budget -= 1;
    const fields = await fetchNoteFields(note.path);
    if (parsed.filter?.kind === 'tag') {
      const escaped = parsed.filter.value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const tagRe = new RegExp(`(^|\\s)#${escaped}\\b`, 'i');
      const fieldsText = Object.values(fields).join(' ');
      // Tag match requires content; only fetch text when fields don't match.
      if (!tagRe.test(fieldsText)) {
        try {
          const data = await api.getNoteContent(note.path);
          if (!tagRe.test(String(data?.content ?? ''))) continue;
        } catch {
          continue;
        }
      }
    }
    rows.push({ note, fields });
  }

  if (!rows.length) {
    container.textContent = t('dataviewEmpty');
    return;
  }

  if (parsed.mode === 'list') {
    const ul = document.createElement('ul');
    ul.className = 'dv-list';
    for (const r of rows) {
      const li = document.createElement('li');
      li.append(makeWikiLink(r.note.path));
      ul.append(li);
    }
    container.textContent = '';
    container.append(ul);
    return;
  }

  const table = document.createElement('table');
  table.className = 'dv-table';
  const thead = document.createElement('thead');
  const headRow = document.createElement('tr');
  for (const name of ['...', ...parsed.fields]) {
    const th = document.createElement('th');
    th.textContent = name;
    headRow.append(th);
  }
  thead.append(headRow);
  table.append(thead);
  const tbody = document.createElement('tbody');
  for (const r of rows) {
    const tr = document.createElement('tr');
    const tdName = document.createElement('td');
    tdName.append(makeWikiLink(r.note.path));
    tr.append(tdName);
    for (const f of parsed.fields) {
      const td = document.createElement('td');
      td.textContent = r.fields[f.toLowerCase()] ?? '';
      tr.append(td);
    }
    tbody.append(tr);
  }
  table.append(tbody);
  container.textContent = '';
  container.append(table);
}

function makeWikiLink(path) {
  const a = document.createElement('a');
  a.href = '#';
  a.className = 'wiki-link';
  a.dataset.wikiTarget = encodeURIComponent(path.replace(/\.md$/i, ''));
  a.textContent = path.split('/').pop().replace(/\.md$/i, '');
  return a;
}

function scheduleDataviewFill() {
  setTimeout(() => {
    document.querySelectorAll('.editor-preview .dataview-block[data-pending]').forEach((el) => {
      el.removeAttribute('data-pending');
      runDataviewQuery(el).catch(() => {
        el.textContent = t('dataviewUnsupported');
      });
    });
  }, 0);
}

// ---------------------------------------------------------------------------
// Editor instance
// ---------------------------------------------------------------------------

function create() {
  const textarea = document.getElementById('note-markdown');

  instance = new EasyMDE({
    element: textarea,
    // EasyMDE 2.18 throws "Cannot create property 'timeFormat' on boolean
    // 'false'" when autosave is a bare false — it must be an object.
    autosave: { enabled: false },
    autofocus: false,
    spellChecker: false,
    status: false,
    renderingConfig: {
      // Requires hljs (loaded in index.html) — colors code in the preview.
      codeSyntaxHighlighting: true,
    },
    placeholder: t('editorPlaceholder'),
    toolbar: [
      'bold', 'italic', 'strikethrough', '|',
      'heading-1', 'heading-2', '|',
      'unordered-list', 'ordered-list', '|',
      'code', 'quote', '|',
      {
        // Note picker instead of the useless "[ ](https://)" template.
        name: 'noteLink',
        action: () => document.dispatchEvent(new CustomEvent('link-picker-open')),
        title: t('toolbarLink'),
        className: 'fa fa-link',
      },
      {
        name: 'wikilink',
        action: (editor) => {
          editor.codemirror.replaceSelection('[[]]');
          const cm = editor.codemirror;
          cm.setCursor({ line: cm.getCursor().line, ch: cm.getCursor().ch - 2 });
          cm.focus();
        },
        title: t('toolbarWikilink'),
        className: 'fa fa-book',
      },
      '|',
      'preview', '|',
      {
        name: 'guide',
        // In-app reference instead of an external website: the guide must
        // list only what this app's preview actually renders. noDisable
        // keeps the button clickable while preview mode is active.
        action: () => openMdHelp(),
        title: t('toolbarGuide'),
        className: 'fa fa-question-circle',
        noDisable: true,
      },
    ],
    previewRender(plainText) {
      // "this.parent" is the EasyMDE instance (see EasyMDE's default hook).
      const mde = this.parent;
      const { html, boardType } = renderMarkdownPipeline(plainText, (text) => mde.markdown(text));
      currentBoardType = boardType;
      scheduleDataviewFill();
      return html;
    },
  });

  // EasyMDE's full-screen preview renders only on toggle — it has no change
  // listener (unlike side-by-side). Kanban interactions write back to the
  // source, so the preview must follow: re-render on change while active.
  let previewUpdateTimer = null;
  instance.codemirror.on('change', () => {
    if (!isPreviewActive()) return;
    clearTimeout(previewUpdateTimer);
    previewUpdateTimer = setTimeout(() => {
      const preview = instance.codemirror.getWrapperElement().querySelector('.editor-preview-full');
      if (!preview) return;
      preview.innerHTML = instance.options.previewRender(instance.value(), preview);
      scheduleDataviewFill();
    }, 250);
  });

  // Preview state changes (the 👁 button) → notify app.js for the banner.
  document.getElementById('editor-container').addEventListener('click', (event) => {
    const toolbar = event.target.closest('.editor-toolbar');
    if (toolbar && event.target.closest('button.preview')) {
      setTimeout(() => {
        document.dispatchEvent(new CustomEvent('preview-toggled', {
          detail: { active: isPreviewActive() },
        }));
      }, 50);
    }
  });

  // Wikilinks and tags inside the preview: wiki links open the target note,
  // tags trigger a search. Resolution is app.js's job; we announce intents.
  document.getElementById('editor-container').addEventListener('click', (event) => {
    const link = event.target.closest('a.wiki-link');
    if (link) {
      event.preventDefault();
      try {
        const target = decodeURIComponent(link.dataset.wikiTarget);
        document.dispatchEvent(new CustomEvent('wiki-open', { detail: { target } }));
      } catch {
        /* malformed target — ignore */
      }
      return;
    }
    const tag = event.target.closest('a.md-tag');
    if (tag) {
      event.preventDefault();
      try {
        document.dispatchEvent(new CustomEvent('tag-click', { detail: { tag: decodeURIComponent(tag.dataset.tag) } }));
      } catch {
        /* malformed tag — ignore */
      }
      return;
    }
    const addBtn = event.target.closest('.kanban-add');
    if (addBtn) {
      event.preventDefault();
      openKanbanCardModal(Number(addBtn.dataset.col), null);
      return;
    }
    const infoBtn = event.target.closest('.kanban-info');
    if (infoBtn) {
      event.preventDefault();
      openKanbanCardModal(Number(infoBtn.dataset.col), Number(infoBtn.dataset.card));
      return;
    }
    const cardEl = event.target.closest('.kanban-card');
    if (cardEl) {
      // Any card click opens its editor (add/view description, delete, done).
      event.preventDefault();
      openKanbanCardModal(Number(cardEl.dataset.col), Number(cardEl.dataset.card));
    }
  });

  // Kanban drag & drop: move cards between columns (and reorder within one).
  const container = document.getElementById('editor-container');
  container.addEventListener('dragstart', (event) => {
    const card = event.target.closest('.kanban-card');
    if (!card) return;
    event.dataTransfer.setData('text/plain', JSON.stringify({
      col: Number(card.dataset.col),
      card: Number(card.dataset.card),
    }));
    event.dataTransfer.effectAllowed = 'move';
    card.classList.add('is-dragging');
  });
  container.addEventListener('dragend', (event) => {
    event.target.closest('.kanban-card')?.classList.remove('is-dragging');
    document.querySelectorAll('.kanban-col.drag-over').forEach((el) => el.classList.remove('drag-over'));
  });
  container.addEventListener('dragover', (event) => {
    const col = event.target.closest('.kanban-col');
    if (!col) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
    col.classList.add('drag-over');
  });
  container.addEventListener('drop', (event) => {
    const colEl = event.target.closest('.kanban-col');
    if (!colEl) return;
    event.preventDefault();
    let payload;
    try {
      payload = JSON.parse(event.dataTransfer.getData('text/plain'));
    } catch {
      return;
    }
    if (!Number.isInteger(payload.col) || !Number.isInteger(payload.card)) return;

    const columns = boardModelFromDom();
    const source = columns[payload.col];
    const card = source?.cards[payload.card];
    if (!card) return;
    source.cards.splice(payload.card, 1);
    columns[Number(colEl.dataset.col)].cards.push(card);
    writeBoardToSource(boardMarkdownFromModel(columns));
  });

  // Kanban card modal wiring
  document.getElementById('kanban-save').addEventListener('click', saveKanbanCard);
  document.getElementById('kanban-delete').addEventListener('click', deleteKanbanCard);
  document.getElementById('kanban-cancel').addEventListener('click', closeKanbanCardModal);
  document.getElementById('kanban-backdrop').addEventListener('click', closeKanbanCardModal);
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !document.getElementById('kanban-modal').hidden) closeKanbanCardModal();
  });

  return instance;
}

export function get() {
  return instance ?? create();
}

export function getValue() {
  return instance ? instance.value() : '';
}

export function setValue(value) {
  get().value(value ?? '');
  instance.codemirror.refresh();
}

export function clear() {
  if (instance) {
    instance.value('');
    instance.codemirror.refresh();
  }
}

export function insertAtCursor(text) {
  get().codemirror.replaceSelection(text);
  instance.codemirror.focus();
}

export function getSelection() {
  return instance ? String(instance.codemirror.getSelection() || '') : '';
}

export function isPreviewActive() {
  return instance ? Boolean(instance.isPreviewActive()) : false;
}

export function exitPreview() {
  if (instance && instance.isPreviewActive()) {
    instance.togglePreview();
  }
}
