import { api } from '../api.js';
import { el, timeAgo, toast } from '../ui.js';
import { asyncView } from './_shared.js';

export function renderInbox(query) {
  return asyncView(async () => build());

  async function build() {
    const q = new URLSearchParams(query || '');
    const unread = q.get('filter') === 'unread';
    const items = await api.get(`/api/notifications/?filter=${unread ? 'unread' : 'all'}`);
    const refresh = async () => document.getElementById('view').replaceChildren(await renderInbox(query));

    const tab = (s, label) => el('a', {
      href: `#/inbox${s ? '?filter=unread' : ''}`, class: 'tab' + ((unread ? 'unread' : '') === s ? ' active' : ''),
    }, label);

    const rows = items.map((n) => el('div', { class: 'pr-row notif' + (n.isRead ? ' read' : '') },
      el('div', {},
        el('a', { href: n.url || '#', onclick: async () => { await api.post('/api/notifications/mark', { ids: [n.id], read: true }); } },
          el('strong', {}, n.title)),
        el('div', { class: 'muted' }, `${n.orgSlug ? n.orgSlug + '/' + n.repoSlug + ' · ' : ''}${n.subjectKind} #${n.subjectNumber ?? ''} · ${n.reason} · ${timeAgo(n.createdAt)}`),
        n.body && el('div', { class: 'muted clip' }, n.body)),
      el('button', { class: 'small', onclick: async () => {
        await api.post('/api/notifications/mark', { ids: [n.id], read: !n.isRead }); refresh();
      } }, n.isRead ? 'Mark unread' : 'Mark read')));

    return el('div', { class: 'stack' },
      el('div', { class: 'page-head' },
        el('h1', {}, 'Notifications'),
        el('div', { class: 'row' },
          el('nav', { class: 'sub-nav' }, tab('', 'All'), tab('unread', 'Unread')),
          el('button', { onclick: async () => { await api.post('/api/notifications/read-all'); toast('Marked all read.', 'ok'); refresh(); } }, 'Mark all read'))),
      rows.length ? el('div', { class: 'card nopad' }, ...rows) : el('div', { class: 'card empty muted' }, 'Nothing here.'));
  }
}
