import { api, ApiError } from '../api.js';
import { el, form, toast, errorToast, timeAgo, confirmDialog } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView } from './_shared.js';
import { shell } from './repo.js';

async function ctx(slug, repoSlug) {
  const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview`);
  return { b: { ...ov.repo, myPermission: ov.myPermission }, refs: ov.refs };
}
const R = (s, r) => `/api/orgs/${s}/repos/${r}`;
const canWrite = (p) => ['write', 'maintain', 'admin'].includes(p);

export function renderReleases(slug, repoSlug) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const list = await api.get(`${R(slug, repoSlug)}/releases`);
    const rows = list.map((r) => el('div', { class: 'pr-row' },
      el('div', {},
        el('a', { href: `#/o/${slug}/${repoSlug}/releases/${encodeURIComponent(r.tagName)}` },
          el('strong', {}, r.name || r.tagName)),
        r.isDraft && el('span', { class: 'pill' }, 'draft'),
        r.isPrerelease && el('span', { class: 'pill' }, 'pre-release'),
        el('div', { class: 'muted' }, `${r.tagName} · ${r.authorUsername} · ${timeAgo(r.createdAt)} · ${r.assets.length} asset(s)`))));
    const body = el('div', { class: 'stack' },
      el('div', { class: 'page-head' }, el('h1', {}, 'Releases'),
        canWrite(b.myPermission) && el('a', { class: 'btn primary', href: `#/o/${slug}/${repoSlug}/releases/new` }, 'Draft a new release')),
      rows.length ? el('div', { class: 'card nopad' }, ...rows) : el('div', { class: 'card empty muted' }, 'No releases yet.'));
    return shell(b, null, 'code', body);
  });
}

export function renderNewRelease(slug, repoSlug) {
  return asyncView(async () => {
    const { b, refs } = await ctx(slug, repoSlug);
    const targets = [...refs.branches.map((x) => x.name), ...refs.tags.map((x) => x.name)];
    const body = el('div', { class: 'card' }, el('h2', {}, 'New release'), form({
      fields: [
        { name: 'tagName', label: 'Tag', required: true, hint: 'e.g. v1.2.0 — created if it does not exist' },
        { name: 'target', label: 'Target', type: 'select', value: b.defaultBranch, options: targets.map((t) => ({ value: t, label: t })) },
        { name: 'name', label: 'Release title' },
        { name: 'body', label: 'Notes' },
        { name: 'isPrerelease', label: 'Pre-release', type: 'select', value: 'false', options: [{ value: 'false', label: 'No' }, { value: 'true', label: 'Yes' }] },
        { name: 'isDraft', label: 'Draft', type: 'select', value: 'false', options: [{ value: 'false', label: 'No' }, { value: 'true', label: 'Yes' }] },
      ],
      submitLabel: 'Publish release',
      onSubmit: async (v) => {
        await api.post(`${R(slug, repoSlug)}/releases`, { ...v, isPrerelease: v.isPrerelease === 'true', isDraft: v.isDraft === 'true' });
        navigate(`/o/${slug}/${repoSlug}/releases/${encodeURIComponent(v.tagName)}`);
      },
    }));
    return shell(b, null, 'code', body);
  });
}

export function renderReleaseDetail(slug, repoSlug, tag) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const r = await api.get(`${R(slug, repoSlug)}/releases/${encodeURIComponent(tag)}`);
    const refresh = async () => document.getElementById('view').replaceChildren(await renderReleaseDetail(slug, repoSlug, tag));

    const assets = el('div', { class: 'card' }, el('h3', {}, 'Assets'),
      r.assets.length
        ? el('table', { class: 'data-table' }, el('tbody', {}, ...r.assets.map((a) => el('tr', {},
            el('td', {}, el('a', { href: `${R(slug, repoSlug)}/releases/${encodeURIComponent(tag)}/assets/${a.id}` }, a.name)),
            el('td', { class: 'muted' }, `${(a.sizeBytes / 1024).toFixed(1)} KB · ${a.downloads} downloads`)))))
        : el('span', { class: 'muted' }, 'None'));

    if (canWrite(b.myPermission)) {
      const fileInput = el('input', { type: 'file' });
      assets.append(el('div', { class: 'row' }, fileInput, el('button', { class: 'small', onclick: async () => {
        if (!fileInput.files[0]) return;
        const fd = new FormData();
        fd.append('file', fileInput.files[0]);
        const res = await fetch(`${R(slug, repoSlug)}/releases/${encodeURIComponent(tag)}/assets`, {
          method: 'POST', headers: { 'X-GitSrv-CSRF': '1' }, credentials: 'same-origin', body: fd,
        });
        if (!res.ok) { errorToast(new ApiError(res.status, 'Upload failed')); return; }
        toast('Asset uploaded.', 'ok'); refresh();
      } }, 'Upload asset')));
    }

    const body = el('div', { class: 'stack' },
      el('div', { class: 'page-head' },
        el('h1', {}, r.name || r.tagName),
        canWrite(b.myPermission) && el('button', { class: 'danger small', onclick: async () => {
          if (!await confirmDialog(`Delete release ${r.tagName}?`)) return;
          await api.del(`${R(slug, repoSlug)}/releases/${encodeURIComponent(tag)}`);
          navigate(`/o/${slug}/${repoSlug}/releases`);
        } }, 'Delete')),
      el('div', { class: 'muted' }, `${r.tagName} · ${r.authorUsername} · ${timeAgo(r.createdAt)}`),
      r.body && el('div', { class: 'card' }, el('pre', { class: 'commit-body' }, r.body)),
      assets);
    return shell(b, null, 'code', body);
  });
}
