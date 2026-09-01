import { route, setNotFound, startRouter, navigate, currentPath } from './router.js';
import { session } from './session.js';
import { api } from './api.js';
import { el, clear } from './ui.js';
import { initThemeToggle } from './features/theme.js';

import { renderAuth } from './views/auth.js';
import { renderDashboard } from './views/dashboard.js';
import { renderSettings } from './views/settings.js';
import { renderNewOrg } from './views/new-org.js';
import { renderOrg } from './views/org.js';
import { renderOrgPeople } from './views/org-people.js';
import { renderOrgTeams } from './views/org-teams.js';
import { renderRepoCode, renderRepoBlob, renderRepoBlame } from './views/repo.js';
import { renderRepoCommits, renderRepoCommit, renderRepoGraph } from './views/repo-history.js';
import { renderRepoSettings } from './views/repo-settings.js';
import { renderPullList, renderNewPull, renderPullDetail } from './views/pulls.js';
import { renderIssueList, renderNewIssue, renderIssueDetail, renderLabels, renderMilestones } from './views/issues.js';
import { renderInbox } from './views/inbox.js';
import { renderReleases, renderNewRelease, renderReleaseDetail } from './views/releases.js';

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

// Repository browsing — more specific routes first (the router takes the first match).
const P = (ctx) => ctx.params;
route('/o/:slug/:repo/blob/:ref/*path', (ctx) => mount(renderRepoBlob(P(ctx).slug, P(ctx).repo, P(ctx).ref, P(ctx).path)));
route('/o/:slug/:repo/blame/:ref/*path', (ctx) => mount(renderRepoBlame(P(ctx).slug, P(ctx).repo, P(ctx).ref, P(ctx).path)));
route('/o/:slug/:repo/tree/:ref/*path', (ctx) => mount(renderRepoCode(P(ctx).slug, P(ctx).repo, P(ctx).ref, P(ctx).path)));
route('/o/:slug/:repo/commits/:ref', (ctx) => mount(renderRepoCommits(P(ctx).slug, P(ctx).repo, P(ctx).ref, ctx.query)));
route('/o/:slug/:repo/commit/:sha', (ctx) => mount(renderRepoCommit(P(ctx).slug, P(ctx).repo, P(ctx).sha)));
route('/o/:slug/:repo/pulls/new', requireAuth((ctx) => mount(renderNewPull(P(ctx).slug, P(ctx).repo, ctx.query))));
route('/o/:slug/:repo/pulls/:number', (ctx) => mount(renderPullDetail(P(ctx).slug, P(ctx).repo, ctx.params.number)));
route('/o/:slug/:repo/pulls', (ctx) => mount(renderPullList(P(ctx).slug, P(ctx).repo, ctx.query)));
route('/o/:slug/:repo/issues/new', requireAuth((ctx) => mount(renderNewIssue(P(ctx).slug, P(ctx).repo))));
route('/o/:slug/:repo/issues/:number', (ctx) => mount(renderIssueDetail(P(ctx).slug, P(ctx).repo, ctx.params.number)));
route('/o/:slug/:repo/issues', (ctx) => mount(renderIssueList(P(ctx).slug, P(ctx).repo, ctx.query)));
route('/o/:slug/:repo/releases/new', requireAuth((ctx) => mount(renderNewRelease(P(ctx).slug, P(ctx).repo))));
route('/o/:slug/:repo/releases/:tag', (ctx) => mount(renderReleaseDetail(P(ctx).slug, P(ctx).repo, ctx.params.tag)));
route('/o/:slug/:repo/releases', (ctx) => mount(renderReleases(P(ctx).slug, P(ctx).repo)));
route('/o/:slug/:repo/labels', requireAuth((ctx) => mount(renderLabels(P(ctx).slug, P(ctx).repo))));
route('/o/:slug/:repo/milestones', requireAuth((ctx) => mount(renderMilestones(P(ctx).slug, P(ctx).repo))));
route('/inbox', requireAuth((ctx) => mount(renderInbox(ctx.query))));
route('/o/:slug/:repo/graph', (ctx) => mount(renderRepoGraph(P(ctx).slug, P(ctx).repo)));
route('/o/:slug/:repo/settings', requireAuth((ctx) => mount(renderRepoSettings(P(ctx).slug, P(ctx).repo))));
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
  // Send anonymous visitors to sign-in unless they're deep-linking to a repo page (public repos
  // are browsable without an account) or already on the login screen.
  const path = currentPath();
  const anonOk = path === '/login' || /^\/o\/[^/]+\/[^/]+/.test(path);
  if (!session.isAuthenticated && !anonOk) { navigate('/login'); }
  startRouter();
})();
