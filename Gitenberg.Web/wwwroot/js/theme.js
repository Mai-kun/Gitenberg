// Theme switching: a data-theme attribute on <html> overrides the OS palette
// with a fixed one; "auto" follows the system light/dark preference and
// re-applies when it changes. index.html carries an inline pre-paint snippet
// with the same storage key — keep both in sync.

const STORAGE_KEY = 'gitenberg.theme';

export const THEMES = [
  'auto', 'light', 'dark', 'sepia', 'nord',
  'dracula', 'solar-light', 'solar-dark', 'black',
];

export function getTheme() {
  const stored = localStorage.getItem(STORAGE_KEY);
  return THEMES.includes(stored) ? stored : 'auto';
}

export function isDarkTheme(id) {
  if (id === 'dark' || id === 'nord' || id === 'dracula' || id === 'solar-dark' || id === 'black') return true;
  if (id !== 'auto') return false;
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
}

export function applyTheme(id) {
  const theme = THEMES.includes(id) ? id : 'auto';
  if (theme === 'auto') {
    delete document.documentElement.dataset.theme;
  } else {
    document.documentElement.dataset.theme = theme;
  }

  // highlight.js ships two fixed palettes: pick the one matching the surface.
  const dark = isDarkTheme(theme);
  const lightSheet = document.getElementById('hljs-theme-light');
  const darkSheet = document.getElementById('hljs-theme-dark');
  if (lightSheet) lightSheet.disabled = dark;
  if (darkSheet) darkSheet.disabled = !dark;
}

export function setTheme(id) {
  applyTheme(id);
  if (THEMES.includes(id)) localStorage.setItem(STORAGE_KEY, id);
}

// "auto" tracks the OS preference live; the media query listener makes a
// theme change in the OS visible without a reload.
window.matchMedia?.('(prefers-color-scheme: dark)')
  .addEventListener?.('change', () => {
    if (getTheme() === 'auto') applyTheme('auto');
  });
