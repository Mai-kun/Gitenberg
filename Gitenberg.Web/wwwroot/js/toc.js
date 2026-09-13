// Note outline (table of contents): a bottom sheet listing the note's
// headings. In preview mode the list mirrors the rendered preview DOM —
// a tap scrolls the preview; in edit mode headings are parsed from the
// markdown source and a tap jumps CodeMirror to the heading line.

import { t } from './i18n.js';
import * as editor from './editor.js';

// ATX headings of the markdown source, skipping YAML frontmatter and fenced
// code blocks (setext headings and headings inside blockquotes are left out —
// the preview path lists what is actually rendered instead). Returns
// [{ level, text, line }], line = 0-based index into the source.
export function extractHeadingsFromSource(markdown) {
  const lines = String(markdown ?? '').split(/\r?\n/);

  // YAML frontmatter is metadata, not content: start after the closing ---.
  // An unterminated opening --- is an ordinary rule, not frontmatter.
  let start = 0;
  if (/^---\s*$/.test(lines[0] ?? '')) {
    let close = 1;
    while (close < lines.length && !/^---\s*$/.test(lines[close])) close += 1;
    if (close < lines.length) start = close + 1;
  }

  const items = [];
  let fence = null; // { char, len } of the currently open ``` / ~~~ block
  for (let i = start; i < lines.length; i += 1) {
    if (!fence) {
      const open = lines[i].match(/^ {0,3}(`{3,}|~{3,})/);
      if (open) {
        fence = { char: open[1][0], len: open[1].length };
        continue;
      }
    } else {
      const close = lines[i].match(/^ {0,3}(`{3,}|~{3,})\s*$/);
      if (close && close[1][0] === fence.char && close[1].length >= fence.len) fence = null;
      continue;
    }
    const heading = lines[i].match(/^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$/);
    if (heading) items.push({ level: heading[1].length, text: heading[2].trim(), line: i });
  }
  return items;
}

// Headings of the active EasyMDE full preview, in document order — guaranteed
// to match what is on screen. Returns [{ level, text, el }], or null when the
// preview is not open.
export function collectHeadingsFromPreview() {
  const preview = document.querySelector('#editor-container .editor-preview-full');
  if (!preview) return null;
  return [...preview.querySelectorAll('h1, h2, h3, h4, h5, h6')]
    .map((el) => ({ level: Number(el.tagName[1]), text: (el.textContent || '').trim(), el }))
    .filter((item) => item.text);
}

// The list source follows the current editor surface: the rendered preview
// when it is open, the raw markdown otherwise. Every item carries its jump().
// Jumping is instant on purpose: some WebViews
// included) silently drop behavior:'smooth' programmatic scrolls, which would
// make a tap feel dead.
function tocItems() {
  if (editor.isPreviewActive()) {
    return (collectHeadingsFromPreview() ?? []).map((item) => ({
      level: item.level,
      text: item.text,
      jump: () => item.el.scrollIntoView({ block: 'start' }),
    }));
  }
  return extractHeadingsFromSource(editor.getValue()).map((item) => ({
    level: item.level,
    text: item.text,
    jump: () => editor.scrollToLine(item.line),
  }));
}

function renderTocList(items) {
  const list = document.getElementById('toc-list');
  list.textContent = '';

  if (!items.length) {
    const empty = document.createElement('p');
    empty.className = 'toc-empty';
    empty.textContent = t('tocEmpty');
    list.append(empty);
    return;
  }

  for (const item of items) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = `toc-item toc-level-${item.level}`;
    btn.textContent = item.text;
    btn.addEventListener('click', () => {
      closeToc();
      item.jump();
    });
    list.append(btn);
  }
}

function openToc() {
  renderTocList(tocItems());
  document.getElementById('toc-modal').hidden = false;
}

function closeToc() {
  document.getElementById('toc-modal').hidden = true;
}

export function initToc() {
  document.getElementById('btn-editor-toc').addEventListener('click', openToc);
  document.getElementById('toc-close').addEventListener('click', closeToc);
  document.getElementById('toc-backdrop').addEventListener('click', closeToc);
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !document.getElementById('toc-modal').hidden) closeToc();
  });
}
