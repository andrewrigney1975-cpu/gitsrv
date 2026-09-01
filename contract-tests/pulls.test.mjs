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
const git = (cwd, ...args) => run('git', [...NO_CRED, ...args], { cwd, env: { ...process.env, GIT_TERMINAL_PROMPT: '0' }, timeout: 30000 });

const alice = { name: `pr_alice_${S}`, cookies: jar() };
const bob = { name: `pr_bob_${S}`, cookies: jar() };
const org = `pr-org-${S}`;
const repo = 'svc';
let tmp, dir;
const R = () => `/api/orgs/${org}/repos/${repo}/pulls`;

before(async () => {
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-pr-'));
  for (const u of [alice, bob]) {
    assert.equal((await call(u.cookies, 'POST', '/api/auth/register',
      { username: u.name, email: `${u.name}@ex.com`, displayName: u.name, password: 'correct-horse-battery' })).status, 200);
    u.token = (await call(u.cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true })).body.token;
  }
  assert.equal((await call(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'PR Org' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/members`, { username: bob.name, role: 'member' })).status, 204);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/repos`, { slug: repo, name: 'Svc', visibility: 'internal', defaultBranch: 'main' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/repos/${repo}/collaborators`, { username: bob.name, permission: 'write' })).status, 204);

  dir = join(tmp, 'w');
  await git(tmp, 'clone', `http://${alice.name}:${alice.token}@${HOST}/${org}/${repo}.git`, dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await writeFile(join(dir, 'app.txt'), 'line one\nline two\nline three\n');
  await git(dir, 'add', '-A');
  await git(dir, 'commit', '-m', 'base');
  await git(dir, 'push', 'origin', 'HEAD:main');

  await git(dir, 'checkout', '-b', 'feature');
  await writeFile(join(dir, 'app.txt'), 'line one\nline two changed\nline three\nline four\n');
  await git(dir, 'commit', '-am', 'tweak app.txt');
  await git(dir, 'push', 'origin', 'feature');
});

test('open a PR and read its compare + commits', async () => {
  const created = await call(alice.cookies, 'POST', R(), { title: 'Tweak app', body: 'small change', baseBranch: 'main', headBranch: 'feature' });
  assert.equal(created.status, 201);
  assert.equal(created.body.number, 1);

  const list = await call(bob.cookies, 'GET', `${R()}/?state=open`);
  assert.equal(list.status, 200);
  assert.equal(list.body.length, 1);
  assert.equal(list.body[0].title, 'Tweak app');
  assert.equal(list.body[0].headBranch, 'feature');

  const d = await call(bob.cookies, 'GET', `${R()}/1`);
  assert.equal(d.status, 200);
  assert.equal(d.body.detail.state, 'open');
  assert.equal(d.body.detail.compare.ahead, 1);
  assert.equal(d.body.detail.compare.mergeable, true);
  assert.ok(d.body.detail.compare.files.some((f) => f.path === 'app.txt'));
});

test('inline review: comment, submit review, resolve thread', async () => {
  // Bob leaves a pending inline comment, then submits a review that publishes it.
  const c = await call(bob.cookies, 'POST', `${R()}/1/comments`,
    { body: 'why change this line?', filePath: 'app.txt', line: 2, side: 'new', pending: true });
  assert.equal(c.status, 201);

  // Alice can't see the pending comment yet.
  let d = await call(alice.cookies, 'GET', `${R()}/1`);
  assert.equal(d.body.detail.threads.length, 0);

  const rv = await call(bob.cookies, 'POST', `${R()}/1/reviews`, { state: 'request_changes', body: 'one question' });
  assert.equal(rv.status, 204);

  d = await call(alice.cookies, 'GET', `${R()}/1`);
  assert.equal(d.body.detail.threads.length, 1);
  assert.equal(d.body.detail.threads[0].comments[0].body, 'why change this line?');
  assert.equal(d.body.detail.merge.blockedByReview, true);
  assert.equal(d.body.detail.merge.mergeable, false);

  // Alice replies and resolves the thread.
  const threadId = d.body.detail.threads[0].id;
  assert.equal((await call(alice.cookies, 'POST', `${R()}/1/comments`, { body: 'clarity', threadId })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `${R()}/1/threads/${threadId}/resolve`)).status, 204);

  d = await call(alice.cookies, 'GET', `${R()}/1`);
  assert.equal(d.body.detail.threads[0].isResolved, true);
});

test('merge is blocked until the change request is cleared, then squash-merges', async () => {
  // Still blocked (request_changes stands).
  let m = await call(alice.cookies, 'POST', `${R()}/1/merge`, { method: 'squash' });
  assert.equal(m.status, 422);

  // Bob approves.
  assert.equal((await call(bob.cookies, 'POST', `${R()}/1/reviews`, { state: 'approve', body: 'ok now' })).status, 204);

  const d = await call(alice.cookies, 'GET', `${R()}/1`);
  assert.equal(d.body.detail.merge.mergeable, true);
  assert.equal(d.body.detail.merge.approvals, 1);

  m = await call(alice.cookies, 'POST', `${R()}/1/merge`, { method: 'squash' });
  assert.equal(m.status, 204, JSON.stringify(m.body));

  const after = await call(alice.cookies, 'GET', `${R()}/1`);
  assert.equal(after.body.detail.state, 'merged');
  assert.equal(after.body.detail.mergeMethod, 'squash');

  // head branch auto-deleted, base contains the squash commit
  const refs = await call(alice.cookies, 'GET', `/api/orgs/${org}/repos/${repo}/browse/refs`);
  assert.ok(!refs.body.branches.some((b) => b.name === 'feature'));
  const log = await call(alice.cookies, 'GET', `/api/orgs/${org}/repos/${repo}/browse/commits/main`);
  assert.ok(log.body.commits.some((c) => c.summary === 'Tweak app (#1)'));
});

test('a conflicting PR reports as not mergeable', async () => {
  await git(dir, 'checkout', 'main');
  await git(dir, 'pull', 'origin', 'main');
  await git(dir, 'checkout', '-b', 'conflictA');
  await writeFile(join(dir, 'app.txt'), 'A one\nline two\nline three\n');
  await git(dir, 'commit', '-am', 'A edits line one');
  await git(dir, 'push', 'origin', 'conflictA');
  await git(dir, 'checkout', 'main');
  await writeFile(join(dir, 'app.txt'), 'B one\nline two\nline three\n');
  await git(dir, 'commit', '-am', 'B edits line one on main');
  await git(dir, 'push', 'origin', 'main');

  const c = await call(alice.cookies, 'POST', R(), { title: 'Conflicting', baseBranch: 'main', headBranch: 'conflictA' });
  assert.equal(c.status, 201);
  const d = await call(alice.cookies, 'GET', `${R()}/${c.body.number}`);
  assert.equal(d.body.detail.merge.hasConflicts, true);
  assert.ok(d.body.detail.merge.conflictPaths.includes('app.txt'));
});

test('pushing the base branch past the head auto-merges the PR', async () => {
  await git(dir, 'checkout', 'main');
  await git(dir, 'pull', 'origin', 'main');
  await git(dir, 'checkout', '-b', 'ff');
  await writeFile(join(dir, 'ff.txt'), 'ff\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'ff work');
  await git(dir, 'push', 'origin', 'ff');
  const c = await call(alice.cookies, 'POST', R(), { title: 'FF', baseBranch: 'main', headBranch: 'ff' });

  // merge ff into main directly via git, then push
  await git(dir, 'checkout', 'main');
  await git(dir, 'merge', '--no-ff', 'ff', '-m', 'external merge');
  await git(dir, 'push', 'origin', 'main');

  const d = await call(alice.cookies, 'GET', `${R()}/${c.body.number}`);
  assert.equal(d.body.detail.state, 'merged');
});
