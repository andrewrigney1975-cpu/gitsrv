import { api } from '../api.js';
import { el, form, toast, errorToast } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView, orgNav } from './_shared.js';
import { suggestSlug, repoSlugCheck } from '../features/slug.js';

export function renderOrg(slug) {
  return asyncView(async () => {
    const org = await api.get(`/api/orgs/${slug}`);
    const repos = await api.get(`/api/orgs/${slug}/repos`);
    const canCreate = ['owner', 'admin', 'member', 'site-admin'].includes(org.myRole);

    const wrap = el('div', { class: 'stack' });
    wrap.append(el('div', { class: 'page-head' },
      el('div', {}, el('h1', {}, org.name), org.description && el('p', { class: 'muted' }, org.description)),
      canCreate && el('div', { class: 'row' },
        el('button', { class: 'btn', onclick: () => toggle('import') }, 'Import'),
        el('button', { class: 'btn primary', onclick: () => toggle('new') }, 'New repository'))));
    wrap.append(orgNav(slug, org.myRole));

    const newRepoHost = el('div', {});
    wrap.append(newRepoHost);

    let openMode = null;
    function toggle(mode) {
      if (openMode === mode) { newRepoHost.replaceChildren(); openMode = null; return; }
      openMode = mode;
      newRepoHost.replaceChildren(mode === 'import' ? importCard() : newCard());
    }

    function importCard() {
      return el('div', { class: 'card' }, el('h2', {}, 'Import a repository'),
        el('p', { class: 'muted' }, 'Mirror a public repo from another host (e.g. GitHub) by its clone URL. The import runs in the background.'),
        form({
          fields: [
            { name: 'sourceUrl', label: 'Clone URL', required: true, placeholder: 'https://github.com/owner/repo.git' },
            { name: 'name', label: 'Name', required: true, deriveFrom: 'sourceUrl',
              derive: (u) => (u.split('/').pop() || '').replace(/\.git$/, '') },
            { name: 'slug', label: 'URL slug', required: true, hint: 'lowercase letters, digits, single - or _',
              deriveFrom: 'name', derive: suggestSlug, check: repoSlugCheck(slug) },
            { name: 'visibility', label: 'Visibility', type: 'select', value: 'private', options: [
              { value: 'private', label: 'Private — only people with access' },
              { value: 'internal', label: 'Internal — any org member' },
              { value: 'public', label: 'Public — anyone' },
            ]},
          ],
          submitLabel: 'Start import',
          onSubmit: async (v) => {
            const r = await api.post(`/api/orgs/${slug}/repos/import`, v);
            toast('Import started — it may take a moment.', 'ok');
            navigate(`/o/${slug}/${r.slug}`);
          },
        }));
    }

    function newCard() {
      return el('div', { class: 'card' }, el('h2', {}, 'New repository'), form({
        fields: [
          { name: 'name', label: 'Name', required: true },
          { name: 'slug', label: 'URL slug', required: true, hint: 'lowercase letters, digits, single - or _',
            deriveFrom: 'name', derive: suggestSlug, check: repoSlugCheck(slug) },
          { name: 'description', label: 'Description' },
          { name: 'visibility', label: 'Visibility', type: 'select', value: 'private', options: [
            { value: 'private', label: 'Private — only people with access' },
            { value: 'internal', label: 'Internal — any org member' },
            { value: 'public', label: 'Public — anyone' },
          ]},
          { name: 'defaultBranch', label: 'Default branch', value: 'main' },
        ],
        submitLabel: 'Create repository',
        onSubmit: async (v) => {
          const repo = await api.post(`/api/orgs/${slug}/repos`, v);
          toast(`Created ${repo.name}. Push access lands in Phase 2.`, 'ok');
          navigate(`/o/${slug}/${repo.slug}`);
        },
      }));
    }

    if (!repos.length) {
      wrap.append(el('div', { class: 'card empty muted' }, 'No repositories yet.'));
    } else {
      const list = el('ul', { class: 'repo-list' });
      for (const r of repos) {
        list.append(el('li', {},
          el('a', { href: `#/o/${slug}/${r.slug}` }, el('strong', {}, r.name)),
          el('span', { class: `pill vis-${r.visibility}` }, r.visibility),
          r.isArchived && el('span', { class: 'pill' }, 'archived'),
          r.description && el('span', { class: 'muted' }, r.description)));
      }
      wrap.append(list);
    }
    return wrap;
  });
}
