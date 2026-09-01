import { api } from '../api.js';
import { session } from '../session.js';
import { el, form, toast } from '../ui.js';
import { navigate } from '../router.js';

export function renderAuth() {
  const wrap = el('div', { class: 'auth-card card' });
  let mode = 'login';

  function render() {
    wrap.replaceChildren();
    wrap.append(
      el('h1', {}, mode === 'login' ? 'Sign in to GitSrv' : 'Create your account'),
      mode === 'login' ? loginForm() : registerForm(),
      el('p', { class: 'muted' },
        mode === 'login' ? "No account yet? " : 'Already have an account? ',
        el('a', { href: '#', onclick: (e) => { e.preventDefault(); mode = mode === 'login' ? 'register' : 'login'; render(); } },
          mode === 'login' ? 'Register' : 'Sign in')),
    );
  }

  function loginForm() {
    return form({
      fields: [
        { name: 'usernameOrEmail', label: 'Username or email', required: true, autocomplete: 'username' },
        { name: 'password', label: 'Password', type: 'password', required: true, autocomplete: 'current-password' },
      ],
      submitLabel: 'Sign in',
      onSubmit: async (v) => {
        await api.post('/api/auth/login', v);
        await session.refresh();
        toast(`Welcome back, ${session.user.username}.`, 'ok');
        navigate('/');
      },
    });
  }

  function registerForm() {
    return form({
      fields: [
        { name: 'username', label: 'Username', required: true, hint: 'lowercase letters, digits, - or _ · becomes your URL', autocomplete: 'username' },
        { name: 'email', label: 'Email', type: 'email', required: true, autocomplete: 'email' },
        { name: 'displayName', label: 'Display name', autocomplete: 'name' },
        { name: 'password', label: 'Password', type: 'password', required: true, hint: 'at least 10 characters', autocomplete: 'new-password' },
      ],
      submitLabel: 'Create account',
      onSubmit: async (v) => {
        await api.post('/api/auth/register', v);
        await session.refresh();
        toast('Account created. The first account is the site admin.', 'ok');
        navigate('/');
      },
    });
  }

  render();
  return wrap;
}
