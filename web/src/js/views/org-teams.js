import { api } from '../api.js';
import { el, form, toast, errorToast, confirmDialog } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView, orgNav } from './_shared.js';

export function renderOrgTeams(slug, teamSlug) {
  return asyncView(async () => {
    const org = await api.get(`/api/orgs/${slug}`);
    const canAdmin = ['owner', 'admin', 'site-admin'].includes(org.myRole);
    const wrap = el('div', { class: 'stack' });
    wrap.append(el('h1', {}, `${org.name} · Teams`), orgNav(slug, org.myRole));

    if (teamSlug) {
      wrap.append(await teamDetail());
      return wrap;
    }

    const teams = await api.get(`/api/orgs/${slug}/teams`);
    const list = el('ul', { class: 'repo-list' });
    for (const t of teams) {
      list.append(el('li', {},
        el('a', { href: `#/o/${slug}/teams/${t.slug}` }, el('strong', {}, t.name)),
        el('span', { class: 'muted' }, `${t.memberCount} member${t.memberCount === 1 ? '' : 's'}`)));
    }
    wrap.append(teams.length ? list : el('div', { class: 'card empty muted' }, 'No teams yet.'));

    if (canAdmin) {
      wrap.append(el('div', { class: 'card' }, el('h2', {}, 'New team'), form({
        fields: [
          { name: 'name', label: 'Name', required: true },
          { name: 'slug', label: 'URL slug', required: true },
          { name: 'description', label: 'Description' },
        ],
        submitLabel: 'Create team',
        onSubmit: async (v) => {
          const t = await api.post(`/api/orgs/${slug}/teams`, v);
          toast(`Created ${t.name}.`, 'ok');
          navigate(`/o/${slug}/teams/${t.slug}`);
        },
      })));
    }
    return wrap;

    async function teamDetail() {
      const t = await api.get(`/api/orgs/${slug}/teams/${teamSlug}`);
      const card = el('div', { class: 'card' });
      const membersHost = el('div', {});

      function render() {
        card.replaceChildren(
          el('div', { class: 'page-head' },
            el('h2', {}, t.name),
            canAdmin && el('button', {
              class: 'danger small',
              onclick: async () => {
                if (!await confirmDialog(`Delete team ${t.name}?`)) return;
                await api.del(`/api/orgs/${slug}/teams/${teamSlug}`);
                toast('Team deleted.', 'ok');
                navigate(`/o/${slug}/teams`);
              },
            }, 'Delete team')),
          t.description && el('p', { class: 'muted' }, t.description),
          membersHost);
        renderMembers(t.members);
      }

      function renderMembers(members) {
        const rows = members.map((m) => el('tr', {},
          el('td', {}, el('strong', {}, m.username), m.displayName && el('span', { class: 'muted' }, ` ${m.displayName}`)),
          el('td', { class: 'right' }, canAdmin && el('button', {
            class: 'danger small',
            onclick: async () => {
              await api.del(`/api/orgs/${slug}/teams/${teamSlug}/members/${m.userId}`);
              t.members = t.members.filter((x) => x.userId !== m.userId);
              render();
            },
          }, 'Remove'))));
        const children = [el('table', { class: 'data-table' },
          el('thead', {}, el('tr', {}, el('th', {}, 'Member'), el('th', {}, ''))),
          el('tbody', {}, ...(rows.length ? rows : [el('tr', {}, el('td', { colspan: 2, class: 'muted' }, 'No members yet.'))])))];
        if (canAdmin) {
          children.push(form({
            fields: [{ name: 'username', label: 'Add org member to team', required: true }],
            submitLabel: 'Add',
            onSubmit: async (v) => {
              await api.post(`/api/orgs/${slug}/teams/${teamSlug}/members`, v);
              const fresh = await api.get(`/api/orgs/${slug}/teams/${teamSlug}`);
              t.members = fresh.members;
              render();
            },
          }));
        }
        membersHost.replaceChildren(...children);
      }

      render();
      return card;
    }
  });
}
