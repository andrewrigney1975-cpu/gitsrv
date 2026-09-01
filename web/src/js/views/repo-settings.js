import { api } from '../api.js';
import { el, form, toast, errorToast, confirmDialog } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView } from './_shared.js';
import { shell } from './repo.js';

const PERMS = ['read', 'triage', 'write', 'maintain', 'admin'];

export function renderRepoSettings(slug, repoSlug) {
  return asyncView(async () => {
    const repo = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/`);
    const b = { ...repo, myPermission: repo.myPermission };
    if (repo.myPermission !== 'admin') {
      return shell(b, repo.defaultBranch, 'settings', el('div', { class: 'card muted' }, 'Admin permission required.'));
    }

    const settings = el('div', { class: 'card' }, el('h2', {}, 'Settings'), form({
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
    }),
      el('details', { class: 'danger-zone' }, el('summary', {}, 'Rename slug'), form({
        fields: [{ name: 'slug', label: 'New slug', value: repo.slug, required: true, hint: 'old URLs 301 to the new one' }],
        submitLabel: 'Rename',
        onSubmit: async (v) => { await api.post(`/api/orgs/${slug}/repos/${repoSlug}/rename`, v); toast('Renamed.', 'ok'); navigate(`/o/${slug}/${v.slug}/settings`); },
      })),
      el('details', { class: 'danger-zone' }, el('summary', {}, 'Delete repository'),
        el('p', { class: 'muted' }, 'Permanently deletes the repository and all its Git data.'),
        el('button', { class: 'danger', onclick: async () => {
          if (!await confirmDialog(`Delete ${repo.orgSlug}/${repo.slug} and all its data? This cannot be undone.`)) return;
          await api.del(`/api/orgs/${slug}/repos/${repoSlug}/`);
          toast('Repository deleted.', 'ok');
          navigate(`/o/${slug}`);
        }}, 'Delete this repository')));

    const access = el('div', { class: 'card' });
    async function refresh() {
      const a = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/collaborators`);
      const uRows = a.users.map((u) => el('tr', {},
        el('td', {}, el('strong', {}, u.username)), el('td', {}, el('span', { class: 'pill' }, u.permission)),
        el('td', { class: 'right' }, el('button', { class: 'danger small', onclick: async () => {
          await api.del(`/api/orgs/${slug}/repos/${repoSlug}/collaborators/${u.userId}`); refresh();
        }}, 'Remove'))));
      const tRows = a.teams.map((t) => el('tr', {},
        el('td', {}, el('strong', {}, t.name), ' ', el('code', { class: 'muted' }, t.slug)),
        el('td', {}, el('span', { class: 'pill' }, t.permission)),
        el('td', { class: 'right' }, el('button', { class: 'danger small', onclick: async () => {
          await api.del(`/api/orgs/${slug}/repos/${repoSlug}/team-access/${t.teamId}`); refresh();
        }}, 'Remove'))));
      const empty = () => el('tr', {}, el('td', { colspan: 3, class: 'muted' }, 'None.'));
      access.replaceChildren(
        el('h2', {}, 'Access'),
        el('h3', {}, 'Collaborators'),
        el('table', { class: 'data-table' }, el('tbody', {}, ...(uRows.length ? uRows : [empty()]))),
        form({ fields: [
          { name: 'username', label: 'Add collaborator', required: true },
          { name: 'permission', label: 'Permission', type: 'select', value: 'read', options: PERMS.map((p) => ({ value: p, label: p })) },
        ], submitLabel: 'Add', onSubmit: async (v) => { await api.post(`/api/orgs/${slug}/repos/${repoSlug}/collaborators`, v); refresh(); } }),
        el('h3', {}, 'Team access'),
        el('table', { class: 'data-table' }, el('tbody', {}, ...(tRows.length ? tRows : [empty()]))),
        form({ fields: [
          { name: 'teamSlug', label: 'Team slug', required: true },
          { name: 'permission', label: 'Permission', type: 'select', value: 'read', options: PERMS.map((p) => ({ value: p, label: p })) },
        ], submitLabel: 'Grant', onSubmit: async (v) => { await api.post(`/api/orgs/${slug}/repos/${repoSlug}/team-access`, v); refresh(); } }));
    }
    await refresh();

    return shell(b, repo.defaultBranch, 'settings', el('div', { class: 'stack' }, settings, access));
  });
}
