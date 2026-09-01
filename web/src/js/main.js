import { api } from './api.js';
import { initThemeToggle } from './features/theme.js';

initThemeToggle();

function setPill(el, state, label) {
  el.className = `pill ${state}`;
  el.textContent = label;
}

async function refreshStatus() {
  const apiPill = document.getElementById('api-pill');
  const apiDetail = document.getElementById('api-detail');
  const dbPill = document.getElementById('db-pill');
  const dbDetail = document.getElementById('db-detail');

  try {
    const health = await api.health();
    setPill(apiPill, 'ok', 'up');
    apiDetail.textContent = 'GET /health';
    const dbOk = health.db === 'ok';
    setPill(dbPill, dbOk ? 'ok' : 'bad', dbOk ? 'up' : 'down');
    dbDetail.textContent = dbOk ? 'SELECT 1' : (health.error || 'unavailable');
  } catch (err) {
    setPill(apiPill, 'bad', 'down');
    apiDetail.textContent = err.message;
    setPill(dbPill, 'bad', 'unknown');
    dbDetail.textContent = 'API unreachable';
    return;
  }

  try {
    const meta = await api.meta();
    document.getElementById('meta-card').hidden = false;
    document.getElementById('meta-detail').textContent =
      `${meta.name} v${meta.version} · schema phase ${meta.phase}`;
  } catch { /* non-fatal on the skeleton */ }
}

refreshStatus();
setInterval(refreshStatus, 15000);
