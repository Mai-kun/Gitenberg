// EasyMDE wrapper: lazily creates a single editor instance on the #note-markdown
// textarea and reuses it for every note (re-initializing EasyMDE on the same
// element leaks DOM and codemirror instances).

import { t } from './i18n.js';
import { openMdHelp } from './md-help.js';
import * as api from './api.js';
import { getVaultIndex } from './vault.js';

let instance = null;

// ---------------------------------------------------------------------------
// Obsidian-style preview extensions
// ---------------------------------------------------------------------------

// ==text== → <mark>, [[target]] → wiki link, #tag → tag chip, key:: value →
// inline field. Runs on marked's HTML output, so # of headings never matches.
function enhancePreviewHtml(html) {
  let out = html;

  // Highlight (must run before tag regex — ==x== has no '#').
  out = out.replace(/==([^<>\n]+?)==/g, '<mark>$1</mark>');

  // Wikilinks [[target|label]].
  out = out.replace(/\[\[([^\]<>\n]+?)\]\]/g, (_, raw) => {
    const [pathPart, labelPart] = raw.split('|');
    const target = pathPart.trim().replace(/\.md$/i, '');
    const label = labelPart || target;
    return `<a href="#" class="wiki-link" data-wiki-target="${encodeURIComponent(target)}">${label}</a>`;
  });

  // Tags: #слово/с-дефисами, not inside HTML tags or code. Look-behind for a
  // tag boundary (start, whitespace or closing bracket), then the markup
  // itself is safe: heading '#'s never survive into marked's HTML.
  out = out.replace(/(^|[\s>(])#([A-Za-zА-Яа-яЁё0-9][A-Za-zА-Яа-яЁё0-9_/-]*)/g,
    (m, prefix, tag) => `${prefix}<a href="#" class="md-tag" data-tag="${encodeURIComponent(tag)}">#${tag}</a>`);

  // Inline Dataview fields: "key:: value" at line starts.
  out = out.replace(/(^|<p>|<br>)\s*([A-Za-zА-Яа-яЁё0-9_-]+)::([^<>\n]*?)(?=<br>|<\/p>|$)/g,
    (m, prefix, key, value) => `${prefix}<span class="dv-field"><span class="dv-key">${key}::</span> ${value.trim()}</span>`);

  // Obsidian callouts: > [!info] Title / > body — replace the blockquote
  // with a themed box. marked may keep the whole callout in one <p>
  // (title + <br> + body) or split it into two paragraphs.
  out = out.replace(/<blockquote>\s*<p>\s*\[!(\w+)\]([\s\S]*?)<\/blockquote>/g,
    (_, type, rest) => {
      const t = type.toLowerCase();
      const icon = CALLOUT_ICONS[t] || 'ℹ️';
      const brIdx = rest.search(/<br\s*\/?>/);
      const pEnd = rest.search(/<\/p>/);
      let title;
      let body;
      if (brIdx !== -1 && (pEnd === -1 || brIdx < pEnd)) {
        title = rest.slice(0, brIdx);
        body = rest.slice(brIdx).replace(/^<br\s*\/?>/, '');
      } else if (pEnd !== -1) {
        title = rest.slice(0, pEnd);
        body = rest.slice(pEnd + 4);
      } else {
        title = rest;
        body = '';
      }
      return `<div class="callout callout-${t}"><div class="callout-title">`
        + `<span class="callout-icon">${icon}</span>${title.trim() || type}</div>`
        + `<div class="callout-body">${body.trim()}</div></div>`;
    });

  // Dataview queries: replace the code block with a placeholder that the
  // async renderer fills in (previewRender is synchronous).
  out = out.replace(/<pre><code class="language-dataview">([\s\S]*?)<\/code><\/pre>/g, (_, escaped) => {
    const query = decodeEntities(escaped).trim();
    return `<div class="dataview-block" data-pending data-query="${encodeURIComponent(query)}">${t('dataviewLoading')}</div>`;
  });

  return out;
}

function decodeEntities(text) {
  const el = document.createElement('textarea');
  el.innerHTML = text;
  return el.value;
}

const CALLOUT_ICONS = {
  info: 'ℹ️', note: '📝', question: '❓', help: '❓', faq: '❓',
  warning: '⚠️', caution: '⚠️', attention: '⚠️',
  danger: '🚨', error: '🚨', bug: '🐛',
  tip: '💡', hint: '💡', important: '💡',
  success: '✅', check: '✅', done: '✅',
  failure: '❌', fail: '❌', missing: '❌',
  example: '📋', quote: '❝', cite: '❝', abstract: '📋', summary: '📋', todo: '☑️',
};

// YAML frontmatter of regular notes: rendered as a metadata card above the
// note (tags inside become clickable chips). Kanban boards handle their own
// frontmatter earlier in the pipeline.
function extractFrontmatter(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/);
  if (!m) return null;
  return { yaml: m[1], body: m[2] };
}

function renderFrontMatterHtml(yaml) {
  const entries = [];
  let lastKey = null;
  for (const raw of yaml.split(/\r?\n/)) {
    if (!raw.trim()) continue;
    const kv = raw.match(/^([A-Za-zА-Яа-яЁё0-9_-]+):\s*(.*)$/);
    if (kv && !/^\s/.test(raw)) {
      lastKey = kv[1];
      entries.push({ key: lastKey, value: kv[2].trim(), list: [] });
      continue;
    }
    const listItem = raw.match(/^\s*-\s+(.*)$/);
    if (listItem && lastKey && entries.length) {
      entries[entries.length - 1].list.push(listItem[1].trim());
    }
  }

  if (!entries.length) return '';
  const rows = entries.map((e) => {
    if (e.key.toLowerCase() === 'tags') {
      const tags = [...e.list, ...(e.value ? e.value.split(/[,\s]+/).filter(Boolean) : [])];
      if (tags.length) {
        const chips = tags.map((tag) => {
          const clean = tag.replace(/^#/, '');
          return `<a href="#" class="md-tag" data-tag="${encodeURIComponent(clean)}">#${escapeHtmlText(clean)}</a>`;
        }).join(' ');
        return `<div class="fm-row"><span class="fm-key">tags:</span> ${chips}</div>`;
      }
    }
    const value = [...e.list, ...(e.value ? [e.value] : [])].map((v) => escapeHtmlText(v)).join(', ');
    return `<div class="fm-row"><span class="fm-key">${escapeHtmlText(e.key)}:</span> ${value}</div>`;
  }).join('');
  return `<div class="front-matter">${rows}</div>`;
}

// ---------------------------------------------------------------------------
// Kanban boards (Obsidian Kanban compatible)
// ---------------------------------------------------------------------------

// "## Column" sections with "- [ ] card" / "- [x] done" items; indented
// "> ..." lines under a card are its hidden description (not shown on the
// board, only a badge). Whole-file boards come from frontmatter
// "kanban-plugin: board", inline ones from ```kanban fenced blocks.
function parseKanbanBoard(boardText) {
  const columns = [];
  let current = null;
  for (const rawLine of boardText.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) continue;
    const heading = line.match(/^#{1,6}\s+(.+)$/);
    if (heading) {
      current = { title: heading[1], cards: [] };
      columns.push(current);
      continue;
    }
    if (!current) {
      current = { title: '', cards: [] };
      columns.push(current);
    }
    const descMatch = rawLine.match(/^\s*>\s?(.*)$/);
    if (descMatch && current.cards.length > 0) {
      const last = current.cards[current.cards.length - 1];
      last.desc = last.desc ? `${last.desc}\n${descMatch[1]}` : descMatch[1];
      continue;
    }
    const card = line.match(/^-\s+\[( |x)\]\s+(.*)$/i) || line.match(/^-\s+(.*)$/);
    if (card) {
      const hasCheckbox = /^-\s+\[/i.test(line);
      const done = hasCheckbox && card[1].toLowerCase() === 'x';
      const text = hasCheckbox ? card[2] : card[1];
      current.cards.push({ text: text.trim(), done, hasCheckbox, desc: '' });
    }
  }
  return columns;
}

function renderKanbanHtml(boardText) {
  const columns = parseKanbanBoard(boardText);
  const cols = columns.map((col, colIndex) => {
    const cards = col.cards.map((c, cardIndex) => {
      const mark = c.hasCheckbox
        ? `<span class="kanban-check${c.done ? ' done' : ''}">${c.done ? '✓' : ''}</span>`
        : '';
      const info = c.desc
        ? `<button type="button" class="kanban-info" data-col="${colIndex}" data-card="${cardIndex}" title="${t('kanbanHasDesc')}">ℹ</button>`
        : '';
      return `<div class="kanban-card${c.done ? ' is-done' : ''}" draggable="true"`
        + ` data-col="${colIndex}" data-card="${cardIndex}" data-done="${c.done ? 1 : 0}"`
        + ` data-checkbox="${c.hasCheckbox ? 1 : 0}" data-text="${escapeAttr(c.text)}"`
        + ` data-desc="${escapeAttr(c.desc)}">`
        + `${mark}<span class="kanban-card-text">${escapeHtmlText(c.text)}</span>${info}</div>`;
    }).join('');
    return `<div class="kanban-col" data-col="${colIndex}">`
      + `<div class="kanban-col-title">${escapeHtmlText(col.title)}</div>${cards}`
      + `<button type="button" class="kanban-add" data-col="${colIndex}">＋ ${t('kanbanAddCard')}</button>`
      + `</div>`;
  }).join('');

  return `<div class="kanban">${cols}</div>`;
}

function escapeHtmlText(text) {
  return String(text)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
}

function escapeAttr(text) {
  return escapeHtmlText(text).replaceAll('"', '&quot;');
}

function boardModelFromDom() {
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
const dataviewContentCache = new Map(); // path → { at, fields }
const DATACACHE_TTL_MS = 120_000;

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

// Extract a kanban board from frontmatter-only notes
// (--- / kanban-plugin: board / --- followed by ## columns).
function extractFrontmatterBoard(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/);
  if (!m) return null;
  if (!/^kanban-plugin:\s*board\b/mi.test(m[1])) return null;
  return m[2];
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
      let text = plainText;

      // Whole-file kanban via frontmatter (Obsidian Kanban plugin format).
      const frontBoard = extractFrontmatterBoard(text);
      if (frontBoard !== null) {
        currentBoardType = 'frontmatter';
        return renderKanbanHtml(frontBoard);
      }

      // Regular YAML frontmatter → metadata card; the rest renders normally.
      let frontMatterHtml = '';
      const front = extractFrontmatter(text);
      if (front) {
        frontMatterHtml = renderFrontMatterHtml(front.yaml);
        text = front.body;
      }

      // ```kanban fenced blocks must not go through marked as plain code.
      const kanbanBlocks = [];
      text = text.replace(/```kanban\r?\n([\s\S]*?)```/gi, (_, body) => {
        kanbanBlocks.push(body);
        return `@@KANBAN_BLOCK_${kanbanBlocks.length - 1}@@`;
      });

      currentBoardType = kanbanBlocks.length ? 'fenced' : null;

      let html = mde.markdown(text);
      html = enhancePreviewHtml(html);

      // Restore rendered kanban blocks (marked may wrap the placeholder in <p>).
      html = html.replace(/(?:<p>)?@@KANBAN_BLOCK_(\d+)@@(?:<\/p>)?/g, (_, i) =>
        renderKanbanHtml(kanbanBlocks[Number(i)]));

      scheduleDataviewFill();
      return frontMatterHtml + html;
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
