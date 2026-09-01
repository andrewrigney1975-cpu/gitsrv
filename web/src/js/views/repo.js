import { api } from '../api.js';
import { el, form, toast, errorToast, confirmDialog } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView } from './_shared.js';

export function renderRepo(slug, repoSlug) {
  return asyncView(async () => {
    const repo = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/`);
    const canAdmin = repo.myPermission === 'admin';
    const cloneUrl = `${location.origin}/${repo.orgSlug}/${repo.slug}.git`;

    const wrap = el('div', { class: 'stack' });
    wrap.append(el('div', { class: 'page-head' },
      el('div', {},
        el('h1', {}, el('a', { href: `#/o/${slug}` }, repo.orgSlug), ' / ', repo.name),
        repo.description && el('p', { class: 'muted' }, repo.description)),
      el('div', { class: 'row' },
        el('span', { class: `pill vis-${repo.visibility}` }, repo.visibility),
        el('span', { class: 'pill' }, `you: ${repo.myPermission}`))));

    wrap.append(el('div', { class: 'card' },
      el('h2', {}, 'Clone'),
      el('p', { class: 'muted' }, 'Git transport arrives in Phase 2 — this URL will be live then.'),
      el('div', { class: 'clone-row' },
        el('code', {}, `git clone ${cloneUrl}`),
        el('button', { class: 'small', onclick: () => { navigator.clipboard?.writeText(`git clone ${cloneUrl}`); toast('Copied.', 'ok'); } }, 'Copy'))));

    if (canAdmin) {
      wrap.append(settingsCard());
      wrap.append(await accessCard());
    }
    return wrap;

    function settingsCard() {
      return el('div', { class: 'card' }, el('h2', {}, 'Settings'), form({
        fields: [
          { name: 'name', label: 'Name', value: repo.name, required: true },
          { name: 'description', label: 'Description', value: repo.description },
          { name: 'visibility', label: 'Visibility', type: 'select', value: repo.visibility, options: [
            { value: 'private', label: 'Private' }, { value: 'internal', label: 'Internal' }, { value: 'public', label: 'Public' },
          ]},
          { name: 'isArchived', label: 'Archived', type: 'select', value: String(repo.isArchived), options: [
            { value: 'false', label: 'No' }, { value: 'true', label: 'Yes — read-only' },
          ]},
        ],
        submitLabel: 'Save settings',
        onSubmit: async (v) => {
          await api.patch(`/api/orgs/${slug}/repos/${repoSlug}/`, { ...v, isArchived: v.isArchived === 'true' });
          toast('Saved.', 'ok');
        },
      }), el('details', { class: 'danger-zone' },
        el('summary', {}, 'Rename slug'),
        form({
          fields: [{ name: 'slug', label: 'New slug', value: repo.slug, required: true, hint: 'old URLs will 301 to the new one' }],
          submitLabel: 'Rename',
          onSubmit: async (v) => {
            await api.post(`/api/orgs/${slug}/repos/${repoSlug}/rename`, v);
            toast('Renamed.', 'ok');
            navigate(`/o/${slug}/${v.slug}`);
          },
        })));
    }

    async function accessCard() {
      const card = el('div', { class: 'card' });
      async function refresh() {
        const access = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/collaborators`);
        const userRows = access.users.map((u) => el('tr', {},
          el('td', {}, el('strong', {}, u.username)),
          el('td', {}, el('span', { class: 'pill' }, u.permission)),
          el('td', { class: 'right' }, el('button', { class: 'danger small', onclick: async () => {
            await api.del(`/api/orgs/${slug}/repos/${repoSlug}/collaborators/${u.userId}`); await refresh();
          }}, 'Remove'))));
        const teamRows = access.teams.map((t) => el('tr', {},
          el('td', {}, el('strong', {}, t.name), ' ', el('code', { class: 'muted' }, t.slug)),
          el('td', {}, el('span', { class: 'pill' }, t.permission)),
          el('td', { class: 'right' }, el('button', { class: 'danger small', onclick: async () => {
            await api.del(`/api/orgs/${slug}/repos/${repoSlug}/team-access/${t.teamId}`); await refresh();
          }}, 'Remove'))));
        card.replaceChildren(
          el('h2', {}, 'Access'),
          el('h3', {}, 'Collaborators'),
          el('table', { class: 'data-table' }, el('tbody', {}, ...(userRows.length ? userRows : [emptyRow()]))),
          form({
            fields: [
              { name: 'username', label: 'Add collaborator', required: true },
              { name: 'permission', label: 'Permission', type: 'select', value: 'read',
                options: ['read', 'triage', 'write', 'maintain', 'admin'].map((p) => ({ value: p, label: p })) },
            ],
            submitLabel: 'Add', onSubmit: async (v) => { await api.post(`/api/orgs/${slug}/repos/${repoSlug}/collaborators`, v); await refresh(); },
          }),
          el('h3', {}, 'Team access'),
          el('table', { class: 'data-table' }, el('tbody', {}, ...(teamRows.length ? teamRows : [emptyRow()]))),
          form({
            fields: [
              { name: 'teamSlug', label: 'Team slug', required: true },
              { name: 'permission', label: 'Permission', type: 'select', value: 'read',
                options: ['read', 'triage', 'write', 'maintain', 'admin'].map((p) => ({ value: p, label: p })) },
            ],
            submitLabel: 'Grant', onSubmit: async (v) => { await api.post(`/api/orgs/${slug}/repos/${repoSlug}/team-access`, v); await refresh(); },
          }));
      }
      const emptyRow = () => el('tr', {}, el('td', { colspan: 3, class: 'muted' }, 'None.'));
      await refresh();
      return card;
    }
  });
}
