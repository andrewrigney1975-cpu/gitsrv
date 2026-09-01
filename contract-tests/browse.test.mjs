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

function jar() {
  const c = new Map();
  return {
    header: () => [...c].map(([k, v]) => `${k}=${v}`).join('; '),
    absorb: (res) => { for (const l of res.headers.getSetCookie?.() ?? []) { const [p] = l.split(';'); const i = p.indexOf('='); c.set(p.slice(0, i), p.slice(i + 1)); } },
  };
}
async function apiCall(cookies, method, path, body) {
  const headers = { Accept: 'application/json', Cookie: cookies.header() };
  if (method !== 'GET') headers['X-GitSrv-CSRF'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(BASE + path, { method, headers, body: body && JSON.stringify(body) });
  cookies.absorb(res);
  const t = await res.text();
  return { status: res.status, body: t ? JSON.parse(t) : null };
}
const git = (cwd, ...args) => run('git', [...NO_CRED, ...args], { cwd, env: { ...process.env, GIT_TERMINAL_PROMPT: '0' }, timeout: 30000 });

const cookies = jar();
const user = `br_${S}`;
const org = `br-org-${S}`;
const repo = 'app';
let tmp;

before(async () => {
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-br-'));
  assert.equal((await apiCall(cookies, 'POST', '/api/auth/register',
    { username: user, email: `${user}@ex.com`, displayName: 'Br', password: 'correct-horse-battery' })).status, 200);
  const t = await apiCall(cookies, 'POST', '/api/user/tokens', { name: 't', scopeRead: true, scopeWrite: true });
  const token = t.body.token;
  assert.equal((await apiCall(cookies, 'POST', '/api/orgs/', { slug: org, name: 'Br Org' })).status, 201);
  assert.equal((await apiCall(cookies, 'POST', `/api/orgs/${org}/repos`, { slug: repo, name: 'App', visibility: 'public', defaultBranch: 'main' })).status, 201);

  const dir = join(tmp, 'w');
  await git(tmp, 'clone', `http://${user}:${token}@${HOST}/${org}/${repo}.git`, dir);
  await git(dir, 'config', 'user.email', 'a@b.c');
  await git(dir, 'config', 'user.name', 'Author One');
  await mkdir(join(dir, 'src'));
  await writeFile(join(dir, 'README.md'), '# App\n\nHello **world**.\n');
  await writeFile(join(dir, 'src/main.js'), 'export const x = 1;\nconsole.log(x);\n');
  await git(dir, 'add', '-A');
  await git(dir, 'commit', '-m', 'initial commit');
  await writeFile(join(dir, 'src/main.js'), 'export const x = 2;\nconsole.log(x);\nconsole.log("more");\n');
  await git(dir, 'commit', '-am', 'bump x and add a line');
  await git(dir, 'push', 'origin', 'HEAD:main');
});

const B = () => `/api/orgs/${org}/repos/${repo}/browse`;

test('overview returns refs, rendered README, tree and languages', async () => {
  const { status, body } = await apiCall(cookies, 'GET', `${B()}/overview`);
  assert.equal(status, 200);
  assert.equal(body.refs.defaultBranch, 'main');
  assert.equal(body.refs.isEmpty, false);
  assert.match(body.home.readmeHtml, /<strong>world<\/strong>/);
  assert.ok(body.home.entries.some((e) => e.name === 'src' && e.type === 'tree'));
  assert.ok(body.home.languages.some((l) => l.language === 'JavaScript'));
});

test('tree listing for a subdirectory', async () => {
  const { body } = await apiCall(cookies, 'GET', `${B()}/tree/main/src`);
  assert.equal(body.entries.length, 1);
  assert.equal(body.entries[0].name, 'main.js');
  assert.equal(body.entries[0].type, 'blob');
});

test('blob returns file text and detected language', async () => {
  const { body } = await apiCall(cookies, 'GET', `${B()}/blob/main/src/main.js`);
  assert.equal(body.language, 'JavaScript');
  assert.match(body.blob.text, /const x = 2/);
  assert.equal(body.blob.isBinary, false);
});

test('commit history is paginated and per-path filtered', async () => {
  const all = await apiCall(cookies, 'GET', `${B()}/commits/main`);
  assert.equal(all.body.commits.length, 2);
  assert.equal(all.body.commits[0].summary, 'bump x and add a line');

  const readmeOnly = await apiCall(cookies, 'GET', `${B()}/commits/main?path=README.md`);
  assert.equal(readmeOnly.body.commits.length, 1);
});

test('commit detail carries a unified diff', async () => {
  const list = await apiCall(cookies, 'GET', `${B()}/commits/main`);
  const sha = list.body.commits[0].sha;
  const { body } = await apiCall(cookies, 'GET', `${B()}/commit/${sha}`);
  assert.equal(body.commit.summary, 'bump x and add a line');
  const f = body.files.find((x) => x.path === 'src/main.js');
  assert.ok(f);
  assert.match(f.patch, /-export const x = 1/);
  assert.match(f.patch, /\+export const x = 2/);
});

test('blame attributes every line to a commit', async () => {
  const { body } = await apiCall(cookies, 'GET', `${B()}/blame/main/src/main.js`);
  assert.equal(body.lines.length, 3);
  const covered = body.hunks.reduce((n, h) => n + h.lineCount, 0);
  assert.equal(covered, 3);
});

test('commit graph assigns lanes', async () => {
  const { body } = await apiCall(cookies, 'GET', `${B()}/graph`);
  assert.equal(body.length, 2);
  assert.equal(body[0].lane, 0);
  assert.ok(Array.isArray(body[0].parents));
});

test('a private repo is not browsable anonymously', async () => {
  const priv = `br-priv-${S}`;
  assert.equal((await apiCall(cookies, 'POST', `/api/orgs/${org}/repos`, { slug: priv, name: 'P', visibility: 'private' })).status, 201);
  const anon = jar();
  const r = await apiCall(anon, 'GET', `/api/orgs/${org}/repos/${priv}/browse/overview`);
  assert.equal(r.status, 404);
  // but the public one is
  const rp = await apiCall(anon, 'GET', `${B()}/overview`);
  assert.equal(rp.status, 200);
});
