import { test } from 'node:test';
import assert from 'node:assert/strict';

// Exercises the Phase 1 identity flows end to end against a running stack. Each run uses a unique
// suffix so the suite is idempotent against a persistent database.
const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';
const S = Date.now().toString(36);

function jar() {
  const cookies = new Map();
  return {
    header: () => [...cookies.entries()].map(([k, v]) => `${k}=${v}`).join('; '),
    absorb: (res) => {
      for (const c of res.headers.getSetCookie?.() ?? []) {
        const [pair] = c.split(';');
        const i = pair.indexOf('=');
        cookies.set(pair.slice(0, i), pair.slice(i + 1));
      }
    },
  };
}

async function call(cookies, method, path, body) {
  const headers = { Accept: 'application/json', Cookie: cookies.header() };
  if (method !== 'GET') headers['X-GitSrv-CSRF'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(BASE + path, { method, headers, body: body && JSON.stringify(body) });
  cookies.absorb(res);
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

test('register → create org → add member → team → repo → permissions', async () => {
  const alice = jar();
  const bob = jar();

  const aliceName = `alice_${S}`;
  const bobName = `bob_${S}`;
  const orgSlug = `acme-${S}`;

  // Register both users.
  let r = await call(alice, 'POST', '/api/auth/register',
    { username: aliceName, email: `${aliceName}@ex.com`, displayName: 'Alice', password: 'correct-horse-battery' });
  assert.equal(r.status, 200, JSON.stringify(r.body));
  assert.equal(r.body.username, aliceName);

  r = await call(bob, 'POST', '/api/auth/register',
    { username: bobName, email: `${bobName}@ex.com`, displayName: 'Bob', password: 'correct-horse-battery' });
  assert.equal(r.status, 200);

  // Alice creates an org (she becomes owner).
  r = await call(alice, 'POST', '/api/orgs/', { slug: orgSlug, name: 'Acme', description: 'test' });
  assert.equal(r.status, 201, JSON.stringify(r.body));

  // Bob can't see it yet.
  r = await call(bob, 'GET', `/api/orgs/${orgSlug}`);
  assert.equal(r.status, 404);

  // Alice adds Bob as a member.
  r = await call(alice, 'POST', `/api/orgs/${orgSlug}/members`, { username: bobName, role: 'member' });
  assert.equal(r.status, 204);

  // Now Bob sees it, as a member.
  r = await call(bob, 'GET', `/api/orgs/${orgSlug}`);
  assert.equal(r.status, 200);
  assert.equal(r.body.myRole, 'member');

  // Alice creates a team and a private repo.
  r = await call(alice, 'POST', `/api/orgs/${orgSlug}/teams`, { slug: 'core', name: 'Core' });
  assert.equal(r.status, 201);
  r = await call(bob, 'GET', `/api/orgs/${orgSlug}/teams`);
  assert.equal(r.status, 200);
  assert.equal(r.body[0].name, 'Core');
  assert.equal(r.body[0].memberCount, 0);
  r = await call(alice, 'POST', `/api/orgs/${orgSlug}/repos`,
    { slug: 'widget', name: 'Widget', visibility: 'private', defaultBranch: 'main' });
  assert.equal(r.status, 201);

  // Bob (plain member) can't see a private repo…
  r = await call(bob, 'GET', `/api/orgs/${orgSlug}/repos/widget/`);
  assert.equal(r.status, 404);

  // …until Alice grants his team write access.
  r = await call(alice, 'POST', `/api/orgs/${orgSlug}/teams/core/members`, { username: bobName });
  assert.equal(r.status, 204);
  r = await call(alice, 'POST', `/api/orgs/${orgSlug}/repos/widget/team-access`, { teamSlug: 'core', permission: 'write' });
  assert.equal(r.status, 204);

  r = await call(bob, 'GET', `/api/orgs/${orgSlug}/repos/widget/`);
  assert.equal(r.status, 200);
  assert.equal(r.body.myPermission, 'write');

  // Bob still can't administer it.
  r = await call(bob, 'PATCH', `/api/orgs/${orgSlug}/repos/widget/`, { visibility: 'public' });
  assert.equal(r.status, 403);

  // CSRF: a cookie-authed unsafe call without the header is rejected.
  const noHeader = await fetch(BASE + `/api/orgs/${orgSlug}/teams`, {
    method: 'POST', headers: { Cookie: alice.header(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ slug: 'x', name: 'X' }),
  });
  assert.equal(noHeader.status, 403);
});

test('slug rename leaves a 301 redirect', async () => {
  const u = jar();
  const name = `carol_${S}`;
  await call(u, 'POST', '/api/auth/register',
    { username: name, email: `${name}@ex.com`, displayName: 'Carol', password: 'correct-horse-battery' });
  const from = `rena-${S}`;
  const to = `renb-${S}`;
  let r = await call(u, 'POST', '/api/orgs/', { slug: from, name: 'Rename Me' });
  assert.equal(r.status, 201);
  r = await call(u, 'POST', `/api/orgs/${from}/rename`, { slug: to });
  assert.equal(r.status, 204);

  const redirect = await fetch(BASE + `/api/orgs/${from}`, {
    headers: { Cookie: u.header(), Accept: 'application/json' }, redirect: 'manual',
  });
  assert.equal(redirect.status, 301);
  assert.match(redirect.headers.get('location'), new RegExp(`/api/orgs/${to}$`));
});
