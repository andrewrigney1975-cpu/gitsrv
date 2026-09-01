// Light/dark toggle. Cycles system -> light -> dark -> system, persisting the explicit choices.
// Matches the pre-paint script in index.html (localStorage key `gitsrv_theme`).

const KEY = 'gitsrv_theme';
const ORDER = ['system', 'light', 'dark'];

function current() {
  try {
    const saved = localStorage.getItem(KEY);
    return saved === 'light' || saved === 'dark' ? saved : 'system';
  } catch {
    return 'system';
  }
}

function apply(mode) {
  const root = document.documentElement;
  if (mode === 'system') {
    root.removeAttribute('data-theme');
    try { localStorage.removeItem(KEY); } catch { /* ignore */ }
  } else {
    root.setAttribute('data-theme', mode);
    try { localStorage.setItem(KEY, mode); } catch { /* ignore */ }
  }
}

export function initThemeToggle() {
  const btn = document.getElementById('theme-toggle');
  if (!btn) return;

  const label = () => {
    const m = current();
    btn.textContent = `Theme: ${m}`;
  };
  label();

  btn.addEventListener('click', () => {
    const next = ORDER[(ORDER.indexOf(current()) + 1) % ORDER.length];
    apply(next);
    label();
  });
}
