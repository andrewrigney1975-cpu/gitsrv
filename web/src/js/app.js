import { route, setNotFound, startRouter, navigate, currentPath } from './router.js';
import { session } from './session.js';
import { api } from './api.js';
import { el, clear } from './ui.js';
import { initThemeToggle } from './features/theme.js';

// Core views load eagerly; everything else is code-split via dynamic import() and fetched only
// when its route is first visited.
import { renderAuth } from './views/auth.js';
import { renderDashboard } from './views/dashboard.js';
import { renderRepoCode } from './views/repo.js';

initThemeToggle();

const view = document.getElementById('view');
const nav = document.getElementById('nav');
const userMenu = document.getElementById('user-menu');

function mount(node) { clear(view).append(node); }

// Build id from this module's own URL (index.html stamps ?v=… on the app.js tag). Appended to
// every dynamic import so a single deploy invalidates the whole module graph.
const BUILD = new URL(import.meta.url).searchParams.get('v') || '';
const bust = (p) => (BUILD ? `${p}?v=${BUILD}` : p);

// lazy(modulePath, exportName) -> (…args) that imports on demand and mounts the result
function lazy(path, name) {
  return (...args) => {
    mount(el('div', { class: 'card muted', text: 'Loading…' }));
    import(bust(path))
      .then((m) => mount(m[name](...args)))
      .catch((err) => mount(el('div', { class: 'card' }, el('h1', {}, 'Failed to load'), el('p', { class: 'muted', text: err.message }))));
  };
}

function requireAuth(fn) {
  return (ctx) => {
    if (!session.isAuthenticated) { navigate('/login'); return; }
    fn(ctx);
  };
}
const P = (ctx) => ctx.params;

route('/', requireAuth(() => mount(renderDashboard())));
route('/login', () => mount(renderAuth()));
route('/settings', requireAuth(() => lazy('./views/settings.js', 'renderSettings')()));
route('/new', requireAuth(() => lazy('./views/new-org.js', 'renderNewOrg')()));
route('/inbox', requireAuth((ctx) => lazy('./views/inbox.js', 'renderInbox')(ctx.query)));

route('/o/:slug', requireAuth((ctx) => lazy('./views/org.js', 'renderOrg')(P(ctx).slug)));
route('/o/:slug/people', requireAuth((ctx) => lazy('./views/org-people.js', 'renderOrgPeople')(P(ctx).slug)));
route('/o/:slug/settings', requireAuth((ctx) => lazy('./views/org-settings.js', 'renderOrgSettings')(P(ctx).slug)));
route('/o/:slug/teams', requireAuth((ctx) => lazy('./views/org-teams.js', 'renderOrgTeams')(P(ctx).slug, null)));
route('/o/:slug/teams/:teamSlug', requireAuth((ctx) => lazy('./views/org-teams.js', 'renderOrgTeams')(P(ctx).slug, P(ctx).teamSlug)));
route('/o/:slug/packages/:kind/:name', requireAuth((ctx) => lazy('./views/packages.js', 'renderPackageDetail')(P(ctx).slug, P(ctx).kind, P(ctx).name)));
route('/o/:slug/packages', requireAuth((ctx) => lazy('./views/packages.js', 'renderPackages')(P(ctx).slug)));

// Repository routes — more specific first (the router takes the first match).
route('/o/:slug/:repo/blob/:ref/*path', (ctx) => lazy('./views/repo.js', 'renderRepoBlob')(P(ctx).slug, P(ctx).repo, P(ctx).ref, P(ctx).path));
route('/o/:slug/:repo/blame/:ref/*path', (ctx) => lazy('./views/repo.js', 'renderRepoBlame')(P(ctx).slug, P(ctx).repo, P(ctx).ref, P(ctx).path));
route('/o/:slug/:repo/tree/:ref/*path', (ctx) => mount(renderRepoCode(P(ctx).slug, P(ctx).repo, P(ctx).ref, P(ctx).path)));
route('/o/:slug/:repo/commits/:ref', (ctx) => lazy('./views/repo-history.js', 'renderRepoCommits')(P(ctx).slug, P(ctx).repo, P(ctx).ref, ctx.query));
route('/o/:slug/:repo/commit/:sha', (ctx) => lazy('./views/repo-history.js', 'renderRepoCommit')(P(ctx).slug, P(ctx).repo, P(ctx).sha));
route('/o/:slug/:repo/graph', (ctx) => lazy('./views/repo-history.js', 'renderRepoGraph')(P(ctx).slug, P(ctx).repo));
route('/o/:slug/:repo/pulls/new', requireAuth((ctx) => lazy('./views/pulls.js', 'renderNewPull')(P(ctx).slug, P(ctx).repo, ctx.query)));
route('/o/:slug/:repo/pulls/:number', (ctx) => lazy('./views/pulls.js', 'renderPullDetail')(P(ctx).slug, P(ctx).repo, ctx.params.number));
route('/o/:slug/:repo/pulls', (ctx) => lazy('./views/pulls.js', 'renderPullList')(P(ctx).slug, P(ctx).repo, ctx.query));
route('/o/:slug/:repo/issues/new', requireAuth((ctx) => lazy('./views/issues.js', 'renderNewIssue')(P(ctx).slug, P(ctx).repo)));
route('/o/:slug/:repo/issues/:number', (ctx) => lazy('./views/issues.js', 'renderIssueDetail')(P(ctx).slug, P(ctx).repo, ctx.params.number));
route('/o/:slug/:repo/issues', (ctx) => lazy('./views/issues.js', 'renderIssueList')(P(ctx).slug, P(ctx).repo, ctx.query));
route('/o/:slug/:repo/labels', requireAuth((ctx) => lazy('./views/issues.js', 'renderLabels')(P(ctx).slug, P(ctx).repo)));
route('/o/:slug/:repo/milestones', requireAuth((ctx) => lazy('./views/issues.js', 'renderMilestones')(P(ctx).slug, P(ctx).repo)));
route('/o/:slug/:repo/releases/new', requireAuth((ctx) => lazy('./views/releases.js', 'renderNewRelease')(P(ctx).slug, P(ctx).repo)));
route('/o/:slug/:repo/releases/:tag', (ctx) => lazy('./views/releases.js', 'renderReleaseDetail')(P(ctx).slug, P(ctx).repo, ctx.params.tag));
route('/o/:slug/:repo/releases', (ctx) => lazy('./views/releases.js', 'renderReleases')(P(ctx).slug, P(ctx).repo));
route('/o/:slug/:repo/actions/:number', (ctx) => lazy('./views/actions.js', 'renderActionRun')(P(ctx).slug, P(ctx).repo, ctx.params.number));
route('/o/:slug/:repo/actions', (ctx) => lazy('./views/actions.js', 'renderActionsList')(P(ctx).slug, P(ctx).repo));
route('/o/:slug/:repo/settings', requireAuth((ctx) => lazy('./views/repo-settings.js', 'renderRepoSettings')(P(ctx).slug, P(ctx).repo)));
route('/o/:slug/:repo', (ctx) => mount(renderRepoCode(P(ctx).slug, P(ctx).repo, null, null)));

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

  const inbox = el('a', { href: '#/inbox', class: 'btn-link inbox-link', title: 'Notifications' }, 'Inbox');
  userMenu.append(
    inbox,
    el('a', { href: '#/settings', class: 'btn-link', title: 'Settings' }, session.user.username),
    el('button', { onclick: async () => { await session.logout(); navigate('/login'); } }, 'Sign out'),
  );
  api.get('/api/notifications/count').then((c) => {
    if (c.unread > 0) inbox.append(el('span', { class: 'badge' }, String(c.unread)));
  }).catch(() => {});
}

(async () => {
  await session.load();
  renderChrome();
  const path = currentPath();
  const anonOk = path === '/login' || /^\/o\/[^/]+\/[^/]+/.test(path);
  if (!session.isAuthenticated && !anonOk) { navigate('/login'); }
  startRouter();
})();
