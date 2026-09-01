import { api } from '../api.js';
import { el, form, toast, errorToast, confirmDialog } from '../ui.js';
import { asyncView, orgNav } from './_shared.js';

export function renderOrgSettings(slug) {
  return asyncView(async () => {
    const org = await api.get(`/api/orgs/${slug}`);
    if (!['owner', 'admin', 'site-admin'].includes(org.myRole)) {
      return el('div', { class: 'card muted' }, 'Org admin permission required.');
    }

    const host = el('div', { class: 'stack' }, el('h1', {}, `${org.name} · Settings`), orgNav(slug, org.myRole));

    // ---- Enklr integration ----
    const enklrCard = el('div', { class: 'card' });
    async function refreshEnklr() {
      const c = await api.get(`/api/orgs/${slug}/enklr`);
      const children = [
        el('h2', {}, 'Enklr.app integration'),
        el('p', { class: 'muted' }, 'Link commits, branches and pull requests to Enklr cards with ',
          el('code', {}, `${c.cardPrefix || 'ENK'}-123`), ' references. Cards then show linked work and its status.'),
      ];
      if (c.connected) {
        children.push(el('p', {}, 'Connected to ', el('code', {}, c.baseUrl), c.workspace && ` · workspace ${c.workspace}`));
        children.push(el('button', { class: 'danger small', onclick: async () => {
          if (!await confirmDialog('Disconnect Enklr?')) return;
          await api.del(`/api/orgs/${slug}/enklr`); toast('Disconnected.', 'ok'); refreshEnklr();
        } }, 'Disconnect'));
      }
      children.push(form({
        fields: [
          { name: 'baseUrl', label: 'Enklr base URL', value: c.baseUrl || '', required: true },
          { name: 'workspace', label: 'Workspace / board', value: c.workspace || '' },
          { name: 'cardPrefix', label: 'Card reference prefix', value: c.cardPrefix || 'ENK' },
          { name: 'apiToken', label: 'Enklr API token', type: 'password', hint: 'GitSrv uses this to call Enklr' },
          { name: 'inboundSecret', label: 'Inbound webhook secret', type: 'password', hint: 'Enklr signs its callbacks with this' },
        ],
        submitLabel: c.connected ? 'Update connection' : 'Connect',
        onSubmit: async (v) => { await api.put(`/api/orgs/${slug}/enklr`, v); toast('Saved.', 'ok'); refreshEnklr(); },
      }));
      enklrCard.replaceChildren(...children);
    }
    await refreshEnklr();
    host.append(enklrCard);

    // ---- org secrets ----
    const secretsCard = el('div', { class: 'card' });
    async function refreshSecrets() {
      const list = await api.get(`/api/orgs/${slug}/secrets`);
      secretsCard.replaceChildren(
        el('h2', {}, 'Organisation action secrets'),
        el('p', { class: 'muted' }, 'Available to every repo’s workflows in this org.'),
        list.length ? el('table', { class: 'data-table' }, el('tbody', {}, ...list.map((s) => el('tr', {},
          el('td', {}, el('code', {}, s.name)),
          el('td', { class: 'right' }, el('button', { class: 'small danger', onclick: async () => {
            await api.del(`/api/orgs/${slug}/secrets/${s.name}`); refreshSecrets();
          } }, 'Remove')))))) : el('p', { class: 'muted' }, 'None.'),
        form({
          fields: [{ name: 'name', label: 'Name', required: true }, { name: 'value', label: 'Value', type: 'password', required: true }],
          submitLabel: 'Save secret',
          onSubmit: async (v) => { await api.put(`/api/orgs/${slug}/secrets`, v); toast('Saved.', 'ok'); refreshSecrets(); },
        }));
    }
    await refreshSecrets().catch(() => {});
    host.append(secretsCard);

    return host;
  });
}
