import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const run = promisify(execFile);
const NPM = process.platform === 'win32' ? 'npm.cmd' : 'npm';
const npm = (args, opts) => run(NPM, args, { ...opts, shell: process.platform === 'win32' });
const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';
const HOST = new URL(BASE).host;
const S = Date.now().toString(36);
const WITH_DOCKER = process.env.GITSRV_DOCKER === '1';

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

const alice = { name: `pkg_alice_${S}`, cookies: jar() };
const org = `pkg-org-${S}`;
let tmp;

before(async () => {
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-pkg-'));
  assert.equal((await call(alice.cookies, 'POST', '/api/auth/register',
    { username: alice.name, email: `${alice.name}@ex.com`, displayName: 'A', password: 'correct-horse-battery' })).status, 200);
  alice.token = (await call(alice.cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true })).body.token;
  assert.equal((await call(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Pkg Org' })).status, 201);
});

test('npm publish then install back', async () => {
  const pkgName = `demo-pkg-${S}`;
  const dir = join(tmp, 'lib');
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, 'package.json'), JSON.stringify({ name: pkgName, version: '1.0.0', main: 'index.js' }, null, 2));
  await writeFile(join(dir, 'index.js'), 'module.exports = 42;\n');

  const registry = `${BASE}/npm/${org}/`;
  const npmrc = join(dir, '.npmrc');
  const hostNoProto = registry.replace(/^https?:/, '');
  await writeFile(npmrc, `registry=${registry}\n${hostNoProto}:_authToken=${alice.token}\n`);

  const npmEnv = { ...process.env, npm_config_userconfig: npmrc, npm_config_cache: join(tmp, 'npm-cache') };
  await npm(['publish', '--registry', registry], { cwd: dir, env: npmEnv, timeout: 60000 });

  // install into a fresh project
  const consumer = join(tmp, 'app');
  await mkdir(consumer, { recursive: true });
  await writeFile(join(consumer, 'package.json'), JSON.stringify({ name: 'consumer', version: '1.0.0' }));
  await writeFile(join(consumer, '.npmrc'), `registry=${registry}\n${hostNoProto}:_authToken=${alice.token}\n`);
  await npm(['install', pkgName, '--no-audit', '--no-fund'],
    { cwd: consumer, env: { ...process.env, npm_config_cache: join(tmp, 'npm-cache2') }, timeout: 60000 });

  const out = await run('node', ['-e', `console.log(require('${pkgName}'))`], { cwd: consumer, timeout: 15000 });
  assert.equal(out.stdout.trim(), '42');

  // it shows up in the API package list
  const list = await call(alice.cookies, 'GET', `/api/orgs/${org}/packages`);
  assert.ok(list.body.packages.some((p) => p.kind === 'npm' && p.name === pkgName && p.versions === 1));
});

test('generic upload and download round-trips', async () => {
  const put = await fetch(`${BASE}/generic/${org}/tools/2.0.0/build.tar.gz`, {
    method: 'PUT',
    headers: { Authorization: `Bearer ${alice.token}`, 'Content-Type': 'application/gzip' },
    body: 'fake-tarball-bytes',
  });
  assert.equal(put.status, 201);

  const get = await fetch(`${BASE}/generic/${org}/tools/2.0.0/build.tar.gz`, { headers: { Authorization: `Bearer ${alice.token}` } });
  assert.equal(get.status, 200);
  assert.equal(await get.text(), 'fake-tarball-bytes');
});

test('docker push then pull', { skip: !WITH_DOCKER && 'set GITSRV_DOCKER=1' }, async () => {
  const image = `${HOST}/${org}/hello:v1`;
  // build a tiny image
  const ctxDir = join(tmp, 'img');
  await mkdir(ctxDir, { recursive: true });
  await writeFile(join(ctxDir, 'Dockerfile'), 'FROM busybox:latest\nRUN echo gitsrv > /msg\n');
  await run('docker', ['build', '-t', image, ctxDir], { timeout: 120000 });

  await run('docker', ['login', HOST, '-u', alice.name, '-p', alice.token], { timeout: 30000 });
  await run('docker', ['push', image], { timeout: 120000 });

  await run('docker', ['rmi', '-f', image], { timeout: 30000 }).catch(() => {});
  await run('docker', ['pull', image], { timeout: 120000 });
  const out = await run('docker', ['run', '--rm', image, 'cat', '/msg'], { timeout: 30000 });
  assert.equal(out.stdout.trim(), 'gitsrv');

  const tags = await fetch(`${BASE}/v2/${org}/hello/tags/list`, { headers: { Authorization: `Basic ${Buffer.from(`${alice.name}:${alice.token}`).toString('base64')}` } });
  const data = await tags.json();
  assert.ok(data.tags.includes('v1'));
});
