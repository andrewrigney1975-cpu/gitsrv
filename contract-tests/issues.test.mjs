import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const run = promisify(execFile);
const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';
const HOST = new URL(BASE).host;
const MAIL = process.env.GITSRV_MAIL_URL; // e.g. http://localhost:8025
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

const alice = { name: `iss_alice_${S}`, cookies: jar() };
const bob = { name: `iss_bob_${S}`, cookies: jar() };
const org = `iss-org-${S}`;
const repo = 'proj';
let tmp, dir;
const R = () => `/api/orgs/${org}/repos/${repo}`;

before(async () => {
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-iss-'));
  for (const u of [alice, bob]) {
    assert.equal((await call(u.cookies, 'POST', '/api/auth/register',
      { username: u.name, email: `${u.name}@ex.com`, displayName: u.name, password: 'correct-horse-battery' })).status, 200);
    u.token = (await call(u.cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true })).body.token;
  }
  assert.equal((await call(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Iss Org' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/members`, { username: bob.name, role: 'member' })).status, 204);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/repos`, { slug: repo, name: 'Proj', visibility: 'internal', defaultBranch: 'main' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `${R()}/collaborators`, { username: bob.name, permission: 'write' })).status, 204);

  dir = join(tmp, 'w');
  await git(tmp, 'clone', `http://${alice.name}:${alice.token}@${HOST}/${org}/${repo}.git`, dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await writeFile(join(dir, 'f.txt'), 'x\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'base');
  await git(dir, 'push', 'origin', 'HEAD:main');
});

test('file an issue with a mention; the mentioned user gets an inbox notification', async () => {
  const created = await call(alice.cookies, 'POST', `${R()}/issues`,
    { title: 'Something is broken', body: `Hey @${bob.name} can you look at this?` });
  assert.equal(created.status, 201);
  assert.ok(created.body.number >= 1);

  const inbox = await call(bob.cookies, 'GET', '/api/notifications/');
  assert.equal(inbox.status, 200);
  const n = inbox.body.find((x) => x.subjectKind === 'issue' && x.reason === 'mention');
  assert.ok(n, 'expected a mention notification for bob');
  assert.equal(n.title, 'Something is broken');
  assert.equal(n.isRead, false);

  const count = await call(bob.cookies, 'GET', '/api/notifications/count');
  assert.ok(count.body.unread >= 1);
});

test('issue body renders markdown and links #refs and @mentions', async () => {
  const list = await call(alice.cookies, 'GET', `${R()}/issues`);
  const num = list.body[0].number;
  const d = await call(bob.cookies, 'GET', `${R()}/issues/${num}`);
  assert.match(d.body.detail.bodyHtml, new RegExp(`<a href="#/u/${bob.name}">@${bob.name}</a>`));
});

test('assigning an issue notifies the assignee and adds a timeline event', async () => {
  const list = await call(alice.cookies, 'GET', `${R()}/issues`);
  const num = list.body[0].number;
  assert.equal((await call(alice.cookies, 'PUT', `${R()}/issues/${num}/assignees`, { usernames: [bob.name] })).status, 204);

  const d = await call(alice.cookies, 'GET', `${R()}/issues/${num}`);
  assert.deepEqual(d.body.detail.assignees, [bob.name]);
  assert.ok(d.body.detail.events.some((e) => e.kind === 'assigned'));

  const inbox = await call(bob.cookies, 'GET', '/api/notifications/?filter=unread');
  assert.ok(inbox.body.some((x) => x.reason === 'assign'));
});

test('labels: create a repo label and apply it', async () => {
  const l = await call(alice.cookies, 'POST', `${R()}/labels`, { name: 'bug', color: '#b23b2e', description: 'defect' });
  assert.equal(l.status, 201);
  const list = await call(alice.cookies, 'GET', `${R()}/issues`);
  const num = list.body[0].number;
  assert.equal((await call(alice.cookies, 'PUT', `${R()}/issues/${num}/labels`, { labelIds: [l.body.id] })).status, 204);
  const d = await call(alice.cookies, 'GET', `${R()}/issues/${num}`);
  assert.equal(d.body.detail.labels[0].name, 'bug');
});

test('a merged PR with "closes #N" closes the issue and records the reference', async () => {
  // open a fresh issue to close
  const iss = await call(alice.cookies, 'POST', `${R()}/issues`, { title: 'Close me via PR' });
  const issNum = iss.body.number;

  await git(dir, 'checkout', '-b', 'fix');
  await writeFile(join(dir, 'f.txt'), 'fixed\n');
  await git(dir, 'commit', '-am', `resolve the thing\n\nCloses #${issNum}`);
  await git(dir, 'push', 'origin', 'fix');

  const pr = await call(alice.cookies, 'POST', `${R()}/pulls`, { title: 'The fix', body: `Closes #${issNum}`, baseBranch: 'main', headBranch: 'fix' });
  assert.equal(pr.status, 201);
  const m = await call(alice.cookies, 'POST', `${R()}/pulls/${pr.body.number}/merge`, { method: 'merge' });
  assert.equal(m.status, 204, JSON.stringify(m.body));

  const d = await call(alice.cookies, 'GET', `${R()}/issues/${issNum}`);
  assert.equal(d.body.detail.state, 'closed');
  assert.ok(d.body.detail.references.some((r) => r.sourceKind === 'pr' && r.closes));
  assert.ok(d.body.detail.events.some((e) => e.kind === 'closed'));
});

test('activity feed records repo events', async () => {
  const feed = await call(alice.cookies, 'GET', `${R()}/activity`);
  assert.equal(feed.status, 200);
  assert.ok(feed.body.some((a) => a.kind === 'issue_opened'));
  assert.ok(feed.body.some((a) => a.kind === 'pr_merged'));
});

test('email worker delivers a digest (mailpit)', { skip: !MAIL && 'GITSRV_MAIL_URL not set' }, async () => {
  // trigger a fresh mention
  await call(alice.cookies, 'POST', `${R()}/issues`, { title: 'ping', body: `@${bob.name} check email` });
  // worker polls every ~15s with a 20s delay gate
  let found = false;
  for (let i = 0; i < 20 && !found; i++) {
    await new Promise((r) => setTimeout(r, 3000));
    const res = await fetch(`${MAIL}/api/v1/search?query=${encodeURIComponent(bob.name + '@ex.com')}`);
    const data = await res.json();
    found = (data.messages || []).some((m) => /GitSrv/.test(m.Subject));
  }
  assert.ok(found, 'expected a GitSrv email for bob in mailpit');
});
