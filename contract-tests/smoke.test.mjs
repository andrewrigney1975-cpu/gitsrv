import { test } from 'node:test';
import assert from 'node:assert/strict';

const BASE = process.env.GITSRV_BASE_URL ?? 'http://localhost:8080';

async function get(path) {
  const res = await fetch(BASE + path, { headers: { Accept: 'application/json' } });
  return { status: res.status, body: await res.json() };
}

test('web tier serves the app shell', async () => {
  const res = await fetch(BASE + '/');
  assert.equal(res.status, 200);
  assert.match(await res.text(), /GitSrv/);
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
  assert.equal(body.phase, 11);
});
