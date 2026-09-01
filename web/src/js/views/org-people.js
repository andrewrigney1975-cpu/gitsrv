import { api } from '../api.js';
import { session } from '../session.js';
import { el, form, toast, errorToast, confirmDialog } from '../ui.js';
import { asyncView, orgNav } from './_shared.js';

const ROLES = ['owner', 'admin', 'member'];

export function renderOrgPeople(slug) {
  return asyncView(async () => {
    const org = await api.get(`/api/orgs/${slug}`);
    const canAdmin = ['owner', 'admin', 'site-admin'].includes(org.myRole);
    const isOwner = ['owner', 'site-admin'].includes(org.myRole);

    const wrap = el('div', { class: 'stack' });
    wrap.append(el('h1', {}, `${org.name} · People`), orgNav(slug, org.myRole));

    const tableHost = el('div', { class: 'card' });
    wrap.append(tableHost);
    await refresh();

    if (canAdmin) {
      wrap.append(el('div', { class: 'card' }, el('h2', {}, 'Add a member'), form({
        fields: [
          { name: 'username', label: 'Username', required: true },
          { name: 'role', label: 'Role', type: 'select', value: 'member',
            options: ROLES.map((r) => ({ value: r, label: r })) },
        ],
        submitLabel: 'Add member',
        onSubmit: async (v) => {
          await api.post(`/api/orgs/${slug}/members`, v);
          toast(`Added ${v.username}.`, 'ok');
          await refresh();
        },
      })));
    }

    async function refresh() {
      const members = await api.get(`/api/orgs/${slug}/members`);
      const rows = members.map((m) => el('tr', {},
        el('td', {}, el('strong', {}, m.username),
          m.displayName && el('span', { class: 'muted' }, ` ${m.displayName}`)),
        el('td', {}, isOwner
          ? el('select', { value: m.role, onchange: async (e) => {
              try { await api.patch(`/api/orgs/${slug}/members/${m.userId}`, { role: e.target.value }); toast('Role updated.', 'ok'); }
              catch (err) { errorToast(err); await refresh(); }
            }}, ...ROLES.map((r) => el('option', { value: r, selected: r === m.role }, r)))
          : el('span', { class: `pill role-${m.role}` }, m.role)),
        el('td', { class: 'right' }, canAdmin && m.userId !== session.user.id && el('button', {
          class: 'danger small',
          onclick: async () => {
            if (!await confirmDialog(`Remove ${m.username} from ${org.name}?`)) return;
            try { await api.del(`/api/orgs/${slug}/members/${m.userId}`); toast('Removed.', 'ok'); await refresh(); }
            catch (err) { errorToast(err); }
          },
        }, 'Remove'))));
      tableHost.replaceChildren(el('table', { class: 'data-table' },
        el('thead', {}, el('tr', {}, el('th', {}, 'Member'), el('th', {}, 'Role'), el('th', {}, ''))),
        el('tbody', {}, ...rows)));
    }

    return wrap;
  });
}
