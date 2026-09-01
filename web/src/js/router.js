// Hash router. Routes are '/o/:slug/teams/:teamSlug' style; params land in ctx.params.

const routes = [];

export function route(pattern, handler) {
  const keys = [];
  // ':name' captures one segment; '*name' at the end captures the rest (may be empty).
  // :name first (left to right), then the trailing *name — so `keys` matches capture-group order.
  let src = pattern
    .replace(/:([A-Za-z0-9_]+)/g, (_, k) => { keys.push(k); return '([^/]+)'; })
    .replace(/\/\*([A-Za-z0-9_]+)$/, (_, k) => { keys.push(k); return '(?:/(.*))?'; });
  const rx = new RegExp('^' + src.replace(/\//g, '\\/') + '$');
  routes.push({ rx, keys, handler });
}

let notFound = () => {};
export function setNotFound(fn) { notFound = fn; }

export function navigate(path) {
  if (location.hash.slice(1) === path) dispatch();
  else location.hash = path;
}

export function currentPath() {
  return (location.hash.slice(1) || '/').split('?')[0];
}

export function currentQuery() {
  const i = location.hash.indexOf('?');
  return i < 0 ? '' : location.hash.slice(i + 1);
}

export function dispatch() {
  const path = currentPath();
  const query = currentQuery();
  for (const { rx, keys, handler } of routes) {
    const m = path.match(rx);
    if (m) {
      const params = {};
      keys.forEach((k, i) => { params[k] = m[i + 1] != null ? decodeURIComponent(m[i + 1]) : ''; });
      handler({ path, params, query });
      return;
    }
  }
  notFound({ path });
}

export function startRouter() {
  window.addEventListener('hashchange', dispatch);
  dispatch();
}
