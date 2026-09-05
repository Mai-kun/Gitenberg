// In-app Markdown reference. The editor toolbar's help button opens this
// sheet instead of an external website, so the listed syntax matches exactly
// what the built-in preview renders. Rows come from i18n, so every newly
// supported feature is listed here automatically.

import { t } from './i18n.js';

export function openMdHelp() {
  const list = document.getElementById('md-help-list');
  list.textContent = '';
  for (const [label, syntax] of t('helpRows')) {
    const dt = document.createElement('dt');
    dt.textContent = label;
    const dd = document.createElement('dd');
    const code = document.createElement('code');
    code.textContent = syntax;
    dd.append(code);
    list.append(dt, dd);
  }
  document.getElementById('md-help-modal').hidden = false;
}

export function closeMdHelp() {
  document.getElementById('md-help-modal').hidden = true;
}

document.getElementById('md-help-close').addEventListener('click', closeMdHelp);
document.getElementById('md-help-backdrop').addEventListener('click', closeMdHelp);
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !document.getElementById('md-help-modal').hidden) closeMdHelp();
});
