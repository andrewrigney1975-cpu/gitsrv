// Lazy syntax highlighting via highlight.js from cdnjs. Loaded once, on first file view. If the CDN
// is blocked the file still renders as plain text — highlighting is purely additive.

const HLJS_VERSION = '11.10.0';
let loading;

function loadHljs() {
  if (window.hljs) return Promise.resolve(window.hljs);
  loading ??= new Promise((resolve, reject) => {
    const css = document.createElement('link');
    css.rel = 'stylesheet';
    css.href = `https://cdnjs.cloudflare.com/ajax/libs/highlight.js/${HLJS_VERSION}/styles/github.min.css`;
    css.media = '(prefers-color-scheme: light)';
    document.head.append(css);
    const cssDark = document.createElement('link');
    cssDark.rel = 'stylesheet';
    cssDark.href = `https://cdnjs.cloudflare.com/ajax/libs/highlight.js/${HLJS_VERSION}/styles/github-dark.min.css`;
    cssDark.media = '(prefers-color-scheme: dark)';
    document.head.append(cssDark);

    const s = document.createElement('script');
    s.src = `https://cdnjs.cloudflare.com/ajax/libs/highlight.js/${HLJS_VERSION}/highlight.min.js`;
    s.onload = () => resolve(window.hljs);
    s.onerror = () => reject(new Error('hljs load failed'));
    document.head.append(s);
  });
  return loading;
}

const LANG_MAP = {
  'C#': 'csharp', JavaScript: 'javascript', TypeScript: 'typescript', Python: 'python',
  Ruby: 'ruby', Go: 'go', Rust: 'rust', Java: 'java', 'C++': 'cpp', C: 'c', PHP: 'php',
  HTML: 'xml', CSS: 'css', SCSS: 'scss', Shell: 'bash', PowerShell: 'powershell', SQL: 'sql',
  JSON: 'json', YAML: 'yaml', Markdown: 'markdown', Kotlin: 'kotlin', Swift: 'swift',
};

export async function highlight(codeEl, language, path) {
  try {
    const hljs = await loadHljs();
    const rows = [...codeEl.children];
    const full = rows.map((r) => r.textContent).join('\n');
    const lang = LANG_MAP[language];
    const result = lang && hljs.getLanguage(lang)
      ? hljs.highlight(full, { language: lang, ignoreIllegals: true })
      : hljs.highlightAuto(full);
    const hlRows = result.value.split('\n');
    rows.forEach((r, i) => { if (hlRows[i] != null) r.innerHTML = hlRows[i] || ' '; });
    codeEl.classList.add('hljs');
  } catch {
    /* plain text is fine */
  }
}
