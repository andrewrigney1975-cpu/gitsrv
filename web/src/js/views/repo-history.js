import { api } from '../api.js';
import { el, timeAgo, toast } from '../ui.js';
import { asyncView } from './_shared.js';
import { shell, enc } from './repo.js';

async function ctx(slug, repoSlug, refName) {
  const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview${refName ? `?ref=${enc(refName)}` : ''}`);
  return { b: { ...ov.repo, myPermission: ov.myPermission }, refs: ov.refs, defaultBranch: ov.repo.defaultBranch };
}

// ---- commit list ----

export function renderRepoCommits(slug, repoSlug, refName, query) {
  return asyncView(async () => {
    const { b, defaultBranch } = await ctx(slug, repoSlug, refName);
    const ref = (refName && refName !== 'null') ? refName : (defaultBranch || 'HEAD');
    const path = new URLSearchParams(query || '').get('path') || '';
    const page = Number(new URLSearchParams(query || '').get('page') || 1);

    const res = await api.get(
      `/api/orgs/${slug}/repos/${repoSlug}/browse/commits/${enc(ref)}?page=${page}${path ? `&path=${enc(path)}` : ''}`);

    const list = el('div', { class: 'commit-list card nopad' }, ...res.commits.map((c) => el('div', { class: 'commit-row' },
      el('div', { class: 'commit-main' },
        el('a', { href: `#/o/${b.orgSlug}/${b.repoSlug}/commit/${c.sha}` }, el('strong', {}, c.summary)),
        el('div', { class: 'muted' }, `${c.author.name} committed ${timeAgo(c.author.when)}`)),
      el('div', { class: 'commit-side' },
        el('a', { class: 'mono', href: `#/o/${b.orgSlug}/${b.repoSlug}/commit/${c.sha}` }, c.shortSha),
        el('button', { class: 'small', onclick: () => { navigator.clipboard?.writeText(c.sha); toast('SHA copied.', 'ok'); } }, 'Copy')))));

    const pager = el('div', { class: 'pager' },
      page > 1 && el('a', { class: 'btn', href: `#/o/${b.orgSlug}/${b.repoSlug}/commits/${enc(ref)}?page=${page - 1}${path ? `&path=${enc(path)}` : ''}` }, '← Newer'),
      res.hasMore && el('a', { class: 'btn', href: `#/o/${b.orgSlug}/${b.repoSlug}/commits/${enc(ref)}?page=${page + 1}${path ? `&path=${enc(path)}` : ''}` }, 'Older →'));

    const body = el('div', { class: 'stack' },
      path && el('div', { class: 'muted mono' }, `History of ${path}`),
      list, pager);
    return shell(b, ref, 'commits', body);
  });
}

// ---- commit detail + diff ----

export function renderRepoCommit(slug, repoSlug, sha) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug, null);
    const d = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/commit/${sha}`);
    const c = d.commit;

    const head = el('div', { class: 'card' },
      el('h2', {}, c.summary),
      c.message.trim() !== c.summary.trim() && el('pre', { class: 'commit-body' }, c.message.trim()),
      el('div', { class: 'muted' },
        `${c.author.name} authored ${timeAgo(c.author.when)}`,
        c.parents.length ? el('span', {}, ' · parent ',
          ...c.parents.map((p) => el('a', { class: 'mono', href: `#/o/${b.orgSlug}/${b.repoSlug}/commit/${p}` }, p.slice(0, 7)))) : ' · root commit'),
      el('div', { class: 'mono muted' }, c.sha),
      el('div', { class: 'diffstat' }, `${d.files.length} file(s) `,
        el('span', { class: 'add' }, `+${d.totalAdded}`), ' ',
        el('span', { class: 'del' }, `−${d.totalDeleted}`)));

    const files = d.files.map((f) => el('div', { class: 'card nopad diff-file' },
      el('div', { class: 'diff-head mono' },
        f.oldPath ? `${f.oldPath} → ${f.path}` : f.path,
        el('span', { class: 'muted' }, ` ${f.changeKind}`),
        el('span', { class: 'add' }, ` +${f.added}`), el('span', { class: 'del' }, ` −${f.deleted}`)),
      f.isBinary ? el('div', { class: 'diff-binary muted' }, 'Binary file not shown')
        : f.patch ? renderPatch(f.patch) : el('div', { class: 'muted diff-binary' }, 'No textual changes')));

    return shell(b, null, 'commits', el('div', { class: 'stack' }, head, ...files));
  });
}

function renderPatch(patch) {
  const box = el('div', { class: 'patch' });
  for (const raw of patch.split('\n')) {
    let cls = 'ctx';
    if (raw.startsWith('@@')) cls = 'hunk';
    else if (raw.startsWith('+') && !raw.startsWith('+++')) cls = 'add';
    else if (raw.startsWith('-') && !raw.startsWith('---')) cls = 'del';
    else if (raw.startsWith('diff ') || raw.startsWith('index ') || raw.startsWith('+++') || raw.startsWith('---')) cls = 'meta';
    box.append(Object.assign(el('div', { class: `pl ${cls}` }), { textContent: raw || ' ' }));
  }
  return box;
}

// ---- commit graph (Canvas) ----

export function renderRepoGraph(slug, repoSlug) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug, null);
    const commits = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/graph?limit=200`);

    if (!commits.length) return shell(b, null, 'graph', el('div', { class: 'card muted' }, 'No commits yet.'));

    const rowH = 28;
    const laneW = 16;
    const maxLane = Math.max(...commits.map((c) => Math.max(c.lane, ...c.parentLanes))) + 1;
    const canvasW = maxLane * laneW + 12;
    const wrap = el('div', { class: 'graph-wrap card nopad' });
    const canvas = el('canvas', { width: canvasW * devicePixelRatio, height: commits.length * rowH * devicePixelRatio,
      style: `width:${canvasW}px;height:${commits.length * rowH}px` });
    const rows = el('div', { class: 'graph-rows' });

    const idx = new Map(commits.map((c, i) => [c.sha, i]));
    const palette = ['#0c66e4', '#a8620c', '#1f7a54', '#7a3ea8', '#b23b2e', '#0a7ea4', '#8a6d00', '#c2185b'];
    const ctx2d = canvas.getContext('2d');
    ctx2d.scale(devicePixelRatio, devicePixelRatio);
    ctx2d.lineWidth = 1.5;

    commits.forEach((c, i) => {
      const x = 6 + c.lane * laneW + laneW / 2;
      const y = i * rowH + rowH / 2;
      c.parents.forEach((p, k) => {
        const pi = idx.get(p);
        if (pi == null) return;
        const pl = c.parentLanes[k] ?? c.lane;
        const px = 6 + pl * laneW + laneW / 2;
        const py = pi * rowH + rowH / 2;
        ctx2d.strokeStyle = palette[pl % palette.length];
        ctx2d.beginPath();
        ctx2d.moveTo(x, y);
        ctx2d.bezierCurveTo(x, (y + py) / 2, px, (y + py) / 2, px, py);
        ctx2d.stroke();
      });
    });
    commits.forEach((c, i) => {
      const x = 6 + c.lane * laneW + laneW / 2;
      const y = i * rowH + rowH / 2;
      ctx2d.fillStyle = palette[c.lane % palette.length];
      ctx2d.beginPath();
      ctx2d.arc(x, y, 4, 0, Math.PI * 2);
      ctx2d.fill();

      rows.append(el('a', { class: 'graph-row', href: `#/o/${b.orgSlug}/${b.repoSlug}/commit/${c.sha}`, style: `height:${rowH}px` },
        ...c.refs.map((r) => el('span', { class: 'pill ref-pill' }, r)),
        el('span', { class: 'g-summary' }, c.summary),
        el('span', { class: 'muted g-meta' }, `${c.author.name} · ${timeAgo(c.author.when)} · `),
        el('span', { class: 'mono muted' }, c.shortSha)));
    });

    wrap.append(el('div', { class: 'graph-inner', style: `padding-left:${canvasW}px` },
      Object.assign(canvas, { style: canvas.getAttribute('style') + ';position:absolute;left:0;top:0' }), rows));
    return shell(b, null, 'graph', wrap);
  });
}
