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
const git = (cwd, ...a) => run('git', [...NO_CRED, ...a], { cwd, env: { ...process.env, GIT_TERMINAL_PROMPT: '0' }, timeout: 30000 });

const alice = { name: `bp_alice_${S}`, cookies: jar() };
const org = `bp-org-${S}`;
const repo = 'svc';
let tmp, dir, auth;
const R = () => `/api/orgs/${org}/repos/${repo}`;

before(async () => {
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-bp-'));
  assert.equal((await call(alice.cookies, 'POST', '/api/auth/register',
    { username: alice.name, email: `${alice.name}@ex.com`, displayName: 'A', password: 'correct-horse-battery' })).status, 200);
  alice.token = (await call(alice.cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true })).body.token;
  assert.equal((await call(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'BP Org' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/repos`, { slug: repo, name: 'Svc', visibility: 'internal', defaultBranch: 'main' })).status, 201);

  auth = `http://${alice.name}:${alice.token}@${HOST}/${org}/${repo}.git`;
  dir = join(tmp, 'w');
  await git(tmp, 'clone', auth, dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await writeFile(join(dir, 'a.txt'), 'one\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'base');
  await git(dir, 'push', 'origin', 'HEAD:main');
});

test('a protected branch rejects a direct push', async () => {
  assert.equal((await call(alice.cookies, 'POST', `${R()}/protections`, {
    pattern: 'main', requirePullRequest: true, requiredApprovals: 0, requireStatusChecks: false,
    blockForcePush: true, blockDeletion: true, requireLinearHistory: false, restrictPush: false,
  })).status, 201);

  await writeFile(join(dir, 'a.txt'), 'two\n');
  await git(dir, 'commit', '-am', 'direct change');
  await assert.rejects(
    git(dir, 'push', 'origin', 'HEAD:main'),
    /protected|pull request|rejected/i);
});

test('a protected branch cannot be deleted over the wire', async () => {
  await git(dir, 'push', 'origin', 'HEAD:refs/heads/tmp'); // create a branch to prove deletes of unprotected ones work
  await git(dir, 'push', 'origin', '--delete', 'tmp');
  await assert.rejects(git(dir, 'push', 'origin', '--delete', 'main'), /protected|cannot be deleted|rejected/i);
});

test('web cherry-pick lands a commit on another branch', async () => {
  // reset local main to origin, make a feature commit on a branch
  await git(dir, 'fetch', 'origin');
  await git(dir, 'reset', '--hard', 'origin/main');
  await git(dir, 'checkout', '-b', 'feature');
  await writeFile(join(dir, 'b.txt'), 'feature\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'add b.txt');
  await git(dir, 'push', 'origin', 'feature');
  const featureSha = (await git(dir, 'rev-parse', 'HEAD')).stdout.trim();

  // create an unprotected target branch, cherry-pick onto it via the API
  assert.equal((await call(alice.cookies, 'POST', `${R()}/branches`, { name: 'hotfix', from: 'main' })).status, 201);
  const cp = await call(alice.cookies, 'POST', `${R()}/cherry-pick`, { sha: featureSha, branch: 'hotfix' });
  assert.equal(cp.status, 200, JSON.stringify(cp.body));

  const log = await call(alice.cookies, 'GET', `${R()}/browse/commits/hotfix`);
  assert.ok(log.body.commits.some((c) => c.summary === 'add b.txt'));
});

test('creating a tagged release adds an annotated tag', async () => {
  const rel = await call(alice.cookies, 'POST', `${R()}/releases`, {
    tagName: 'v1.0.0', target: 'main', name: 'First release', body: 'Initial cut.', isPrerelease: false, isDraft: false,
  });
  assert.equal(rel.status, 201, JSON.stringify(rel.body));

  const list = await call(alice.cookies, 'GET', `${R()}/releases`);
  assert.ok(list.body.some((r) => r.tagName === 'v1.0.0' && r.name === 'First release'));

  const refs = await call(alice.cookies, 'GET', `${R()}/browse/refs`);
  assert.ok(refs.body.tags.some((t) => t.name === 'v1.0.0'));

  // the tag is fetchable over git
  const { stdout } = await git(tmp, 'ls-remote', '--tags', auth);
  assert.match(stdout, /refs\/tags\/v1\.0\.0/);
});

test('release asset upload and download round-trips', async () => {
  const form = new FormData();
  form.append('file', new Blob(['hello asset\n'], { type: 'text/plain' }), 'notes.txt');
  const up = await fetch(`${BASE}${R()}/releases/v1.0.0/assets`, {
    method: 'POST', headers: { Cookie: alice.cookies.header(), 'X-GitSrv-CSRF': '1' }, body: form,
  });
  assert.equal(up.status, 201);
  const asset = await up.json();

  const dl = await fetch(`${BASE}${R()}/releases/v1.0.0/assets/${asset.id}`, { headers: { Cookie: alice.cookies.header() } });
  assert.equal(dl.status, 200);
  assert.equal(await dl.text(), 'hello asset\n');
});

test('required approvals gate the PR merge button', async () => {
  await call(alice.cookies, 'PUT', `${R()}/protections/${(await call(alice.cookies, 'GET', `${R()}/protections`)).body[0].id}`, {
    pattern: 'main', requirePullRequest: true, requiredApprovals: 1, requireStatusChecks: false,
    blockForcePush: true, blockDeletion: true, requireLinearHistory: false, restrictPush: false,
  });

  await git(dir, 'checkout', '-b', 'needs-approval');
  await writeFile(join(dir, 'c.txt'), 'c\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'needs approval');
  await git(dir, 'push', 'origin', 'needs-approval');

  const pr = await call(alice.cookies, 'POST', `${R()}/pulls`, { title: 'Gated', baseBranch: 'main', headBranch: 'needs-approval' });
  const m = await call(alice.cookies, 'POST', `${R()}/pulls/${pr.body.number}/merge`, { method: 'merge' });
  assert.equal(m.status, 422);
  assert.match(m.body.error, /approval/i);
});
