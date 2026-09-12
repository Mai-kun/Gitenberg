// Shared Markdown preview pipeline: the Obsidian-style extensions rendered
// both by the editor preview and by the public share page (/share/{token}).
// The base markdown → HTML step is injected by the caller (EasyMDE's bundled
// marked in the editor, standalone marked on the share page), everything here
// runs on marked's HTML output.

import { t } from './i18n.js';

// ==text== → <mark>, [[target]] → wiki link, #tag → tag chip, key:: value →
// inline field. Runs on marked's HTML output, so # of headings never matches.
export function enhancePreviewHtml(html, { dataview = true } = {}) {
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
  // (title + <br> + body) or split it into two paragraphs. Nested callouts
  // (a blockquote inside a blockquote) are handled innermost-first.
  out = renderCallouts(out);

  // Dataview queries: replace the code block with a placeholder that the
  // async renderer fills in (the editor's previewRender is synchronous). The
  // share page has no authenticated API, so there the query stays a code block.
  if (dataview) {
    out = out.replace(/<pre><code class="language-dataview">([\s\S]*?)<\/code><\/pre>/g, (_, escaped) => {
      const query = decodeEntities(escaped).trim();
      return `<div class="dataview-block" data-pending data-query="${encodeURIComponent(query)}">${t('dataviewLoading')}</div>`;
    });
  }

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

// Convert > [!type] blockquotes to themed callout boxes. Innermost
// blockquotes are processed first, so nested callouts (> > [!warning] inside
// > [!info]) already exist as divs when their parent is rendered.
function renderCallouts(html) {
  let changed = true;
  while (changed) {
    changed = false;
    html = html.replace(
      /<blockquote>((?:(?!<\/?blockquote>)[\s\S])*?)<\/blockquote>/g,
      (whole, inner) => {
        const start = inner.match(/^\s*<p>\s*\[!(\w+)\]/);
        if (!start) return whole; // an ordinary quote — keep it

        const type = start[1].toLowerCase();
        const icon = CALLOUT_ICONS[type] || 'ℹ️';
        // marked keeps single newlines inside <p> as raw "\n"; without this a
        // multi-line callout puts its first body line into the title. Turn
        // them into <br> so lines stay separated.
        const rest = inner.slice(start[0].length).replace(/\r?\n/g, '<br>');
        const brIdx = rest.search(/<br\s*\/?>/);
        const pEnd = rest.search(/<\/p>/);
        let title;
        let body;
        if (brIdx !== -1 && (pEnd === -1 || brIdx < pEnd)) {
          title = rest.slice(0, brIdx);
          body = rest.slice(brIdx).replace(/^<br\s*\/?>/, '');
          // The callout's own <p> was consumed with the title, so the first
          // </p> in the body is its stray closer (it precedes nested blocks).
          const strayEnd = body.search(/<\/p>/);
          if (strayEnd !== -1) body = body.slice(0, strayEnd) + body.slice(strayEnd + 4);
        } else if (pEnd !== -1) {
          title = rest.slice(0, pEnd);
          body = rest.slice(pEnd + 4);
        } else {
          title = rest;
          body = '';
        }
        // Newlines converted to <br> that sit on block boundaries (before or
        // right after a nested callout / paragraph) are just whitespace.
        body = body.replace(/<br>\s*(?=<(?:div|p|blockquote|ul|ol|table|pre|h[1-6])[\s>])/gi, '');
        body = body.replace(/(<\/(?:div|p|blockquote|ul|ol|table|pre|h[1-6])>)\s*(?:<br>\s*)+/gi, '$1');
        changed = true;
        return `<div class="callout callout-${type}"><div class="callout-title">`
          + `<span class="callout-icon">${icon}</span>${title.trim() || type}</div>`
          + `<div class="callout-body">${body.trim()}</div></div>`;
      }
    );
  }
  return html;
}

// YAML frontmatter of regular notes: rendered as a metadata card above the
// note (tags inside become clickable chips). Kanban boards handle their own
// frontmatter earlier in the pipeline.
export function extractFrontmatter(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/);
  if (!m) return null;
  return { yaml: m[1], body: m[2] };
}

export function renderFrontMatterHtml(yaml) {
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

export function renderKanbanHtml(boardText) {
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

export function escapeHtmlText(text) {
  return String(text)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
}

function escapeAttr(text) {
  return escapeHtmlText(text).replaceAll('"', '&quot;');
}

// Extract a kanban board from frontmatter-only notes
// (--- / kanban-plugin: board / --- followed by ## columns).
export function extractFrontmatterBoard(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/);
  if (!m) return null;
  if (!/^kanban-plugin:\s*board\b/mi.test(m[1])) return null;
  return m[2];
}

// The full preview pipeline shared by the editor and the share page:
// whole-file kanban → frontmatter card → fenced kanban placeholders →
// base markdown → Obsidian-style enhancements → kanban restoration.
// markdownFn turns markdown text into base HTML. Returns the final HTML and,
// when the note is a kanban board, how the board is stored in the source
// ('frontmatter' | 'fenced') — the editor needs that for write-back.
export function renderMarkdownPipeline(text, markdownFn, { dataview = true } = {}) {
  // Whole-file kanban via frontmatter (Obsidian Kanban plugin format).
  const frontBoard = extractFrontmatterBoard(text);
  if (frontBoard !== null) {
    return { html: sanitizePreviewHtml(renderKanbanHtml(frontBoard)), boardType: 'frontmatter' };
  }

  // Regular YAML frontmatter → metadata card; the rest renders normally.
  let frontMatterHtml = '';
  let body = text;
  const front = extractFrontmatter(text);
  if (front) {
    frontMatterHtml = renderFrontMatterHtml(front.yaml);
    body = front.body;
  }

  // ```kanban fenced blocks must not go through marked as plain code.
  const kanbanBlocks = [];
  body = body.replace(/```kanban\r?\n([\s\S]*?)```/gi, (_, blockBody) => {
    kanbanBlocks.push(blockBody);
    return `@@KANBAN_BLOCK_${kanbanBlocks.length - 1}@@`;
  });

  let html = markdownFn(body);
  html = enhancePreviewHtml(html, { dataview });

  // Restore rendered kanban blocks (marked may wrap the placeholder in <p>).
  html = html.replace(/(?:<p>)?@@KANBAN_BLOCK_(\d+)@@(?:<\/p>)?/g, (_, i) =>
    renderKanbanHtml(kanbanBlocks[Number(i)]));

  return { html: sanitizePreviewHtml(frontMatterHtml + html), boardType: kanbanBlocks.length ? 'fenced' : null };
}

// ---------------------------------------------------------------------------
// HTML sanitizer for rendered previews
// ---------------------------------------------------------------------------

// Raw HTML inside a note must never become live markup: a note can carry
// third-party text (forwarded messages), and the public share page renders it
// for strangers — an <img src=x onerror=…> would execute in any visitor's
// browser and inside the Mini App too. An allowlist keeps everything this
// pipeline itself generates (wiki-links, tag chips, callouts, kanban cards,
// dataview blocks, tables, task-list checkboxes); unknown tags are unwrapped,
// script-bearing tags are dropped with their content, and href/src may not
// use script schemes.
const SANITIZE_ALLOWED_TAGS = new Set([
  'p', 'br', 'hr', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
  'ul', 'ol', 'li', 'blockquote', 'pre', 'code',
  'em', 'strong', 'b', 'i', 's', 'del', 'u', 'sup', 'sub', 'kbd', 'mark',
  'a', 'img', 'span', 'div', 'dl', 'dt', 'dd',
  'table', 'thead', 'tbody', 'tfoot', 'tr', 'th', 'td',
  'input', 'button',
]);
const SANITIZE_DROP_WITH_CONTENT = new Set([
  'script', 'style', 'iframe', 'object', 'embed', 'noscript', 'template',
  'svg', 'math', 'form', 'base', 'meta', 'link', 'title', 'head',
]);
const SANITIZE_ALLOWED_ATTRS = new Set([
  'href', 'src', 'alt', 'title', 'class', 'align', 'colspan', 'rowspan',
  'start', 'type', 'checked', 'disabled', 'loading', 'draggable',
]);
const SANITIZE_URL_ATTRS = new Set(['href', 'src']);

function isSafeUrl(value, isImage) {
  // Whitespace inside the scheme ("java\tscript:…") must not slip through —
  // compare on a stripped copy. Entities are already decoded by DOMParser.
  const normalized = String(value).trim().toLowerCase().replace(/\s+/g, '');
  if (/^(javascript|vbscript):/.test(normalized)) return false;
  if (normalized.startsWith('data:')) return isImage && normalized.startsWith('data:image/');
  return true;
}

export function sanitizePreviewHtml(html) {
  // Parsed documents are detached: nothing executes or loads during the walk.
  const doc = new DOMParser().parseFromString(String(html ?? ''), 'text/html');
  sanitizeNode(doc.body);
  return doc.body.innerHTML;
}

function sanitizeNode(root) {
  for (const node of [...root.childNodes]) {
    if (node.nodeType === Node.COMMENT_NODE) {
      node.remove();
      continue;
    }
    if (node.nodeType !== Node.ELEMENT_NODE) continue;

    const tag = node.tagName.toLowerCase();
    if (SANITIZE_DROP_WITH_CONTENT.has(tag)) {
      node.remove();
      continue;
    }
    if (!SANITIZE_ALLOWED_TAGS.has(tag)) {
      // Harmless unknown tag — keep its content, drop the wrapper.
      sanitizeNode(node);
      node.replaceWith(...node.childNodes);
      continue;
    }

    for (const attr of [...node.attributes]) {
      const name = attr.name.toLowerCase();
      if (!SANITIZE_ALLOWED_ATTRS.has(name) && !name.startsWith('data-')) {
        node.removeAttribute(attr.name);
        continue;
      }
      if (SANITIZE_URL_ATTRS.has(name) && !isSafeUrl(attr.value, tag === 'img')) {
        node.removeAttribute(attr.name);
      }
    }
    if (tag === 'input') {
      // Only task-list checkboxes make sense in a note: neutralize anything else.
      node.setAttribute('type', 'checkbox');
      node.setAttribute('disabled', '');
    }
    sanitizeNode(node);
  }
}
