import { test } from 'node:test';
import assert from 'node:assert/strict';

// Base URL of a running stack. CI and `docker compose up` both publish the web tier here.
const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';

async function get(path) {
  const res = await fetch(BASE + path, { headers: { Accept: 'application/json' } });
  const body = await res.json();
  return { status: res.status, body };
}

test('web tier serves the app shell', async () => {
  const res = await fetch(BASE + '/');
  assert.equal(res.status, 200);
  const html = await res.text();
  assert.match(html, /GitSrv/);
});

test('/health reports the API and database up', async () => {
  const { status, body } = await get('/health');
  assert.equal(status, 200);
  assert.equal(body.status, 'ok');
  assert.equal(body.db, 'ok');
});

test('/api/meta reports the running build', async () => {
  const { status, body } = await get('/api/meta');
  assert.equal(status, 200);
  assert.equal(body.name, 'GitSrv');
  assert.equal(body.phase, 0);
});
