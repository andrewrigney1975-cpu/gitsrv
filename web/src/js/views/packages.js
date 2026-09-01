import { api } from '../api.js';
import { el, toast, timeAgo, errorToast, confirmDialog } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView, orgNav } from './_shared.js';

const kindLabel = { npm: 'npm', nuget: 'NuGet', pypi: 'PyPI', maven: 'Maven', oci: 'Container', generic: 'Generic' };
const fmtBytes = (n) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;

export function renderPackages(slug) {
  return asyncView(async () => {
    const org = await api.get(`/api/orgs/${slug}`);
    const data = await api.get(`/api/orgs/${slug}/packages`);
    const rows = data.packages.map((p) => el('div', { class: 'pr-row' },
      el('div', {},
        el('a', { href: `#/o/${slug}/packages/${p.kind}/${encodeURIComponent(p.name)}` }, el('strong', {}, p.name)),
        el('span', { class: 'pill' }, kindLabel[p.kind] || p.kind),
        el('span', { class: `pill vis-${p.visibility}` }, p.visibility),
        el('div', { class: 'muted' }, `${p.versions} version(s) · ${fmtBytes(p.sizeBytes)} · updated ${timeAgo(p.updatedAt)}`))));

    const body = el('div', { class: 'stack' },
      el('h1', {}, `${org.name} · Packages`),
      orgNav(slug, org.myRole),
      el('div', { class: 'card muted' }, `Storage used by packages: ${fmtBytes(data.storageBytes)}`),
      rows.length ? el('div', { class: 'card nopad' }, ...rows)
        : el('div', { class: 'card empty muted' }, 'No packages published to this organisation yet.'));
    return body;
  });
}

export function renderPackageDetail(slug, kind, name) {
  return asyncView(async () => {
    const org = await api.get(`/api/orgs/${slug}`);
    const p = await api.get(`/api/orgs/${slug}/packages/${kind}/${encodeURIComponent(name)}`);
    const isAdmin = ['owner', 'admin', 'site-admin'].includes(org.myRole);
    const refresh = async () => document.getElementById('view').replaceChildren(await renderPackageDetail(slug, kind, name));

    const versions = el('table', { class: 'data-table' }, el('tbody', {},
      ...p.versions.map((v) => el('tr', {},
        el('td', {}, el('strong', {}, v.version), v.yanked && el('span', { class: 'pill' }, 'yanked')),
        el('td', { class: 'muted' }, `${v.publishedByUsername || 'unknown'} · ${timeAgo(v.createdAt)}`)))));

    const files = el('table', { class: 'data-table' }, el('tbody', {},
      ...p.files.map((f) => el('tr', {},
        el('td', {}, el('code', {}, f.name)),
        el('td', { class: 'muted mono' }, `${fmtBytes(f.sizeBytes)} · ${f.digest.slice(0, 19)}…`)))));

    const body = el('div', { class: 'stack' },
      el('div', { class: 'page-head' },
        el('h1', {}, p.name, el('span', { class: 'pill' }, kindLabel[p.kind] || p.kind)),
        isAdmin && el('div', { class: 'row' },
          el('select', { onchange: async (e) => {
            try { await api.patch(`/api/orgs/${slug}/packages/${kind}/${encodeURIComponent(name)}`, { visibility: e.target.value }); toast('Visibility updated.', 'ok'); }
            catch (err) { errorToast(err); }
          } }, ...['private', 'internal', 'public'].map((x) => el('option', { value: x, selected: x === p.visibility }, x))),
          el('button', { class: 'danger small', onclick: async () => {
            if (!await confirmDialog(`Delete package ${p.name} and every version?`)) return;
            await api.del(`/api/orgs/${slug}/packages/${kind}/${encodeURIComponent(name)}`);
            navigate(`/o/${slug}/packages`);
          } }, 'Delete'))),
      el('div', { class: 'card' }, el('h3', {}, 'Install'), el('pre', { class: 'commit-body' }, p.install)),
      el('div', { class: 'card' }, el('h3', {}, 'Versions'), versions),
      el('div', { class: 'card' }, el('h3', {}, 'Files'), files));
    return body;
  });
}
