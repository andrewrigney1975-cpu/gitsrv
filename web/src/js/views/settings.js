import { api } from '../api.js';
import { session } from '../session.js';
import { el, form, toast, errorToast, confirmDialog, timeAgo } from '../ui.js';
import { navigate } from '../router.js';
import { asyncView } from './_shared.js';

export function renderSettings() {
  return asyncView(async () => {
    const keys = await api.get('/api/user/keys');
    const wrap = el('div', { class: 'stack' });
    wrap.append(el('h1', {}, 'Settings'));

    wrap.append(el('div', { class: 'card' }, el('h2', {}, 'Profile'), form({
      fields: [
        { name: 'displayName', label: 'Display name', value: session.user.displayName },
      ],
      submitLabel: 'Save profile',
      onSubmit: async (v) => { await api.patch('/api/user/profile', v); await session.refresh(); toast('Saved.', 'ok'); },
    }), el('p', { class: 'muted' }, `Username ${session.user.username} · ${session.user.email}`)));

    wrap.append(el('div', { class: 'card' }, el('h2', {}, 'Change password'), form({
      fields: [
        { name: 'currentPassword', label: 'Current password', type: 'password', required: true, autocomplete: 'current-password' },
        { name: 'newPassword', label: 'New password', type: 'password', required: true, hint: 'at least 10 characters', autocomplete: 'new-password' },
      ],
      submitLabel: 'Change password',
      onSubmit: async (v) => {
        await api.post('/api/user/password', v);
        toast('Password changed — please sign in again.', 'ok');
        await session.refresh();
        navigate('/login');
      },
    })));

    const keysCard = el('div', { class: 'card' });
    wrap.append(keysCard);
    renderKeys(keys);

    const tokensCard = el('div', { class: 'card' });
    wrap.append(tokensCard);
    renderTokens(await api.get('/api/user/tokens'));

    function renderTokens(list) {
      const rows = list.map((t) => el('tr', {},
        el('td', {}, el('strong', {}, t.name), el('div', {}, el('code', { class: 'muted' }, `${t.tokenPrefix}…`))),
        el('td', {}, [t.scopeRead && 'read', t.scopeWrite && 'write'].filter(Boolean).join(' + ')),
        el('td', { class: 'muted' }, t.lastUsedAt ? `used ${timeAgo(t.lastUsedAt)}` : 'never used'),
        el('td', { class: 'right' }, el('button', { class: 'danger small', onclick: async () => {
          if (!await confirmDialog(`Revoke token "${t.name}"? Anything using it will stop working.`)) return;
          try { await api.del(`/api/user/tokens/${t.id}`); toast('Token revoked.', 'ok'); refreshTokens(); }
          catch (err) { errorToast(err); }
        }}, 'Revoke'))));

      tokensCard.replaceChildren(
        el('h2', {}, 'Personal access tokens'),
        el('p', { class: 'muted' }, 'Use as the password when cloning or pushing over HTTPS.'),
        el('table', { class: 'data-table' },
          el('tbody', {}, ...(rows.length ? rows : [el('tr', {}, el('td', { colspan: 4, class: 'muted' }, 'No tokens yet.'))]))),
        form({
          fields: [
            { name: 'name', label: 'Token name', required: true, placeholder: 'laptop' },
            { name: 'scopes', label: 'Scopes', type: 'select', value: 'read+write', options: [
              { value: 'read+write', label: 'Read and write' },
              { value: 'read', label: 'Read only' },
            ]},
          ],
          submitLabel: 'Generate token',
          onSubmit: async (v) => {
            const res = await api.post('/api/user/tokens', {
              name: v.name,
              scopeRead: true,
              scopeWrite: v.scopes === 'read+write',
            });
            showTokenOnce(res.token);
            refreshTokens();
          },
        }));
    }

    function showTokenOnce(token) {
      const backdrop = el('div', { class: 'modal-backdrop' });
      backdrop.append(el('div', { class: 'modal' },
        el('h3', {}, 'Copy your new token now'),
        el('p', { class: 'muted' }, 'This is the only time it will be shown.'),
        el('code', { class: 'token-reveal' }, token),
        el('div', { class: 'row end' },
          el('button', { class: 'primary', onclick: () => { navigator.clipboard?.writeText(token); toast('Copied.', 'ok'); backdrop.remove(); } }, 'Copy & close'))));
      document.body.append(backdrop);
    }

    async function refreshTokens() { renderTokens(await api.get('/api/user/tokens')); }

    function renderKeys(list) {
      const rows = list.map((k) => el('tr', {},
        el('td', {}, el('strong', {}, k.title), el('div', {}, el('code', { class: 'muted' }, k.fingerprint))),
        el('td', {}, k.keyType),
        el('td', { class: 'muted' }, `added ${timeAgo(k.createdAt)}`),
        el('td', { class: 'right' }, el('button', { class: 'danger small', onclick: async () => {
          if (!await confirmDialog(`Delete SSH key "${k.title}"?`)) return;
          try { await api.del(`/api/user/keys/${k.id}`); toast('Key removed.', 'ok'); refresh(); }
          catch (err) { errorToast(err); }
        }}, 'Delete'))));

      keysCard.replaceChildren(
        el('h2', {}, 'SSH keys'),
        el('p', { class: 'muted' }, 'Used for Git over SSH from Phase 2 onwards.'),
        el('table', { class: 'data-table' },
          el('tbody', {}, ...(rows.length ? rows : [el('tr', {}, el('td', { colspan: 4, class: 'muted' }, 'No keys yet.'))]))),
        form({
          fields: [
            { name: 'title', label: 'Title', placeholder: 'optional — defaults to the key comment' },
            { name: 'key', label: 'Public key', required: true, placeholder: 'ssh-ed25519 AAAA… you@host' },
          ],
          submitLabel: 'Add SSH key',
          onSubmit: async (v) => { await api.post('/api/user/keys', v); toast('Key added.', 'ok'); refresh(); },
        }));
    }

    async function refresh() { renderKeys(await api.get('/api/user/keys')); }

    return wrap;
  });
}
