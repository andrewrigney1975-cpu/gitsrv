import { api } from '../api.js';
import { el, toast, errorToast, timeAgo } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView } from './_shared.js';
import { highlight } from '../features/highlight.js';

// Shared repo chrome: title, clone box, branch picker, tab bar. `body` is the section-specific node.
export function shell(b, refName, section, body) {
  refName = refName || b.defaultBranch || 'HEAD';
  const base = `#/o/${b.orgSlug}/${b.repoSlug}`;
  const tab = (id, href, label) => el('a', { href, class: 'tab' + (section === id ? ' active' : '') }, label);
  const httpUrl = `${location.origin}/${b.orgSlug}/${b.repoSlug}.git`;
  const sshUrl = `git@${location.hostname}:${b.orgSlug}/${b.repoSlug}.git`;

  return el('div', { class: 'stack' },
    el('div', { class: 'page-head' },
      el('h1', {},
        el('a', { href: `#/o/${b.orgSlug}` }, b.orgSlug), ' / ',
        el('a', { href: base }, b.repoSlug),
        el('span', { class: `pill vis-${b.visibility}` }, b.visibility),
        b.isArchived && el('span', { class: 'pill' }, 'archived')),
      el('details', { class: 'clone-menu' },
        el('summary', { class: 'btn' }, 'Clone'),
        el('div', { class: 'clone-pop card' },
          cloneRow('HTTPS', httpUrl), cloneRow('SSH', sshUrl),
          el('p', { class: 'muted' }, 'HTTPS password = a personal access token (Settings).')))),
    b.description && el('p', { class: 'muted' }, b.description),
    el('nav', { class: 'sub-nav' },
      tab('code', base, 'Code'),
      tab('commits', `${base}/commits/${enc(refName)}`, 'Commits'),
      tab('issues', `${base}/issues`, 'Issues'),
      tab('pulls', `${base}/pulls`, 'Pull requests'),
      tab('actions', `${base}/actions`, 'Actions'),
      tab('graph', `${base}/graph`, 'Graph'),
      b.myPermission === 'admin' && tab('settings', `${base}/settings`, 'Settings')),
    body);
}

export const enc = (s) => encodeURIComponent(s);
function cloneRow(label, url) {
  const cmd = `git clone ${url}`;
  return el('div', { class: 'clone-row' },
    el('span', { class: 'muted mono' }, label),
    el('code', {}, cmd),
    el('button', { class: 'small', onclick: () => { navigator.clipboard?.writeText(cmd); toast('Copied.', 'ok'); } }, 'Copy'));
}

function branchPicker(b, refs, current, kind, path) {
  if (!refs || refs.isEmpty) return null;
  const opts = [
    ...refs.branches.map((r) => ({ v: r.name, g: 'Branches' })),
    ...refs.tags.map((r) => ({ v: r.name, g: 'Tags' })),
  ];
  const sel = el('select', { class: 'ref-picker', onchange: (e) => {
    const base = `#/o/${b.orgSlug}/${b.repoSlug}`;
    if (kind === 'tree') navigate(`/o/${b.orgSlug}/${b.repoSlug}/tree/${enc(e.target.value)}${path ? '/' + path : ''}`.replace('#', ''));
    else if (kind === 'commits') navigate(`/o/${b.orgSlug}/${b.repoSlug}/commits/${enc(e.target.value)}`);
    else navigate(`/o/${b.orgSlug}/${b.repoSlug}/tree/${enc(e.target.value)}`);
  }});
  for (const o of opts) sel.append(el('option', { value: o.v, selected: o.v === current }, o.v));
  return sel;
}

// ---- Code / tree + overview ----

export function renderRepoCode(slug, repoSlug, refName, path) {
  return asyncView(async () => {
    const isRoot = !refName && !path;
    const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview${refName ? `?ref=${enc(refName)}` : ''}`);
    const b = { ...ov.repo, orgSlug: ov.repo.orgSlug, repoSlug: ov.repo.repoSlug, myPermission: ov.myPermission };
    const currentRef = refName || ov.repo.defaultBranch || 'HEAD';

    if (ov.refs.isEmpty) {
      return shell(b, currentRef, 'code', emptyRepoHelp(b));
    }

    let tree, readmeHtml, readmeName, languages;
    if (isRoot && ov.home) {
      ({ readmeHtml, readmeName, languages } = ov.home);
      tree = { entries: ov.home.entries, commit: ov.home.commit, path: '' };
    } else {
      tree = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/tree/${enc(currentRef)}${path ? '/' + path : ''}`);
      languages = null;
    }

    const body = el('div', { class: 'stack' },
      el('div', { class: 'repo-toolbar' },
        branchPicker(b, ov.refs, currentRef, 'tree', path),
        breadcrumb(b, currentRef, tree.path || path || ''),
        el('span', { class: 'spacer' }),
        el('a', { class: 'btn-link', href: `#/o/${b.orgSlug}/${b.repoSlug}/releases` },
          `Releases${ov.refs.tags.length ? ' · ' + ov.refs.tags.length : ''}`)),
      languages && languages.length ? languageBar(languages) : null,
      treeTable(b, currentRef, tree),
      tree.commit && lastCommitLine(b, tree.commit),
      readmeHtml ? el('div', { class: 'card readme' },
        el('div', { class: 'card-head muted' }, readmeName),
        el('div', { class: 'markdown', html: readmeHtml })) : null);

    return shell(b, currentRef, 'code', body);
  });
}

function breadcrumb(b, ref, path) {
  const parts = path ? path.split('/') : [];
  const crumbs = [el('a', { href: `#/o/${b.orgSlug}/${b.repoSlug}/tree/${enc(ref)}` }, b.repoSlug)];
  let acc = '';
  parts.forEach((p, i) => {
    acc = acc ? `${acc}/${p}` : p;
    crumbs.push(el('span', { class: 'sep' }, '/'));
    crumbs.push(i === parts.length - 1
      ? el('strong', {}, p)
      : el('a', { href: `#/o/${b.orgSlug}/${b.repoSlug}/tree/${enc(ref)}/${acc}` }, p));
  });
  return el('div', { class: 'breadcrumb mono' }, ...crumbs);
}

function treeTable(b, ref, tree) {
  const rows = tree.entries.map((e) => {
    const icon = e.type === 'tree' ? '📁' : e.type === 'submodule' ? '📦' : '📄';
    const href = e.type === 'tree'
      ? `#/o/${b.orgSlug}/${b.repoSlug}/tree/${enc(ref)}/${e.path}`
      : `#/o/${b.orgSlug}/${b.repoSlug}/blob/${enc(ref)}/${e.path}`;
    return el('tr', {},
      el('td', { class: 'tree-name' }, el('span', { class: 'ico' }, icon),
        e.type === 'submodule' ? el('span', {}, e.name) : el('a', { href }, e.name)),
      el('td', { class: 'right muted mono' }, e.type === 'blob' ? fmtBytes(e.size) : ''));
  });
  return el('table', { class: 'data-table tree-table' }, el('tbody', {}, ...rows));
}

function lastCommitLine(b, c) {
  return el('div', { class: 'last-commit muted' },
    el('a', { class: 'mono', href: `#/o/${b.orgSlug}/${b.repoSlug}/commit/${c.sha}` }, c.shortSha),
    ' ', c.summary, ' · ', c.author.name, ' · ', timeAgo(c.author.when));
}

function languageBar(langs) {
  const bar = el('div', { class: 'lang-bar' });
  langs.forEach((l, i) => bar.append(el('span', {
    class: `lang-seg lang-${i % 8}`, title: `${l.language} ${l.percent}%`,
    style: `width:${l.percent}%`,
  })));
  const legend = el('div', { class: 'lang-legend' }, ...langs.map((l, i) =>
    el('span', {}, el('span', { class: `dot lang-${i % 8}` }), `${l.language} ${l.percent}%`)));
  return el('div', { class: 'card lang-card' }, bar, legend);
}

function emptyRepoHelp(b) {
  const url = `${location.origin}/${b.orgSlug}/${b.repoSlug}.git`;
  return el('div', { class: 'card' },
    el('h2', {}, 'Quick setup'),
    el('pre', { class: 'setup' }, [
      `git init`, `git add .`, `git commit -m "first commit"`,
      `git branch -M ${b.defaultBranch}`, `git remote add origin ${url}`,
      `git push -u origin ${b.defaultBranch}`,
    ].join('\n')));
}

// ---- Blob / file view ----

export function renderRepoBlob(slug, repoSlug, refName, path) {
  return asyncView(async () => {
    const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview?ref=${enc(refName)}`);
    const b = { ...ov.repo, myPermission: ov.myPermission };
    const res = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/blob/${enc(refName)}/${path}`);
    const blob = res.blob;

    const rawUrl = `/api/orgs/${slug}/repos/${repoSlug}/browse/raw/${enc(refName)}/${path}`;
    const canWrite = ['write', 'maintain', 'admin'].includes(b.myPermission);
    const actions = el('div', { class: 'file-actions' },
      el('span', { class: 'muted mono' }, `${fmtBytes(blob.size)}`),
      el('a', { href: `#/o/${b.orgSlug}/${b.repoSlug}/blame/${enc(refName)}/${path}` }, 'Blame'),
      el('a', { href: `#/o/${b.orgSlug}/${b.repoSlug}/commits/${enc(refName)}?path=${enc(path)}` }, 'History'),
      el('a', { href: rawUrl, target: '_blank', rel: 'noopener' }, 'Raw'),
      canWrite && !blob.isBinary && !blob.isTruncated && el('button', { class: 'small', onclick: () => openEditor() }, 'Edit'));

    function openEditor() {
      const ta = el('textarea', { class: 'file-editor', rows: Math.min(40, (blob.text.match(/\n/g) || []).length + 3) });
      ta.value = blob.text;
      const msg = el('input', { type: 'text', placeholder: `Update ${path}` });
      const editorCard = el('div', { class: 'card' }, ta, el('div', { class: 'row' }, msg,
        el('button', { class: 'primary small', onclick: async () => {
          try {
            await api.post(`/api/orgs/${slug}/repos/${repoSlug}/edit`, {
              branch: refName, path, content: ta.value, message: msg.value, expectedBlobSha: blob.sha,
            });
            toast('Committed.', 'ok');
            navigate(`/o/${b.orgSlug}/${b.repoSlug}/blob/${enc(refName)}/${path}`);
          } catch (err) { errorToast(err); }
        } }, 'Commit change'),
        el('button', { class: 'small', onclick: () => navigate(`/o/${b.orgSlug}/${b.repoSlug}/blob/${enc(refName)}/${path}`) }, 'Cancel')));
      document.querySelector('.codeview')?.closest('.card')?.replaceWith(editorCard);
    }

    let content;
    if (blob.isBinary) {
      content = el('div', { class: 'card muted' }, 'Binary file — ',
        el('a', { href: rawUrl, target: '_blank', rel: 'noopener' }, 'download'), ` (${fmtBytes(blob.size)})`);
    } else if (blob.isTruncated) {
      content = el('div', { class: 'card muted' }, 'File is too large to display. ',
        el('a', { href: rawUrl, target: '_blank', rel: 'noopener' }, 'Download raw'));
    } else {
      content = codeBlock(blob.text, res.language, path);
    }

    const body = el('div', { class: 'stack' },
      el('div', { class: 'repo-toolbar' }, breadcrumb(b, refName, path), actions),
      content);
    return shell(b, refName, 'code', body);
  });
}

export function codeBlock(text, language, path, blameByLine) {
  const lines = text.replace(/\n$/, '').split('\n');
  const gutter = el('div', { class: 'gutter' });
  const code = el('div', { class: 'code' });
  lines.forEach((ln, i) => {
    const n = i + 1;
    gutter.append(el('a', { id: `L${n}`, href: `#L${n}`, class: 'ln' }, String(n)));
    const row = el('div', { class: 'cl' });
    row.textContent = ln || ' ';
    code.append(row);
  });
  const pre = el('div', { class: 'codeview' }, gutter, el('pre', {}, code));
  highlight(code, language, path);
  return el('div', { class: 'card nopad' }, pre);
}

// ---- Blame ----

export function renderRepoBlame(slug, repoSlug, refName, path) {
  return asyncView(async () => {
    const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview?ref=${enc(refName)}`);
    const b = { ...ov.repo, myPermission: ov.myPermission };
    const bl = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/blame/${enc(refName)}/${path}`);

    const byLine = [];
    for (const h of bl.hunks) for (let i = 0; i < h.lineCount; i++) byLine[h.startLine + i] = h;
    const now = Date.now();
    const rows = bl.lines.map((ln, i) => {
      const n = i + 1;
      const h = byLine[n];
      const ageDays = h ? (now - new Date(h.author.when)) / 86400000 : 0;
      const heat = h ? Math.max(0, 1 - Math.min(ageDays, 365) / 365) : 0;
      const first = h && (i === 0 || byLine[i]?.sha !== h.sha);
      return el('div', { class: 'blame-row' },
        el('div', { class: 'blame-meta', style: `--heat:${heat.toFixed(2)}` },
          first ? el('a', { class: 'mono', href: `#/o/${b.orgSlug}/${b.repoSlug}/commit/${h.sha}`, title: h.summary }, h.shortSha) : '',
          first ? el('span', { class: 'muted' }, ` ${h.author.name}, ${timeAgo(h.author.when)}`) : ''),
        el('a', { class: 'ln', id: `L${n}`, href: `#L${n}` }, String(n)),
        Object.assign(el('div', { class: 'cl' }), { textContent: ln || ' ' }));
    });

    const body = el('div', { class: 'stack' },
      el('div', { class: 'repo-toolbar' }, breadcrumb(b, refName, path),
        el('a', { href: `#/o/${b.orgSlug}/${b.repoSlug}/blob/${enc(refName)}/${path}` }, 'Back to file')),
      el('div', { class: 'card nopad blame' }, ...rows));
    return shell(b, refName, 'code', body);
  });
}

// ---- helpers ----

function fmtBytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}
