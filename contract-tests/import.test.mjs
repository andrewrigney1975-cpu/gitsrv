import { test, before, after } from 'node:test';
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

// The API container reaches the running instance back through host.docker.internal, which UrlGuard
// explicitly allows. Override with GITSRV_IMPORT_SOURCE_HOST when the test host differs.
const SRC_HOST = process.env.GITSRV_IMPORT_SOURCE_HOST ?? 'host.docker.internal:8080';

function jar() {
  const c = new Map();
  return {
    header: () => [...c].map(([k, v]) => `${k}=${v}`).join('; '),
    absorb: (res) => {
      for (const line of res.headers.getSetCookie?.() ?? []) {
        const [pair] = line.split(';');
        const i = pair.indexOf('=');
        c.set(pair.slice(0, i), pair.slice(i + 1));
      }
    },
  };
}

async function apiCall(cookies, method, path, body) {
  const headers = { Accept: 'application/json', Cookie: cookies.header() };
  if (method !== 'GET') headers['X-GitSrv-CSRF'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(BASE + path, { method, headers, body: body && JSON.stringify(body) });
  cookies.absorb(res);
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

const GIT_ENV = { ...process.env, GIT_TERMINAL_PROMPT: '0' };
const NO_CRED = ['-c', 'credential.helper=', '-c', 'credential.interactive=false'];
const git = (cwd, ...args) => run('git', [...NO_CRED, ...args], { cwd, env: GIT_ENV, timeout: 30000 });
const authUrl = (org, repo, user, token) =>
  `http://${encodeURIComponent(user)}:${encodeURIComponent(token)}@${HOST}/${org}/${repo}.git`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let tmp, org;
const alice = { name: `imp_alice_${S}`, cookies: jar() };

before(async () => {
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-imp-'));
  org = `imp-org-${S}`;

  const r = await apiCall(alice.cookies, 'POST', '/api/auth/register',
    { username: alice.name, email: `${alice.name}@ex.com`, displayName: alice.name, password: 'correct-horse-battery' });
  assert.equal(r.status, 200, JSON.stringify(r.body));
  const t = await apiCall(alice.cookies, 'POST', '/api/user/tokens', { name: 'test', scopeRead: true, scopeWrite: true });
  alice.token = t.body.token;

  assert.equal((await apiCall(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Import Org' })).status, 201);

  // A public source repo with one commit, served by this same instance.
  assert.equal((await apiCall(alice.cookies, 'POST', `/api/orgs/${org}/repos`,
    { slug: 'src', name: 'Src', visibility: 'public', defaultBranch: 'main' })).status, 201);
  const dir = join(tmp, 'src');
  await git(tmp, 'clone', authUrl(org, 'src', alice.name, alice.token), dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await writeFile(join(dir, 'README.md'), '# Imported project\n');
  await git(dir, 'add', '-A');
  await git(dir, 'commit', '-m', 'seed');
  await git(dir, 'push', 'origin', 'HEAD:main');
});

after(async () => { if (tmp) await rm(tmp, { recursive: true, force: true }); });

test('import a repo from an external clone URL', async () => {
  const src = `http://${SRC_HOST}/${org}/src.git`;
  const started = await apiCall(alice.cookies, 'POST', `/api/orgs/${org}/repos/import`,
    { slug: 'mirror', name: 'Mirror', visibility: 'private', sourceUrl: src });
  assert.equal(started.status, 202, JSON.stringify(started.body));

  // The overview reports the import state until the background worker finishes.
  let ov;
  for (let i = 0; i < 60; i++) {
    ov = (await apiCall(alice.cookies, 'GET', `/api/orgs/${org}/repos/mirror/browse/overview`)).body;
    if (ov.repo.importStatus === 'completed' || ov.repo.importStatus == null) break;
    assert.notEqual(ov.repo.importStatus, 'failed', `import failed: ${ov.repo.importError}`);
    await sleep(1000);
  }
  assert.ok(!ov.refs.isEmpty, 'imported repo should have refs');

  // And it is now a normal repo that can be cloned.
  const out = join(tmp, 'clone');
  await git(tmp, 'clone', authUrl(org, 'mirror', alice.name, alice.token), out);
  assert.match((await git(out, 'log', '--oneline')).stdout, /seed/);
});

test('a bogus source URL surfaces as a failed import', async () => {
  const started = await apiCall(alice.cookies, 'POST', `/api/orgs/${org}/repos/import`,
    { slug: 'broken', name: 'Broken', visibility: 'private', sourceUrl: `http://${SRC_HOST}/${org}/does-not-exist.git` });
  assert.equal(started.status, 202, JSON.stringify(started.body));

  let status;
  for (let i = 0; i < 60; i++) {
    const ov = (await apiCall(alice.cookies, 'GET', `/api/orgs/${org}/repos/broken/browse/overview`)).body;
    status = ov.repo.importStatus;
    if (status === 'failed') break;
    await sleep(1000);
  }
  assert.equal(status, 'failed');
});

test('an SSRF-y source URL is rejected outright', async () => {
  const r = await apiCall(alice.cookies, 'POST', `/api/orgs/${org}/repos/import`,
    { slug: 'ssrf', name: 'Ssrf', visibility: 'private', sourceUrl: 'http://169.254.169.254/latest/meta-data/' });
  assert.equal(r.status, 422, JSON.stringify(r.body));
});
