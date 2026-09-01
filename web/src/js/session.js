// Current-user state, loaded once and refreshed after mutations that change it.
import { api } from './api.js';

const listeners = new Set();
let state = { loaded: false, user: null, organisations: [] };

export const session = {
  get user() { return state.user; },
  get organisations() { return state.organisations; },
  get isAuthenticated() { return !!state.user; },
  get loaded() { return state.loaded; },

  onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },

  async load() {
    try {
      const data = await api.me();
      state = { loaded: true, user: data.user, organisations: data.organisations };
    } catch {
      state = { loaded: true, user: null, organisations: [] };
    }
    emit();
    return state;
  },

  async refresh() { return this.load(); },

  async logout() {
    await api.post('/api/auth/logout');
    state = { loaded: true, user: null, organisations: [] };
    emit();
  },
};

function emit() { for (const fn of listeners) fn(state); }
