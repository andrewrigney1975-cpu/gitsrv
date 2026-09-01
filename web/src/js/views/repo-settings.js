import { api } from '../api.js';
import { el, form, toast, errorToast, confirmDialog, timeAgo } from '../ui.js';
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

    // ---- repo config (default branch + merge methods) ----
    const config = el('div', { class: 'card' }, el('h2', {}, 'Merge & branches'), form({
      fields: [
        { name: 'defaultBranch', label: 'Default branch', value: repo.defaultBranch },
        { name: 'allowMergeCommit', label: 'Allow merge commits', type: 'select', value: String(repo.allowMergeCommit ?? true), options: yn() },
        { name: 'allowSquash', label: 'Allow squash merging', type: 'select', value: String(repo.allowSquash ?? true), options: yn() },
        { name: 'allowRebase', label: 'Allow rebase merging', type: 'select', value: String(repo.allowRebase ?? true), options: yn() },
        { name: 'deleteBranchOnMerge', label: 'Delete head branch on merge', type: 'select', value: String(repo.deleteBranchOnMerge ?? true), options: yn() },
      ],
      submitLabel: 'Save',
      onSubmit: async (v) => {
        await api.patch(`${R(slug, repoSlug)}/config`, {
          defaultBranch: v.defaultBranch,
          allowMergeCommit: v.allowMergeCommit === 'true', allowSquash: v.allowSquash === 'true',
          allowRebase: v.allowRebase === 'true', deleteBranchOnMerge: v.deleteBranchOnMerge === 'true',
        });
        toast('Saved.', 'ok');
      },
    }));

    // ---- branch protection ----
    const protections = el('div', { class: 'card' });
    async function refreshProtections() {
      const list = await api.get(`${R(slug, repoSlug)}/protections`);
      protections.replaceChildren(
        el('h2', {}, 'Branch protection'),
        list.length ? el('table', { class: 'data-table' }, el('tbody', {}, ...list.map((p) => el('tr', {},
          el('td', {}, el('code', {}, p.pattern)),
          el('td', { class: 'muted' }, [
            p.requirePullRequest && 'PR required',
            p.requiredApprovals > 0 && `${p.requiredApprovals} approval(s)`,
            p.blockForcePush && 'no force-push',
            p.blockDeletion && 'no delete',
            p.requireLinearHistory && 'linear',
            p.restrictPush && 'maintainers only',
          ].filter(Boolean).join(', ')),
          el('td', { class: 'right' }, el('button', { class: 'small danger', onclick: async () => {
            await api.del(`${R(slug, repoSlug)}/protections/${p.id}`); refreshProtections();
          } }, 'Remove')))))) : el('p', { class: 'muted' }, 'No protected branches.'),
        form({
          fields: [
            { name: 'pattern', label: 'Branch name or pattern', required: true, hint: "e.g. main or release/*" },
            { name: 'requirePullRequest', label: 'Require a pull request', type: 'select', value: 'true', options: yn() },
            { name: 'requiredApprovals', label: 'Required approvals', value: '0' },
            { name: 'blockForcePush', label: 'Block force pushes', type: 'select', value: 'true', options: yn() },
            { name: 'blockDeletion', label: 'Block deletion', type: 'select', value: 'true', options: yn() },
            { name: 'requireLinearHistory', label: 'Require linear history', type: 'select', value: 'false', options: yn() },
            { name: 'restrictPush', label: 'Restrict direct pushes to maintainers', type: 'select', value: 'false', options: yn() },
          ],
          submitLabel: 'Add rule',
          onSubmit: async (v) => {
            await api.post(`${R(slug, repoSlug)}/protections`, {
              pattern: v.pattern, requirePullRequest: v.requirePullRequest === 'true',
              requiredApprovals: +v.requiredApprovals || 0, requireStatusChecks: false,
              blockForcePush: v.blockForcePush === 'true', blockDeletion: v.blockDeletion === 'true',
              requireLinearHistory: v.requireLinearHistory === 'true', restrictPush: v.restrictPush === 'true',
            });
            refreshProtections();
          },
        }));
    }
    await refreshProtections();

    // ---- webhooks ----
    const hooks = el('div', { class: 'card' });
    async function refreshHooks() {
      const list = await api.get(`${R(slug, repoSlug)}/hooks`);
      hooks.replaceChildren(
        el('h2', {}, 'Webhooks'),
        list.length ? el('table', { class: 'data-table' }, el('tbody', {}, ...list.map((h) => el('tr', {},
          el('td', {}, el('code', {}, h.url), el('div', { class: 'muted' }, h.events)),
          el('td', { class: 'right' }, el('button', { class: 'small danger', onclick: async () => {
            await api.del(`${R(slug, repoSlug)}/hooks/${h.id}`); refreshHooks();
          } }, 'Remove')))))) : el('p', { class: 'muted' }, 'No webhooks.'),
        form({
          fields: [
            { name: 'url', label: 'Payload URL', required: true },
            { name: 'secret', label: 'Secret', hint: 'HMAC-SHA256 signs X-GitSrv-Signature-256' },
            { name: 'events', label: 'Events', value: 'push', hint: 'comma-separated: push,pull_request,issues' },
          ],
          submitLabel: 'Add webhook',
          onSubmit: async (v) => { await api.post(`${R(slug, repoSlug)}/hooks`, { ...v, isActive: true }); refreshHooks(); },
        }));
    }
    await refreshHooks();

    // ---- action secrets ----
    const secretsCard = el('div', { class: 'card' });
    async function refreshSecrets() {
      const list = await api.get(`${R(slug, repoSlug)}/secrets`);
      secretsCard.replaceChildren(
        el('h2', {}, 'Action secrets'),
        el('p', { class: 'muted' }, 'Injected as environment variables into workflow jobs and masked in logs.'),
        list.length ? el('table', { class: 'data-table' }, el('tbody', {}, ...list.map((s) => el('tr', {},
          el('td', {}, el('code', {}, s.name), el('span', { class: 'muted' }, ` updated ${timeAgo(s.updatedAt)}`)),
          el('td', { class: 'right' }, el('button', { class: 'small danger', onclick: async () => {
            await api.del(`${R(slug, repoSlug)}/secrets/${s.name}`); refreshSecrets();
          } }, 'Remove')))))) : el('p', { class: 'muted' }, 'No secrets.'),
        form({
          fields: [
            { name: 'name', label: 'Name', required: true, hint: 'UPPER_SNAKE_CASE' },
            { name: 'value', label: 'Value', type: 'password', required: true },
          ],
          submitLabel: 'Save secret',
          onSubmit: async (v) => { await api.put(`${R(slug, repoSlug)}/secrets`, v); toast('Secret saved.', 'ok'); refreshSecrets(); },
        }));
    }
    await refreshSecrets();

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

    return shell(b, repo.defaultBranch, 'settings', el('div', { class: 'stack' }, settings, config, protections, secretsCard, hooks, access));
  });
}

function yn() { return [{ value: 'true', label: 'Yes' }, { value: 'false', label: 'No' }]; }
