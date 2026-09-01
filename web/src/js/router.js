// Hash router. Routes are '/o/:slug/teams/:teamSlug' style; params land in ctx.params.

const routes = [];

export function route(pattern, handler) {
  const keys = [];
  const rx = new RegExp('^' + pattern.replace(/:[^/]+/g, (m) => {
    keys.push(m.slice(1));
    return '([^/]+)';
  }).replace(/\//g, '\\/') + '$');
  routes.push({ rx, keys, handler });
}

let notFound = () => {};
export function setNotFound(fn) { notFound = fn; }

export function navigate(path) {
  if (location.hash.slice(1) === path) dispatch();
  else location.hash = path;
}

export function currentPath() {
  return location.hash.slice(1) || '/';
}

export function dispatch() {
  const path = currentPath();
  for (const { rx, keys, handler } of routes) {
    const m = path.match(rx);
    if (m) {
      const params = {};
      keys.forEach((k, i) => { params[k] = decodeURIComponent(m[i + 1]); });
      handler({ path, params });
      return;
    }
  }
  notFound({ path });
}

export function startRouter() {
  window.addEventListener('hashchange', dispatch);
  dispatch();
}
