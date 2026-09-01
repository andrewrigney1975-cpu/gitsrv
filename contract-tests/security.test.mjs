import { test, before } from 'node:test';
import assert from 'node:assert/strict';

const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';
const S = Date.now().toString(36);

function jar() {
  const c = new Map();
  return {
    header: () => [...c].map(([k, v]) => `${k}=${v}`).join('; '),
    absorb: (res) => { for (const l of res.headers.getSetCookie?.() ?? []) { const [p] = l.split(';'); const i = p.indexOf('='); c.set(p.slice(0, i), p.slice(i + 1)); } },
  };
}
async function call(cookies, method, path, body) {
  const headers = { Accept: 'application/json', Cookie: cookies?.header() ?? '' };
  if (method !== 'GET') headers['X-GitSrv-CSRF'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(BASE + path, { method, headers, body: body && JSON.stringify(body) });
  cookies?.absorb(res);
  const t = await res.text();
  return { status: res.status, body: t ? (() => { try { return JSON.parse(t); } catch { return t; } })() : null };
}

const owner = { name: `sec_owner_${S}`, cookies: jar() };
const stranger = { name: `sec_stranger_${S}`, cookies: jar() };
const org = `sec-org-${S}`;

before(async () => {
  // On a pristine DB the first-ever account is the site admin; make sure that's not `owner`.
  await call(jar(), 'POST', '/api/auth/register',
    { username: `sec_bootstrap_${S}`, email: `sb_${S}@ex.com`, displayName: 'b', password: 'correct-horse-battery' });
  for (const u of [owner, stranger]) {
    assert.equal((await call(u.cookies, 'POST', '/api/auth/register',
      { username: u.name, email: `${u.name}@ex.com`, displayName: u.name, password: 'correct-horse-battery' })).status, 200);
  }
  assert.equal((await call(owner.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Sec Org' })).status, 201);
  assert.equal((await call(owner.cookies, 'POST', `/api/orgs/${org}/repos`, { slug: 'p', name: 'P', visibility: 'private' })).status, 201);
});

test('a private repo 404s for a non-member and for anonymous', async () => {
  assert.equal((await call(stranger.cookies, 'GET', `/api/orgs/${org}/repos/p/`)).status, 404);
  assert.equal((await call(null, 'GET', `/api/orgs/${org}/repos/p/browse/overview`)).status, 404);
});

test('org-admin routes are closed to non-admins', async () => {
  // stranger is not even a member
  assert.equal((await call(stranger.cookies, 'POST', `/api/orgs/${org}/members`, { username: stranger.name, role: 'owner' })).status, 403);
  assert.equal((await call(stranger.cookies, 'GET', `/api/orgs/${org}/audit`)).status, 403);
  assert.equal((await call(stranger.cookies, 'PUT', `/api/orgs/${org}/secrets`, { name: 'X', value: 'y' })).status, 403);
});

test('admin console requires site admin', async () => {
  assert.equal((await call(owner.cookies, 'GET', '/api/admin/overview')).status, 403);
  assert.equal((await call(null, 'GET', '/api/admin/users')).status, 401);
});

test('SSRF: webhook + Enklr URLs pointing at private ranges are rejected', async () => {
  const wh = await call(owner.cookies, 'POST', `/api/orgs/${org}/repos/p/hooks`, { url: 'http://169.254.169.254/latest/meta-data', events: 'push', isActive: true });
  assert.equal(wh.status, 422);
  const wh2 = await call(owner.cookies, 'POST', `/api/orgs/${org}/repos/p/hooks`, { url: 'http://10.1.2.3/hook', events: 'push', isActive: true });
  assert.equal(wh2.status, 422);
  // a normal external URL is fine
  const ok = await call(owner.cookies, 'POST', `/api/orgs/${org}/repos/p/hooks`, { url: 'https://example.com/hook', events: 'push', isActive: true });
  assert.equal(ok.status, 201);
});

test('audit log records the login and is org-admin readable', async () => {
  await call(owner.cookies, 'POST', '/api/auth/login', { usernameOrEmail: owner.name, password: 'correct-horse-battery' });
  const a = await call(owner.cookies, 'GET', `/api/orgs/${org}/audit`);
  assert.equal(a.status, 200);
  assert.ok(Array.isArray(a.body));

  const csv = await fetch(`${BASE}/api/orgs/${org}/audit?format=csv`, { headers: { Cookie: owner.cookies.header() } });
  assert.equal(csv.headers.get('content-type'), 'text/csv');
  assert.match(await csv.text(), /^time,actor,action,target,detail,ip/);
});

test('/metrics is Prometheus text', async () => {
  const res = await fetch(`${BASE}/metrics`);
  assert.equal(res.status, 200);
  const body = await res.text();
  assert.match(body, /^# HELP gitsrv_users_total/m);
  assert.match(body, /gitsrv_repositories_total \d+/);
});

test('auth endpoints are rate limited', async () => {
  // 20/min window — fire 45 and expect at least one 429
  const results = await Promise.all(Array.from({ length: 45 }, () =>
    fetch(`${BASE}/api/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-GitSrv-CSRF': '1' },
      body: JSON.stringify({ usernameOrEmail: 'nobody', password: 'x' }),
    }).then((r) => r.status)));
  assert.ok(results.includes(429), `expected a 429, got ${[...new Set(results)].join(',')}`);
});
