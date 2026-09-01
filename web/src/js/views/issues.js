import { api } from '../api.js';
import { el, form, toast, errorToast, timeAgo, confirmDialog } from '../ui.js';
import { navigate } from '../router.js';
import { session } from '../session.js';
import { asyncView } from './_shared.js';
import { shell } from './repo.js';

async function ctx(slug, repoSlug) {
  const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview`);
  return { b: { ...ov.repo, myPermission: ov.myPermission } };
}
const R = (slug, repoSlug) => `/api/orgs/${slug}/repos/${repoSlug}`;
const canEdit = (perm) => ['triage', 'write', 'maintain', 'admin'].includes(perm);

function labelChip(l) {
  return el('span', { class: 'label-chip', style: `background:${l.color};color:${contrast(l.color)}` }, l.name);
}
function contrast(hex) {
  const n = parseInt(hex.slice(1), 16);
  const l = (0.299 * ((n >> 16) & 255) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)) / 255;
  return l > 0.6 ? '#111' : '#fff';
}

// ---- list ----

export function renderIssueList(slug, repoSlug, query) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const q = new URLSearchParams(query || '');
    const state = q.get('state') || 'open';
    const items = await api.get(`${R(slug, repoSlug)}/issues?state=${state}`);
    const labels = await api.get(`${R(slug, repoSlug)}/labels`);

    const filter = (s, label) => el('a', { href: `#/o/${slug}/${repoSlug}/issues?state=${s}`, class: 'tab' + (state === s ? ' active' : '') }, label);

    const rows = items.map((i) => el('div', { class: 'pr-row' },
      el('div', {},
        el('a', { href: `#/o/${slug}/${repoSlug}/issues/${i.number}` }, el('strong', {}, i.title)),
        ...i.labels.map(labelChip),
        el('div', { class: 'muted' }, `#${i.number} · ${i.authorUsername} · ${state === 'closed' ? 'closed' : 'opened'} ${timeAgo(i.createdAt)}`,
          i.assignees.length ? ` · assigned: ${i.assignees.join(', ')}` : '',
          i.milestone ? ` · ${i.milestone}` : '')),
      i.comments > 0 && el('span', { class: 'muted' }, `💬 ${i.comments}`)));

    const body = el('div', { class: 'stack' },
      el('div', { class: 'page-head' },
        el('nav', { class: 'sub-nav' }, filter('open', 'Open'), filter('closed', 'Closed'), filter('all', 'All')),
        el('div', { class: 'row' },
          b.myPermission === 'admin' && el('a', { class: 'btn', href: `#/o/${slug}/${repoSlug}/labels` }, 'Labels'),
          b.myPermission === 'admin' && el('a', { class: 'btn', href: `#/o/${slug}/${repoSlug}/milestones` }, 'Milestones'),
          canEdit(b.myPermission) && el('a', { class: 'btn primary', href: `#/o/${slug}/${repoSlug}/issues/new` }, 'New issue'))),
      rows.length ? el('div', { class: 'card nopad pr-list' }, ...rows) : el('div', { class: 'card empty muted' }, `No ${state} issues.`));
    return shell(b, null, 'issues', body);
  });
}

// ---- new ----

export function renderNewIssue(slug, repoSlug) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const body = el('div', { class: 'card' }, el('h2', {}, 'New issue'), form({
      fields: [
        { name: 'title', label: 'Title', required: true },
        { name: 'body', label: 'Description' },
      ],
      submitLabel: 'Create issue',
      onSubmit: async (v) => {
        const res = await api.post(`${R(slug, repoSlug)}/issues`, { title: v.title, body: v.body });
        navigate(`/o/${slug}/${repoSlug}/issues/${res.number}`);
      },
    }));
    return shell(b, null, 'issues', body);
  });
}

// ---- detail ----

export function renderIssueDetail(slug, repoSlug, number) {
  return asyncView(async () => build());

  async function build() {
    const res = await api.get(`${R(slug, repoSlug)}/issues/${number}`);
    const d = res.detail;
    const perm = res.myPermission;
    const editable = canEdit(perm);
    const b = { orgSlug: slug, repoSlug, defaultBranch: 'main', myPermission: perm };
    const refresh = async () => document.getElementById('view').replaceChildren(await renderIssueDetail(slug, repoSlug, number));

    const main = el('div', { class: 'issue-main stack' });
    main.append(el('div', {},
      el('h1', {}, d.title, el('span', { class: 'muted' }, ` #${d.number}`)),
      el('div', { class: 'pr-substate' },
        el('span', { class: `pill state-${d.state === 'open' ? 'open' : 'closed'}` }, d.state),
        el('span', { class: 'muted' }, ` ${d.authorUsername} opened ${timeAgo(d.createdAt)}`),
        editable && el('button', { class: 'small', onclick: async () => {
          await api.post(`${R(slug, repoSlug)}/issues/${number}/state`, { state: d.state === 'open' ? 'closed' : 'open' });
          refresh();
        } }, d.state === 'open' ? 'Close issue' : 'Reopen'))));

    if (d.body) main.append(el('div', { class: 'card' },
      el('div', { class: 'muted' }, `${d.authorUsername} · ${timeAgo(d.createdAt)}`),
      el('div', { class: 'markdown', html: d.bodyHtml })));

    // interleave comments + events
    const timeline = [
      ...d.comments.map((c) => ({ t: c.createdAt, node: commentCard(c) })),
      ...d.events.filter((e) => e.kind !== 'opened').map((e) => ({ t: e.createdAt, node: eventLine(e) })),
    ].sort((a, z) => new Date(a.t) - new Date(z.t));
    for (const x of timeline) main.append(x.node);

    if (d.references.length) main.append(el('div', { class: 'card muted' },
      'Referenced by ', ...d.references.map((r) => el('span', {}, `${r.sourceKind} ${r.sourceRef}${r.closes ? ' (closes)' : ''} `))));

    if (editable && d.state === 'open') main.append(el('div', { class: 'card' }, commentForm()));

    function commentCard(c) {
      const card = el('div', { class: 'card comment-card' },
        el('div', { class: 'muted' }, `${c.authorUsername} · ${timeAgo(c.createdAt)}`, c.updatedAt !== c.createdAt && ' (edited)'),
        el('div', { class: 'markdown', html: c.bodyHtml }));
      if (c.authorUsername === session.user?.username) {
        card.append(el('div', { class: 'row' },
          el('button', { class: 'small', onclick: () => {
            const ta = el('textarea', { rows: 4 }); ta.value = c.body;
            card.replaceChildren(ta, el('button', { class: 'small primary', onclick: async () => {
              await api.patch(`${R(slug, repoSlug)}/issues/${number}/comments/${c.id}`, { body: ta.value }); refresh();
            } }, 'Save'));
          } }, 'Edit'),
          el('button', { class: 'small danger', onclick: async () => {
            if (!await confirmDialog('Delete this comment?')) return;
            await api.del(`${R(slug, repoSlug)}/issues/${number}/comments/${c.id}`); refresh();
          } }, 'Delete')));
      }
      return card;
    }

    function eventLine(e) {
      const verb = {
        closed: 'closed this', reopened: 'reopened this', assigned: 'updated assignees',
        labeled: 'updated labels', milestoned: 'updated the milestone', referenced: `referenced this (${e.detail})`,
      }[e.kind] || e.kind;
      return el('div', { class: 'event-line muted' }, `${e.actorUsername || 'someone'} ${verb} · ${timeAgo(e.createdAt)}`);
    }

    function commentForm() {
      const ta = el('textarea', { rows: 4, placeholder: 'Leave a comment' });
      return el('div', { class: 'comment-form' }, ta, el('button', { class: 'small primary', onclick: async () => {
        if (!ta.value.trim()) return;
        try { await api.post(`${R(slug, repoSlug)}/issues/${number}/comments`, { body: ta.value }); refresh(); }
        catch (err) { errorToast(err); }
      } }, 'Comment'));
    }

    // sidebar
    const side = el('div', { class: 'issue-side stack' });
    const allLabels = editable ? await api.get(`${R(slug, repoSlug)}/labels`) : [];
    const milestones = editable ? await api.get(`${R(slug, repoSlug)}/milestones?state=open`) : [];

    side.append(el('div', { class: 'card' }, el('h3', {}, 'Assignees'),
      d.assignees.length ? el('div', {}, d.assignees.join(', ')) : el('span', { class: 'muted' }, 'None'),
      editable && inlineEdit('Set assignees (comma-separated usernames)', d.assignees.join(', '), async (val) => {
        await api.put(`${R(slug, repoSlug)}/issues/${number}/assignees`, { usernames: val.split(',').map((s) => s.trim()).filter(Boolean) });
        refresh();
      })));

    side.append(el('div', { class: 'card' }, el('h3', {}, 'Labels'),
      d.labels.length ? el('div', { class: 'row wrap' }, ...d.labels.map(labelChip)) : el('span', { class: 'muted' }, 'None'),
      editable && el('div', { class: 'row wrap' }, ...allLabels.map((l) => {
        const on = d.labels.some((x) => x.id === l.id);
        return el('button', { class: 'small' + (on ? ' primary' : ''), onclick: async () => {
          const ids = on ? d.labels.filter((x) => x.id !== l.id).map((x) => x.id) : [...d.labels.map((x) => x.id), l.id];
          await api.put(`${R(slug, repoSlug)}/issues/${number}/labels`, { labelIds: ids });
          refresh();
        } }, l.name);
      }))));

    side.append(el('div', { class: 'card' }, el('h3', {}, 'Milestone'),
      d.milestone ? el('div', {}, d.milestone.title) : el('span', { class: 'muted' }, 'None'),
      editable && el('select', { onchange: async (e) => {
        await api.patch(`${R(slug, repoSlug)}/issues/${number}`, e.target.value ? { milestoneId: +e.target.value } : { clearMilestone: true });
        refresh();
      } }, el('option', { value: '' }, '— none —'), ...milestones.map((m) => el('option', { value: m.id, selected: d.milestone?.id === m.id }, m.title)))));

    function inlineEdit(placeholder, value, onSave) {
      const inp = el('input', { type: 'text', value, placeholder });
      return el('div', { class: 'row' }, inp, el('button', { class: 'small', onclick: () => onSave(inp.value).catch(errorToast) }, 'Save'));
    }

    return shell(b, null, 'issues', el('div', { class: 'issue-layout' }, main, side));
  }
}

// ---- labels / milestones admin ----

export function renderLabels(slug, repoSlug) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const host = el('div', { class: 'stack' });
    async function refresh() {
      const labels = await api.get(`${R(slug, repoSlug)}/labels`);
      host.replaceChildren(
        el('h1', {}, 'Labels'),
        el('div', { class: 'card nopad' }, ...labels.map((l) => el('div', { class: 'pr-row' },
          labelChip(l), el('span', { class: 'muted' }, l.description),
          el('button', { class: 'small danger', onclick: async () => { await api.del(`${R(slug, repoSlug)}/labels/${l.id}`); refresh(); } }, 'Delete')))),
        el('div', { class: 'card' }, el('h2', {}, 'New label'), form({
          fields: [
            { name: 'name', label: 'Name', required: true },
            { name: 'color', label: 'Colour (hex)', value: '#0c66e4' },
            { name: 'description', label: 'Description' },
          ],
          submitLabel: 'Create label',
          onSubmit: async (v) => { await api.post(`${R(slug, repoSlug)}/labels`, v); refresh(); },
        })));
    }
    await refresh();
    return shell(b, null, 'issues', host);
  });
}

export function renderMilestones(slug, repoSlug) {
  return asyncView(async () => {
    const { b } = await ctx(slug, repoSlug);
    const host = el('div', { class: 'stack' });
    async function refresh() {
      const ms = await api.get(`${R(slug, repoSlug)}/milestones?state=all`);
      host.replaceChildren(
        el('h1', {}, 'Milestones'),
        el('div', { class: 'card nopad' }, ...ms.map((m) => el('div', { class: 'pr-row' },
          el('div', {}, el('strong', {}, m.title), el('div', { class: 'muted' },
            `${m.openIssues} open / ${m.closedIssues} closed${m.dueOn ? ' · due ' + m.dueOn : ''} · ${m.state}`)),
          el('div', { class: 'row' },
            el('button', { class: 'small', onclick: async () => {
              await api.patch(`${R(slug, repoSlug)}/milestones/${m.id}`, { title: m.title, description: m.description, dueOn: m.dueOn, state: m.state === 'open' ? 'closed' : 'open' });
              refresh();
            } }, m.state === 'open' ? 'Close' : 'Reopen'),
            el('button', { class: 'small danger', onclick: async () => { await api.del(`${R(slug, repoSlug)}/milestones/${m.id}`); refresh(); } }, 'Delete'))))),
        el('div', { class: 'card' }, el('h2', {}, 'New milestone'), form({
          fields: [
            { name: 'title', label: 'Title', required: true },
            { name: 'description', label: 'Description' },
            { name: 'dueOn', label: 'Due date', type: 'date' },
          ],
          submitLabel: 'Create milestone',
          onSubmit: async (v) => { await api.post(`${R(slug, repoSlug)}/milestones`, { ...v, dueOn: v.dueOn || null }); refresh(); },
        })));
    }
    await refresh();
    return shell(b, null, 'issues', host);
  });
}
