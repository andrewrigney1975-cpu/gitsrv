import { el } from '../ui.js';
import { currentPath, navigate } from '../router.js';

/** Async view scaffold: shows a loading state, then either the built node or an error card
 *  that still offers a way out (back / home) so navigation isn't a dead end. */
export function asyncView(loader) {
  const host = el('div', {}, el('div', { class: 'card muted', text: 'Loading…' }));
  loader()
    .then((node) => host.replaceChildren(node))
    .catch((err) => host.replaceChildren(el('div', { class: 'card' },
      el('h1', {}, err.status === 404 ? 'Not found' : 'Something went wrong'),
      el('p', { class: 'muted', text: err.message || 'Unexpected error.' }),
      el('div', { class: 'row' },
        el('button', { onclick: () => history.length > 1 ? history.back() : navigate('/') }, '← Go back'),
        el('a', { class: 'btn', href: '#/' }, 'Home')))));
  return host;
}

export function orgNav(slug, myRole) {
  const path = currentPath();
  const tab = (href, label) => el('a', {
    href: `#${href}`,
    class: 'tab' + (path === href ? ' active' : ''),
  }, label);
  const canAdmin = myRole === 'owner' || myRole === 'admin' || myRole === 'site-admin';
  return el('nav', { class: 'sub-nav' },
    tab(`/o/${slug}`, 'Repositories'),
    tab(`/o/${slug}/packages`, 'Packages'),
    tab(`/o/${slug}/people`, 'People'),
    tab(`/o/${slug}/teams`, 'Teams'),
    canAdmin && tab(`/o/${slug}/settings`, 'Settings'),
    canAdmin && el('span', { class: 'muted tab-hint' }, `you are ${myRole}`));
}
