import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Opt-in: GITSRV_LOAD=1. A light concurrency probe, not a full soak test — it verifies the
// stack stays responsive under a burst of concurrent clones + browse reads.
const ENABLED = process.env.GITSRV_LOAD === '1';
const run = promisify(execFile);
const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';
const HOST = new URL(BASE).host;
const S = Date.now().toString(36);
const NO_CRED = ['-c', 'credential.helper=', '-c', 'credential.interactive=false'];
const CONC = Number(process.env.GITSRV_LOAD_CONC || 20);

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
const git = (cwd, ...a) => run('git', [...NO_CRED, ...a], { cwd, env: { ...process.env, GIT_TERMINAL_PROMPT: '0' }, timeout: 60000 });
const pct = (arr, p) => arr.slice().sort((a, b) => a - b)[Math.min(arr.length - 1, Math.floor(arr.length * p))];

const alice = { name: `load_alice_${S}`, cookies: jar() };
const org = `load-org-${S}`;
const repo = 'svc';
let tmp, cloneUrl;

before(async () => {
  if (!ENABLED) return;
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-load-'));
  assert.equal((await call(alice.cookies, 'POST', '/api/auth/register',
    { username: alice.name, email: `${alice.name}@ex.com`, displayName: 'A', password: 'correct-horse-battery' })).status, 200);
  alice.token = (await call(alice.cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true })).body.token;
  assert.equal((await call(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Load Org' })).status, 201);
  assert.equal((await call(alice.cookies, 'POST', `/api/orgs/${org}/repos`, { slug: repo, name: 'Svc', visibility: 'public', defaultBranch: 'main' })).status, 201);

  cloneUrl = `http://${alice.name}:${alice.token}@${HOST}/${org}/${repo}.git`;
  const dir = join(tmp, 'seed');
  await git(tmp, 'clone', cloneUrl, dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'A');
  for (let i = 0; i < 40; i++) {
    await writeFile(join(dir, `f${i}.txt`), `content ${i}\n`.repeat(20));
    await git(dir, 'add', '-A'); await git(dir, 'commit', '-q', '-m', `commit ${i}`);
  }
  await git(dir, 'push', 'origin', 'HEAD:main');
});

test(`${CONC} concurrent clones stay responsive`, { skip: !ENABLED && 'set GITSRV_LOAD=1' }, async () => {
  const times = await Promise.all(Array.from({ length: CONC }, async (_, i) => {
    const t0 = performance.now();
    await git(tmp, 'clone', '--quiet', cloneUrl, join(tmp, `c${i}`));
    return performance.now() - t0;
  }));
  const p95 = pct(times, 0.95);
  console.log(`clone p50=${pct(times, 0.5) | 0}ms p95=${p95 | 0}ms`);
  assert.ok(p95 < 15000, `clone p95 ${p95 | 0}ms exceeds 15s budget`);
});

test(`${CONC * 5} concurrent browse reads stay fast`, { skip: !ENABLED && 'set GITSRV_LOAD=1' }, async () => {
  const paths = [
    `/api/orgs/${org}/repos/${repo}/browse/overview`,
    `/api/orgs/${org}/repos/${repo}/browse/refs`,
    `/api/orgs/${org}/repos/${repo}/browse/commits/main`,
    `/api/orgs/${org}/repos/${repo}/browse/tree/main`,
    `/api/orgs/${org}/repos/${repo}/browse/graph`,
  ];
  const times = await Promise.all(Array.from({ length: CONC * 5 }, async (_, i) => {
    const t0 = performance.now();
    const res = await fetch(BASE + paths[i % paths.length]);
    assert.equal(res.status, 200);
    await res.text();
    return performance.now() - t0;
  }));
  const p95 = pct(times, 0.95);
  console.log(`browse p50=${pct(times, 0.5) | 0}ms p95=${p95 | 0}ms`);
  assert.ok(p95 < 2000, `browse p95 ${p95 | 0}ms exceeds 2s budget`);
});
