import { api } from '../api.js';
import { el, toast, timeAgo, errorToast } from '../ui.js';
import { asyncView } from './_shared.js';
import { shell } from './repo.js';

async function ctx(slug, repoSlug) {
  const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview`);
  return { b: { ...ov.repo, myPermission: ov.myPermission } };
}
const R = (s, r) => `/api/orgs/${s}/repos/${r}`;
const statusPill = (s) => {
  const c = s.conclusion || s.status;
  const map = { success: 'ok', failure: 'bad', cancelled: 'bad', running: 'pending', queued: 'pending', completed: 'ok' };
  return el('span', { class: `pill ${map[c] || ''}` }, c);
};

export function renderActionsList(slug, repoSlug) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const runs = await api.get(`${R(slug, repoSlug)}/actions`);
    const rows = runs.map((run) => el('div', { class: 'pr-row' },
      el('div', {},
        el('a', { href: `#/o/${slug}/${repoSlug}/actions/${run.number}` },
          el('strong', {}, `${run.workflowName} #${run.number}`)),
        statusPill(run),
        el('div', { class: 'muted' }, `${run.event} · ${run.ref.replace('refs/heads/', '')} · ${run.headSha.slice(0, 7)} · ${timeAgo(run.createdAt)}`))));
    const body = el('div', { class: 'stack' },
      el('h1', {}, 'Actions'),
      runs.length ? el('div', { class: 'card nopad' }, ...rows)
        : el('div', { class: 'card empty muted' }, 'No workflow runs. Add .gitsrv/workflows/*.yml and push.'));
    return shell(b, null, 'actions', body);
  });
}

export function renderActionRun(slug, repoSlug, number) {
  let timer;
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const host = el('div', { class: 'stack' });
    const logCache = {}; // jobId -> {seq, node}

    async function tick() {
      const d = await api.get(`${R(slug, repoSlug)}/actions/${number}`);
      render(d);
      if (d.run.status === 'completed' && timer) { clearInterval(timer); timer = null; }
    }

    function render(d) {
      const canRerun = ['write', 'maintain', 'admin'].includes(b.myPermission);
      const jobNodes = d.jobs.map((j) => {
        const box = el('details', { class: 'job-box', open: j.status !== 'completed' || j.conclusion !== 'success' });
        box.append(el('summary', {}, statusPill(j), ` ${j.name} `, el('span', { class: 'muted' }, j.runsOn)));
        const steps = el('div', { class: 'steps' });
        for (const s of j.steps) {
          steps.append(el('div', { class: 'step-line' }, statusPill(s), ` ${s.name}`, s.exitCode != null && s.exitCode !== 0 && el('span', { class: 'bad' }, ` (exit ${s.exitCode})`)));
        }
        const logPre = logCache[j.id]?.node || el('pre', { class: 'job-log' });
        logCache[j.id] ??= { seq: 0, node: logPre };
        box.append(steps, logPre);
        api.get(`${R(slug, repoSlug)}/actions/${number}/jobs/${j.id}/logs?after=${logCache[j.id].seq}`).then((lines) => {
          for (const l of lines) {
            logPre.append(document.createTextNode(l.line + '\n'));
            logCache[j.id].seq = l.seq;
          }
        }).catch(() => {});
        return box;
      });

      host.replaceChildren(
        el('div', { class: 'page-head' },
          el('h1', {}, `${d.run.workflowName} #${d.run.number} `, statusPill(d.run)),
          canRerun && el('button', { class: 'small', onclick: async () => {
            try { await api.post(`${R(slug, repoSlug)}/actions/${number}/rerun`); toast('Re-run queued.', 'ok'); }
            catch (e) { errorToast(e); }
          } }, 'Re-run')),
        el('div', { class: 'muted' }, `${d.run.event} · ${d.run.headSha.slice(0, 7)}${d.run.prNumber ? ` · PR #${d.run.prNumber}` : ''}`),
        ...jobNodes);
    }

    await tick();
    timer = setInterval(tick, 3000);
    return shell(b, null, 'actions', host);
  });
}
