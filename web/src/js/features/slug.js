import { api } from '../api.js';

// Mirrors the server's Slug.Suggest: lowercase, non-alphanumerics -> '-', collapse, trim, cap 40.
export function suggestSlug(name) {
  return (name || '')
    .trim().toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^[-_]+|[-_]+$/g, '')
    .slice(0, 40)
    .replace(/^[-_]+|[-_]+$/g, '');
}

const RESERVED = new Set([
  'admin', 'api', 'settings', 'help', 'about', 'new', 'explore', 'search', 'login', 'logout',
  'signin', 'signup', 'register', 'orgs', 'teams', 'users', 'repos', 'git', 'gitsrv', 'www',
]);
const VALID = /^[a-z0-9](?:[a-z0-9]|[-_](?![-_]))*[a-z0-9]$|^[a-z0-9]$/;

/** Client-side shape check before we bother the server. */
export function localSlugError(slug) {
  if (!slug) return 'Required.';
  if (slug.length > 40) return 'Too long (40 max).';
  if (RESERVED.has(slug)) return `"${slug}" is reserved.`;
  if (!VALID.test(slug)) return 'Lowercase letters, digits, single - or _ between.';
  return null;
}

// check(value) implementations for form fields --------------------------

export function orgSlugCheck() {
  return async (slug) => {
    const local = localSlugError(slug);
    if (local) return { ok: false, message: local };
    const r = await api.get(`/api/slug-available/org?slug=${encodeURIComponent(slug)}`);
    return { ok: r.available, message: r.available ? 'Available' : 'That org slug is taken.' };
  };
}

export function repoSlugCheck(orgSlug) {
  return async (slug) => {
    const local = localSlugError(slug);
    if (local) return { ok: false, message: local };
    const r = await api.get(`/api/orgs/${orgSlug}/slug-available?kind=repo&slug=${encodeURIComponent(slug)}`);
    return { ok: r.available, message: r.available ? 'Available' : 'That repo slug already exists in this org.' };
  };
}

export function teamSlugCheck(orgSlug) {
  return async (slug) => {
    const local = localSlugError(slug);
    if (local) return { ok: false, message: local };
    const r = await api.get(`/api/orgs/${orgSlug}/slug-available?kind=team&slug=${encodeURIComponent(slug)}`);
    return { ok: r.available, message: r.available ? 'Available' : 'That team slug already exists in this org.' };
  };
}
