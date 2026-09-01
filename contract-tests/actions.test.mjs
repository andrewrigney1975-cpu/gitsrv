import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const run = promisify(execFile);
const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';
const HOST = new URL(BASE).host;
const S = Date.now().toString(36);
const NO_CRED = ['-c', 'credential.helper=', '-c', 'credential.interactive=false'];
// Actions need the runner + docker socket; skip unless explicitly enabled.
const ENABLED = process.env.GITSRV_ACTIONS === '1';

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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const alice = { name: `act_alice_${S}`, cookies: jar() };
const org = `act-org-${S}`;
const repo = 'ci';
let tmp, dir;
const R = () => `/api/orgs/${org}/repos/${repo}`;

before(async (t) => {
  if (!ENABLED) return;
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-act-'));
  assert.equal((await call(alice.cookies, 'POST', '/api/auth/register',
    { username: alice.name, email: `${alice.name}@ex.com`, displayName: 'A', password: 'correct-horse-battery' })).status, 200);
  alice.token = (await call(alice.cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true })).body.token;
  assert.equal((await call(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Act Org' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/repos`, { slug: repo, name: 'CI', visibility: 'internal', defaultBranch: 'main' })).status, 201);
  assert.equal((await call(alice.cookies, 'PUT', `${R()}/secrets`, { name: 'GREETING', value: 'hello-from-secret' })).status, 204);

  dir = join(tmp, 'w');
  await git(tmp, 'clone', `http://${alice.name}:${alice.token}@${HOST}/${org}/${repo}.git`, dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await mkdir(join(dir, '.gitsrv/workflows'), { recursive: true });
  await writeFile(join(dir, '.gitsrv/workflows/ci.yml'), `name: CI
on:
  push:
    branches: [main]
  pull_request:
jobs:
  build:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        target: [alpha, beta]
    steps:
      - uses: actions/checkout@v4
      - name: Show
        run: |
          echo "target is \${{ matrix.target }}"
          echo "secret is \${{ secrets.GREETING }}"
          test -f .gitsrv/workflows/ci.yml
`);
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'add ci');
  await git(dir, 'push', 'origin', 'HEAD:main');
});

async function waitForRun(number, timeoutMs = 120000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const d = await call(alice.cookies, 'GET', `${R()}/actions/${number}`);
    if (d.status === 200 && d.body.run.status === 'completed') return d.body;
    await sleep(3000);
  }
  throw new Error(`run #${number} did not complete in time`);
}

test('a push runs a matrix workflow and posts commit statuses', { skip: !ENABLED && 'set GITSRV_ACTIONS=1' }, async () => {
  // give dispatch a moment
  await sleep(2000);
  const runs = await call(alice.cookies, 'GET', `${R()}/actions`);
  assert.ok(runs.body.length >= 1, 'expected a queued run');
  const runNumber = runs.body[0].number;

  const detail = await waitForRun(runNumber);
  assert.equal(detail.run.conclusion, 'success');
  assert.equal(detail.jobs.length, 2, 'matrix should expand to two jobs');
  assert.ok(detail.jobs.every((j) => j.conclusion === 'success'));

  const sha = detail.run.headSha;
  const statuses = await call(alice.cookies, 'GET', `${R()}/statuses/${sha}`);
  assert.equal(statuses.body.length, 2);
  assert.ok(statuses.body.every((s) => s.state === 'success'));

  // logs contain the secret-masked and matrix-substituted output
  const logs = await call(alice.cookies, 'GET', `${R()}/actions/${runNumber}/jobs/${detail.jobs[0].id}/logs`);
  const text = logs.body.map((l) => l.line).join('\n');
  assert.match(text, /target is (alpha|beta)/);
  assert.match(text, /secret is \*\*\*/);          // masked
  assert.doesNotMatch(text, /hello-from-secret/);
});

test('a required status check gates the PR merge button until green', { skip: !ENABLED && 'set GITSRV_ACTIONS=1' }, async () => {
  assert.equal((await call(alice.cookies, 'POST', `${R()}/protections`, {
    pattern: 'main', requirePullRequest: true, requiredApprovals: 0, requireStatusChecks: true,
    blockForcePush: true, blockDeletion: true, requireLinearHistory: false, restrictPush: false,
  })).status, 201);

  await git(dir, 'checkout', '-b', 'feature');
  await writeFile(join(dir, 'x.txt'), 'x\n');
  await git(dir, 'add', '-A'); await git(dir, 'commit', '-m', 'feature work');
  await git(dir, 'push', 'origin', 'feature');

  const pr = await call(alice.cookies, 'POST', `${R()}/pulls`, { title: 'Gated by CI', baseBranch: 'main', headBranch: 'feature' });
  const num = pr.body.number;

  // merge should be blocked while checks are pending/absent
  let m = await call(alice.cookies, 'POST', `${R()}/pulls/${num}/merge`, { method: 'merge' });
  assert.equal(m.status, 422);
  assert.match(m.body.error, /status check/i);

  // wait for the pull_request run to finish and land green statuses
  const runs = await call(alice.cookies, 'GET', `${R()}/actions`);
  const prRun = runs.body.find((r) => r.event === 'pull_request');
  assert.ok(prRun, 'expected a pull_request run');
  await waitForRun(prRun.number);

  m = await call(alice.cookies, 'POST', `${R()}/pulls/${num}/merge`, { method: 'merge' });
  assert.equal(m.status, 204, JSON.stringify(m.body));
});
