import { route, setNotFound, startRouter, navigate, currentPath } from './router.js';
import { session } from './session.js';
import { el, clear } from './ui.js';
import { initThemeToggle } from './features/theme.js';

import { renderAuth } from './views/auth.js';
import { renderDashboard } from './views/dashboard.js';
import { renderSettings } from './views/settings.js';
import { renderNewOrg } from './views/new-org.js';
import { renderOrg } from './views/org.js';
import { renderOrgPeople } from './views/org-people.js';
import { renderOrgTeams } from './views/org-teams.js';
import { renderRepo } from './views/repo.js';

initThemeToggle();

const view = document.getElementById('view');
const nav = document.getElementById('nav');
const userMenu = document.getElementById('user-menu');

function mount(node) { clear(view).append(node); }

function requireAuth(fn) {
  return (ctx) => {
    if (!session.isAuthenticated) { navigate('/login'); return; }
    fn(ctx);
  };
}

route('/', requireAuth(() => mount(renderDashboard())));
route('/login', () => mount(renderAuth()));
route('/settings', requireAuth(() => mount(renderSettings())));
route('/new', requireAuth(() => mount(renderNewOrg())));
route('/o/:slug', requireAuth((ctx) => mount(renderOrg(ctx.params.slug))));
route('/o/:slug/people', requireAuth((ctx) => mount(renderOrgPeople(ctx.params.slug))));
route('/o/:slug/teams', requireAuth((ctx) => mount(renderOrgTeams(ctx.params.slug, null))));
route('/o/:slug/teams/:teamSlug', requireAuth((ctx) => mount(renderOrgTeams(ctx.params.slug, ctx.params.teamSlug))));
route('/o/:slug/:repo', requireAuth((ctx) => mount(renderRepo(ctx.params.slug, ctx.params.repo))));
setNotFound(() => mount(el('div', { class: 'card' }, el('h1', {}, 'Not found'),
  el('p', { class: 'lede' }, 'That page does not exist.'))));

session.onChange(renderChrome);

function renderChrome() {
  clear(nav);
  clear(userMenu);

  if (!session.isAuthenticated) {
    userMenu.append(el('a', { href: '#/login', class: 'btn-link' }, 'Sign in'));
    return;
  }

  const orgs = session.organisations;
  if (orgs.length) {
    const sel = el('select', { class: 'org-switcher', onchange: (e) => {
      if (e.target.value) navigate(`/o/${e.target.value}`);
    }});
    sel.append(el('option', { value: '' }, 'Switch org…'));
    for (const o of orgs) sel.append(el('option', { value: o.slug }, `${o.name} (${o.role})`));
    nav.append(sel);
  }
  nav.append(el('a', { href: '#/new', class: 'btn-link' }, '+ New org'));

  userMenu.append(
    el('a', { href: '#/settings', class: 'btn-link', title: 'Settings' }, session.user.username),
    el('button', { onclick: async () => { await session.logout(); navigate('/login'); } }, 'Sign out'),
  );
}

(async () => {
  await session.load();
  renderChrome();
  startRouter();
  // After login the auth view calls navigate('/'); guard the very first load too.
  if (!session.isAuthenticated && currentPath() !== '/login') navigate('/login');
})();
