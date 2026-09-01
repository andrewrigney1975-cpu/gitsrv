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

// --- helpers -------------------------------------------------------------

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
// -c credential.helper= disables every credential helper (incl. the Windows GUI credential manager),
// so an unauthenticated request fails fast instead of blocking on a prompt.
const NO_CRED = ['-c', 'credential.helper=', '-c', 'credential.interactive=false'];
async function git(cwd, ...args) {
  return run('git', [...NO_CRED, ...args], { cwd, env: GIT_ENV, timeout: 30000 });
}

function authUrl(org, repo, user, token) {
  return `http://${encodeURIComponent(user)}:${encodeURIComponent(token)}@${HOST}/${org}/${repo}.git`;
}

// --- fixture -----------------------------------------------------------

let tmp;
let org, repo;
const alice = { name: `git_alice_${S}`, cookies: jar() };
const bob = { name: `git_bob_${S}`, cookies: jar() };

before(async () => {
  tmp = await mkdtemp(join(tmpdir(), 'gitsrv-'));
  org = `git-org-${S}`;
  repo = 'proj';

  for (const u of [alice, bob]) {
    const r = await apiCall(u.cookies, 'POST', '/api/auth/register',
      { username: u.name, email: `${u.name}@ex.com`, displayName: u.name, password: 'correct-horse-battery' });
    assert.equal(r.status, 200, JSON.stringify(r.body));
    const t = await apiCall(u.cookies, 'POST', '/api/user/tokens', { name: 'test', scopeRead: true, scopeWrite: true });
    assert.equal(t.status, 201, JSON.stringify(t.body));
    u.token = t.body.token;
  }
  // read-only token for alice
  const ro = await apiCall(alice.cookies, 'POST', '/api/user/tokens', { name: 'ro', scopeRead: true, scopeWrite: false });
  alice.roToken = ro.body.token;

  assert.equal((await apiCall(alice.cookies, 'POST', '/api/orgs/', { slug: org, name: 'Git Org' })).status, 201);
  assert.equal((await apiCall(alice.cookies, 'POST', `/api/orgs/${org}/repos`,
    { slug: repo, name: 'Proj', visibility: 'private', defaultBranch: 'main' })).status, 201);
});

after(async () => { if (tmp) await rm(tmp, { recursive: true, force: true }); });

// --- tests -----------------------------------------------------------

test('clone empty repo, commit, push over HTTPS', async () => {
  const dir = join(tmp, 'work');
  await git(tmp, 'clone', authUrl(org, repo, alice.name, alice.token), dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await writeFile(join(dir, 'README.md'), '# Proj\n');
  await git(dir, 'add', '-A');
  await git(dir, 'commit', '-m', 'first');
  await git(dir, 'push', 'origin', 'HEAD:main');

  // ref advertisement now shows the branch
  const { stdout } = await git(tmp, 'ls-remote', authUrl(org, repo, alice.name, alice.token));
  assert.match(stdout, /refs\/heads\/main/);
});

test('a second user with read access can clone but not push', async () => {
  // grant bob read via direct collaborator
  assert.equal((await apiCall(alice.cookies, 'POST', `/api/orgs/${org}/repos/${repo}/collaborators`,
    { username: bob.name, permission: 'read' })).status, 204);

  const dir = join(tmp, 'bob');
  await git(tmp, 'clone', authUrl(org, repo, bob.name, bob.token), dir);
  assert.match((await git(dir, 'log', '--oneline')).stdout, /first/);

  await writeFile(join(dir, 'x.txt'), 'x');
  await git(dir, 'config', 'user.email', 'b@ex.com');
  await git(dir, 'config', 'user.name', 'Bob');
  await git(dir, 'add', '-A');
  await git(dir, 'commit', '-m', 'nope');
  await assert.rejects(git(dir, 'push', 'origin', 'HEAD:main'), /denied|403|not have write|forbidden/i);
});

test('a read-only token cannot push even for a user who can write', async () => {
  const dir = join(tmp, 'alice-ro');
  await git(tmp, 'clone', authUrl(org, repo, alice.name, alice.roToken), dir);
  await writeFile(join(dir, 'y.txt'), 'y');
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await git(dir, 'add', '-A');
  await git(dir, 'commit', '-m', 'ro');
  await assert.rejects(git(dir, 'push', 'origin', 'HEAD:main'), /scope|denied|403/i);
});

test('anonymous clone is refused for a private repo, allowed once public', async () => {
  await assert.rejects(
    git(tmp, 'clone', `http://${HOST}/${org}/${repo}.git`, join(tmp, 'anon-fail')),
    /Authentication|403|401|denied|unable to get password|could not read Username/i);

  assert.equal((await apiCall(alice.cookies, 'PATCH', `/api/orgs/${org}/repos/${repo}/`,
    { name: 'Proj', description: '', visibility: 'public', isArchived: false })).status, 204);

  await git(tmp, 'clone', `http://${HOST}/${org}/${repo}.git`, join(tmp, 'anon-ok'));
  assert.match((await git(join(tmp, 'anon-ok'), 'log', '--oneline')).stdout, /first/);
});

test('force-push is accepted from a writer', async () => {
  const dir = join(tmp, 'force');
  await git(tmp, 'clone', authUrl(org, repo, alice.name, alice.token), dir);
  await git(dir, 'config', 'user.email', 'a@ex.com');
  await git(dir, 'config', 'user.name', 'Alice');
  await git(dir, 'commit', '--allow-empty', '-m', 'rewrite');
  await git(dir, 'push', '--force', 'origin', 'HEAD:main');
});

// SSH transport. Runs only when GITSRV_SSH_PORT is set (a published ssh port on the same host).
const SSH_PORT = process.env.GITSRV_SSH_PORT;
test('clone and push over SSH, with permissions enforced', { skip: !SSH_PORT && 'GITSRV_SSH_PORT not set' }, async () => {
  const keyDir = join(tmp, 'keys');
  await run('mkdir', ['-p', keyDir]);
  const keyPath = join(keyDir, 'id');
  await run('ssh-keygen', ['-t', 'ed25519', '-N', '', '-C', 'ct', '-f', keyPath, '-q']);
  const pub = (await run('cat', [`${keyPath}.pub`])).stdout.trim();

  const r = await apiCall(alice.cookies, 'POST', '/api/user/keys', { title: 'ct', key: pub });
  assert.equal(r.status, 201, JSON.stringify(r.body));

  const host = new URL(BASE).hostname;
  const fwd = (p) => p.replace(/\\/g, '/'); // ssh wants forward slashes even on Windows
  const sshEnv = {
    ...GIT_ENV,
    GIT_SSH_COMMAND: `ssh -i ${fwd(keyPath)} -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -p ${SSH_PORT}`,
  };
  const sshGit = (cwd, ...args) => run('git', [...NO_CRED, ...args], { cwd, env: sshEnv, timeout: 30000 });
  const url = `git@${host}:${org}/${repo}.git`;

  const dir = join(tmp, 'ssh-work');
  await sshGit(tmp, 'clone', url, dir);
  await sshGit(dir, 'config', 'user.email', 'a@ex.com');
  await sshGit(dir, 'config', 'user.name', 'Alice');
  await writeFile(join(dir, 'ssh.txt'), 'via ssh\n');
  await sshGit(dir, 'add', '-A');
  await sshGit(dir, 'commit', '-m', 'ssh push');
  await sshGit(dir, 'push', 'origin', 'HEAD:main');

  // bob (read only, granted earlier) can clone over SSH but not push
  const bobKey = join(keyDir, 'bob');
  await run('ssh-keygen', ['-t', 'ed25519', '-N', '', '-C', 'bob', '-f', bobKey, '-q']);
  const bobPub = (await run('cat', [`${bobKey}.pub`])).stdout.trim();
  assert.equal((await apiCall(bob.cookies, 'POST', '/api/user/keys', { title: 'bob', key: bobPub })).status, 201);

  const bobEnv = { ...GIT_ENV, GIT_SSH_COMMAND: sshEnv.GIT_SSH_COMMAND.replace(fwd(keyPath), fwd(bobKey)) };
  const bobGit = (cwd, ...args) => run('git', [...NO_CRED, ...args], { cwd, env: bobEnv, timeout: 30000 });
  const bd = join(tmp, 'ssh-bob');
  await bobGit(tmp, 'clone', url, bd);
  await bobGit(bd, 'config', 'user.email', 'b@x.c');
  await bobGit(bd, 'config', 'user.name', 'b');
  await bobGit(bd, 'commit', '--allow-empty', '-m', 'x');
  await assert.rejects(bobGit(bd, 'push', 'origin', 'HEAD:main'), /denied|Could not read|not have write/i);
});
