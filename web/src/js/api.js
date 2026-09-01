// Thin fetch wrapper. Same-origin: nginx proxies /api and /health to the API container.
// The shared vocabulary the front end is held to (Phase 0 decision): feature modules live in
// js/features/, talk to the server only through this module, and never reach for a framework.

export async function getJson(path, { signal } = {}) {
  const res = await fetch(path, {
    headers: { Accept: 'application/json' },
    signal,
  });
  const body = await res.json().catch(() => null);
  if (!res.ok) {
    const err = new Error(`${res.status} ${res.statusText}`);
    err.status = res.status;
    err.body = body;
    throw err;
  }
  return body;
}

export const api = {
  health: () => getJson('/health'),
  meta: () => getJson('/api/meta'),
};
