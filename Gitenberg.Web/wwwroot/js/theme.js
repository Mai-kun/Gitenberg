// Theme switching: a data-theme attribute on <html> overrides the Telegram
// palette with a fixed one; "auto" keeps following the Telegram client (or
// the OS light/dark preference outside Telegram). index.html carries an
// inline pre-paint snippet with the same storage key — keep both in sync.

const STORAGE_KEY = 'gitenberg.theme';

export const THEMES = [
  'auto', 'light', 'dark', 'sepia', 'nord',
  'dracula', 'solar-light', 'solar-dark', 'black',
];

// Background color handed to the Telegram header/body chrome so the native
// UI around the Mini App matches the chosen palette.
const CHROME_BG = {
  light: '#ffffff',
  dark: '#17212b',
  sepia: '#f6edda',
  nord: '#2e3440',
  dracula: '#282a36',
  'solar-light': '#fdf6e3',
  'solar-dark': '#002b36',
  black: '#000000',
};

export function getTheme() {
  const stored = localStorage.getItem(STORAGE_KEY);
  return THEMES.includes(stored) ? stored : 'auto';
}

export function isDarkTheme(id) {
  if (id === 'dark' || id === 'nord' || id === 'dracula' || id === 'solar-dark' || id === 'black') return true;
  if (id !== 'auto') return false;
  const colorScheme = window.Telegram?.WebApp?.colorScheme;
  if (colorScheme === 'dark' || colorScheme === 'light') return colorScheme === 'dark';
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

  syncTelegramChrome(theme);
}

function syncTelegramChrome(theme) {
  const webApp = window.Telegram?.WebApp;
  if (!webApp || webApp.platform === 'unknown') return;
  try {
    const color = theme === 'auto' ? 'bg_color' : CHROME_BG[theme];
    webApp.setHeaderColor?.(color);
    webApp.setBackgroundColor?.(color);
  } catch {
    /* some clients reject custom colors */
  }
}

export function setTheme(id) {
  applyTheme(id);
  if (THEMES.includes(id)) localStorage.setItem(STORAGE_KEY, id);
}
