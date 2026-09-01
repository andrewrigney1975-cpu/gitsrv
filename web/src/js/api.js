// Single choke point between the front end and the server (Phase 0 convention).
// - attaches the CSRF header to every unsafe request
// - on a 401, transparently tries /api/auth/refresh once, then replays the request
// - throws ApiError { status, message, body } for non-2xx

const CSRF_HEADER = 'X-GitSrv-CSRF';

export class ApiError extends Error {
  constructor(status, message, body) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

let refreshing = null;

async function raw(method, path, body) {
  const opts = {
    method,
    headers: { Accept: 'application/json' },
    credentials: 'same-origin',
  };
  if (method !== 'GET' && method !== 'HEAD') {
    opts.headers[CSRF_HEADER] = '1';
  }
  if (body !== undefined) {
    opts.headers['Content-Type'] = 'application/json';
    opts.body = JSON.stringify(body);
  }
  return fetch(path, opts);
}

async function request(method, path, body, { retry = true } = {}) {
  let res = await raw(method, path, body);

  if (res.status === 401 && retry && !path.startsWith('/api/auth/')) {
    refreshing ??= raw('POST', '/api/auth/refresh').finally(() => { refreshing = null; });
    const refreshed = await refreshing;
    if (refreshed.ok) {
      res = await raw(method, path, body);
    }
  }

  const text = await res.text();
  const data = text ? JSON.parse(text) : null;

  if (!res.ok) {
    throw new ApiError(res.status, data?.error || `${res.status} ${res.statusText}`, data);
  }
  return data;
}

export const api = {
  get: (p) => request('GET', p),
  post: (p, b) => request('POST', p, b),
  patch: (p, b) => request('PATCH', p, b),
  del: (p) => request('DELETE', p),

  // convenience
  health: () => request('GET', '/health'),
  meta: () => request('GET', '/api/meta'),
  me: () => request('GET', '/api/user/'),
};
