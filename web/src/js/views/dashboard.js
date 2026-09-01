import { session } from '../session.js';
import { el } from '../ui.js';

export function renderDashboard() {
  const wrap = el('div', { class: 'stack' });
  const orgs = session.organisations;

  wrap.append(el('div', { class: 'page-head' },
    el('h1', {}, `Hi, ${session.user.displayName || session.user.username}`),
    el('a', { href: '#/new', class: 'btn primary' }, 'New organisation')));

  if (!orgs.length) {
    wrap.append(el('div', { class: 'card empty' },
      el('p', {}, 'You are not in any organisations yet.'),
      el('p', { class: 'muted' }, 'Create one to start adding repositories, teams and members.')));
    return wrap;
  }

  const grid = el('div', { class: 'card-grid' });
  for (const o of orgs) {
    grid.append(el('a', { href: `#/o/${o.slug}`, class: 'card org-card' },
      el('strong', {}, o.name),
      el('code', { class: 'muted' }, o.slug),
      el('span', { class: `pill role-${o.role}` }, o.role)));
  }
  wrap.append(grid);
  return wrap;
}
