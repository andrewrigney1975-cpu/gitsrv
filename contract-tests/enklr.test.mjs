import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const run = promisify(execFile);
const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';
const HOST = new URL(BASE).host;
const S = Date.now().toString(36);
const NO_CRED = ['-c', 'credential.helper=', '-c', 'credential.interactive=false'];

function jar() {
  const c = new Map();
  return {
    header: () => [...c].map(([k, v]) => `${k}=${v}`).join('; '),
    absorb: (res) => { for (const l of res.headers.getSetCookie?.() ?? []) { const [p] = l.split(';'); const i = p.indexOf('='); c.set(p.slice(0, i), p.slice(i + 1)); } },
  };
}
async function call(cookies, method, path, body) {
  const headers = { Accept: 'application/json', Cookie: cookies.header() };
  if (method !== 'GET') headers['X-GitSrv-CSRF'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(BASE + path, { method, headers, body: body && JSON.stringify(body) });
  cookies.absorb(res);
  const t = await res.text();
  return { status: res.status, body: t ? JSON.parse(t) : null };
}
const git = (cwd, ...a) => run('git', [...NO_CRED, ...a], { cwd, env: { ...process.env, GIT_TERMINAL_PROMPT: '0' }, timeout: 30000 });

// A stand-in Enklr instance: records everything GitSrv posts to /api/gitsrv/*.
let mock, received = [];
function startMock() {
  return new Promise((resolve) => {
    mock = http.createServer((req, res) => {
      let body = '';
      req.on('data', (d) => (body += d));
      req.on('end', () => {
        received.push({ url: req.url, auth: req.headers.authorization, body: body ? JSON.parse(body) : null });
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end('{}');
      });
    }).listen(0, '0.0.0.0', () => resolve());
  });
}

const alice = { name: `enk_alice_${S}`, cookies: jar() };
const org = `enk-org-${S}`;
const repo = 'svc';
let tmp, dir, mockUrl;
const R = () => `/api/orgs/${org}/repos/${repo}`;

before(async () => {
  await startMock();
  // GitSrv (in a container) reaches the host mock via host.docker.internal
  mockUrl = `http://host.docker.internal:${mock.address().port}`;
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-enk-'));
  assert.equal((await call(alice.cookies, 'POST', '/api/auth/register',
    { username: alice.name, email: `${alice.name}@ex.com`, displayName: 'A', password: 'correct-horse-battery' })).status, 200);
  alice.token = (await call(alice.cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true })).body.token;
  assert.equal((await call(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Enk Org' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/repos`, { slug: repo, name: 'Svc', visibility: 'internal', defaultBranch: 'main' })).status, 201);

  dir = join(tmp, 'w');
  await git(tmp, 'clone', `http://${alice.name}:${alice.token}@${HOST}/${org}/${repo}.git`, dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await writeFile(join(dir, 'a.txt'), 'x\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'base');
  await git(dir, 'push', 'origin', 'HEAD:main');
});

after(() => mock?.close());

test('connect an Enklr workspace', async () => {
  const r = await call(alice.cookies, 'PUT', `/api/orgs/${org}/enklr`, {
    baseUrl: mockUrl, workspace: 'board-7', apiToken: 'enklr-secret-token', inboundSecret: 's3cr3t', cardPrefix: 'ENK',
  });
  assert.equal(r.status, 200);
  const g = await call(alice.cookies, 'GET', `/api/orgs/${org}/enklr`);
  assert.equal(g.body.connected, true);
  assert.equal(g.body.workspace, 'board-7');
});

test('a PR mentioning a card links it and pushes a ref + event to Enklr', async () => {
  received.length = 0;
  await git(dir, 'checkout', '-b', 'feature');
  await writeFile(join(dir, 'b.txt'), 'b\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'work on ENK-42');
  await git(dir, 'push', 'origin', 'feature');

  const pr = await call(alice.cookies, 'POST', `${R()}/pulls`, {
    title: 'Implement widget', body: 'Fixes ENK-42 and touches ENK-9.', baseBranch: 'main', headBranch: 'feature',
  });
  assert.equal(pr.status, 201);
  await new Promise((r) => setTimeout(r, 400));

  const refCalls = received.filter((c) => c.url === '/api/gitsrv/refs');
  assert.ok(refCalls.length >= 2, 'expected ref calls for ENK-42 and ENK-9');
  assert.ok(refCalls.every((c) => c.auth === 'Bearer enklr-secret-token'));
  const cards = refCalls.map((c) => c.body.cardRef).sort();
  assert.deepEqual(cards.slice(0, 2), ['ENK-42', 'ENK-9']);
  assert.equal(refCalls[0].body.state, 'open');

  // the card view lists the PR
  const card = await call(alice.cookies, 'GET', `/api/orgs/${org}/enklr/cards/ENK-42`);
  assert.equal(card.status, 200);
  assert.ok(card.body.some((l) => l.sourceKind === 'pull' && l.sourceRef === '#' + pr.body.number && l.state === 'open'));

  // merge → pr_merged event
  received.length = 0;
  const m = await call(alice.cookies, 'POST', `${R()}/pulls/${pr.body.number}/merge`, { method: 'merge' });
  assert.equal(m.status, 204, JSON.stringify(m.body));
  await new Promise((r) => setTimeout(r, 400));

  const events = received.filter((c) => c.url === '/api/gitsrv/events');
  assert.ok(events.some((e) => e.body.type === 'pr_merged' && e.body.cardRef === 'ENK-42'));

  const card2 = await call(alice.cookies, 'GET', `/api/orgs/${org}/enklr/cards/ENK-42`);
  assert.ok(card2.body.some((l) => l.sourceKind === 'pull' && l.state === 'merged'));
});

test('inbound Enklr webhook is HMAC-verified', async () => {
  const conn = (await call(alice.cookies, 'GET', `/api/orgs/${org}/enklr`)).body;
  // need the connection id — expose via a fresh PUT response
  const put = await call(alice.cookies, 'PUT', `/api/orgs/${org}/enklr`, {
    baseUrl: mockUrl, workspace: 'board-7', apiToken: 'enklr-secret-token', inboundSecret: 's3cr3t', cardPrefix: 'ENK',
  });
  const id = put.body.id;

  const payload = JSON.stringify({ type: 'card_moved', cardRef: 'ENK-42', column: 'In Progress' });
  const { createHmac } = await import('node:crypto');
  const sig = 'sha256=' + createHmac('sha256', 's3cr3t').update(payload).digest('hex');

  const good = await fetch(`${BASE}/api/integrations/enklr/${id}/events`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Enklr-Signature-256': sig }, body: payload,
  });
  assert.equal(good.status, 202);

  const bad = await fetch(`${BASE}/api/integrations/enklr/${id}/events`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Enklr-Signature-256': 'sha256=deadbeef' }, body: payload,
  });
  assert.equal(bad.status, 401);
});
