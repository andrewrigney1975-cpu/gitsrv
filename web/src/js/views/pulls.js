import { api } from '../api.js';
import { el, form, toast, errorToast, timeAgo, confirmDialog } from '../ui.js';
import { navigate } from '../router.js';
import { session } from '../session.js';
import { asyncView } from './_shared.js';
import { shell, enc } from './repo.js';

async function repoCtx(slug, repoSlug) {
  const ov = await api.get(`/api/orgs/${slug}/repos/${repoSlug}/browse/overview`);
  return { b: { ...ov.repo, myPermission: ov.myPermission }, refs: ov.refs };
}
const P = (slug, repoSlug) => `/api/orgs/${slug}/repos/${repoSlug}/pulls`;

// ---- list ----

export function renderPullList(slug, repoSlug, query) {
  return asyncView(async () => {
    const { b } = await repoCtx(slug, repoSlug);
    const state = new URLSearchParams(query || '').get('state') || 'open';
    const items = await api.get(`${P(slug, repoSlug)}/?state=${state}`);

    const filterLink = (s, label) => el('a', {
      href: `#/o/${slug}/${repoSlug}/pulls?state=${s}`, class: 'tab' + (state === s ? ' active' : ''),
    }, label);

    const rows = items.map((p) => el('div', { class: 'pr-row' },
      el('div', {},
        el('a', { href: `#/o/${slug}/${repoSlug}/pulls/${p.number}` }, el('strong', {}, p.title)),
        p.isDraft && el('span', { class: 'pill' }, 'draft'),
        el('span', { class: `pill state-${p.state}` }, p.state),
        el('div', { class: 'muted' }, `#${p.number} · ${p.authorUsername} · ${p.headBranch} → ${p.baseBranch} · updated ${timeAgo(p.updatedAt)}`)),
      p.comments > 0 && el('span', { class: 'muted' }, `💬 ${p.comments}`)));

    const body = el('div', { class: 'stack' },
      el('div', { class: 'page-head' },
        el('nav', { class: 'sub-nav' }, filterLink('open', 'Open'), filterLink('closed', 'Closed'), filterLink('all', 'All')),
        b.myPermission !== 'read' && b.myPermission !== 'none' && el('a', { class: 'btn primary', href: `#/o/${slug}/${repoSlug}/pulls/new` }, 'New pull request')),
      rows.length ? el('div', { class: 'card nopad pr-list' }, ...rows) : el('div', { class: 'card empty muted' }, `No ${state} pull requests.`));
    return shell(b, null, 'pulls', body);
  });
}

// ---- new PR ----

export function renderNewPull(slug, repoSlug, query) {
  return asyncView(async () => {
    const { b, refs } = await repoCtx(slug, repoSlug);
    const q = new URLSearchParams(query || '');
    const branches = refs.branches.map((r) => r.name);
    let base = q.get('base') || refs.defaultBranch || branches[0];
    let head = q.get('head') || branches.find((x) => x !== base) || base;

    const previewHost = el('div', {});
    const titleInput = el('input', { type: 'text', placeholder: 'Title', required: true });
    const bodyInput = el('textarea', { rows: 5, placeholder: 'Describe the change' });
    const draftCheck = el('input', { type: 'checkbox' });

    const pick = (val, cur, on) => {
      const s = el('select', { onchange: (e) => on(e.target.value) });
      for (const x of branches) s.append(el('option', { value: x, selected: x === cur }, x));
      s.value = cur;
      return s;
    };

    async function refresh() {
      previewHost.replaceChildren(el('div', { class: 'muted' }, 'Comparing…'));
      try {
        const cmp = await api.get(`${P(slug, repoSlug)}/compare?base=${enc(base)}&head=${enc(head)}`);
        if (!titleInput.value && cmp.commits.length === 1) titleInput.value = cmp.commits[0].summary;
        previewHost.replaceChildren(comparePreview(cmp));
      } catch (err) {
        previewHost.replaceChildren(el('div', { class: 'card bad-note' }, err.message));
      }
    }

    const bar = el('div', { class: 'compare-bar card' },
      el('span', {}, 'base '), pick(base, base, (v) => { base = v; refresh(); }),
      el('span', {}, ' ← '), pick(head, head, (v) => { head = v; refresh(); }));

    const f = el('form', { class: 'stack', onsubmit: async (e) => {
      e.preventDefault();
      try {
        const res = await api.post(`${P(slug, repoSlug)}/`, {
          title: titleInput.value, body: bodyInput.value, baseBranch: base, headBranch: head, isDraft: draftCheck.checked,
        });
        navigate(`/o/${slug}/${repoSlug}/pulls/${res.number}`);
      } catch (err) { errorToast(err); }
    }},
      el('label', { class: 'field' }, el('span', { text: 'Title' }), titleInput),
      el('label', { class: 'field' }, el('span', { text: 'Description' }), bodyInput),
      el('label', { class: 'row' }, draftCheck, el('span', {}, ' Create as draft')),
      el('button', { class: 'primary', type: 'submit' }, 'Create pull request'));

    refresh();
    return shell(b, null, 'pulls', el('div', { class: 'stack' }, bar, previewHost, f));
  });
}

function comparePreview(cmp) {
  return el('div', { class: 'card' },
    el('div', { class: cmp.mergeable ? 'good-note' : 'bad-note' },
      cmp.ahead === 0 ? 'Nothing to compare.' :
        cmp.mergeable ? `Able to merge. ${cmp.ahead} commit(s), ${cmp.files.length} file(s) changed.`
          : `Can't merge automatically — conflicts in ${cmp.conflictPaths.join(', ')}.`),
    el('div', { class: 'diffstat' },
      el('span', { class: 'add' }, `+${cmp.totalAdded}`), ' ', el('span', { class: 'del' }, `−${cmp.totalDeleted}`)));
}

// ---- detail ----

export function renderPullDetail(slug, repoSlug, number) {
  const state = { tab: 'conversation' };
  return asyncView(async () => build());

  async function build() {
    const res = await api.get(`${P(slug, repoSlug)}/${number}`);
    const d = res.detail;
    const checks = res.checks || [];
    const myPerm = res.myPermission;
    const canWrite = ['write', 'maintain', 'admin'].includes(myPerm);
    const canComment = canWrite || myPerm === 'triage';
    const b = { orgSlug: slug, repoSlug, defaultBranch: d.baseBranch, myPermission: myPerm };

    const host = el('div', { class: 'stack' });

    function rebuild() { host.replaceChildren(header(), tabbar(), section()); }

    function header() {
      return el('div', {},
        el('h1', {}, d.title, el('span', { class: 'muted' }, ` #${d.number}`)),
        el('div', { class: 'pr-substate' },
          el('span', { class: `pill state-${d.state}` }, d.state),
          d.isDraft && el('span', { class: 'pill' }, 'draft'),
          el('span', { class: 'muted' }, ` ${d.authorUsername} wants to merge `),
          el('code', {}, d.headBranch), ' → ', el('code', {}, d.baseBranch),
          canWrite && d.state === 'open' && el('button', { class: 'small', onclick: async () => {
            await api.post(`${P(slug, repoSlug)}/${number}/state`, { state: 'closed' }); refresh();
          } }, 'Close'),
          canWrite && d.state === 'closed' && el('button', { class: 'small', onclick: async () => {
            await api.post(`${P(slug, repoSlug)}/${number}/state`, { state: 'open' }); refresh();
          } }, 'Reopen')));
    }

    function tabbar() {
      const t = (id, label) => el('a', { href: '#', class: 'tab' + (state.tab === id ? ' active' : ''),
        onclick: (e) => { e.preventDefault(); state.tab = id; rebuild(); } }, label);
      const fileCount = d.compare?.files.length ?? 0;
      const commitCount = d.compare?.commits.length ?? 0;
      return el('nav', { class: 'sub-nav' },
        t('conversation', 'Conversation'), t('commits', `Commits ${commitCount}`), t('files', `Files ${fileCount}`));
    }

    function section() {
      if (state.tab === 'commits') return commitsSection();
      if (state.tab === 'files') return filesSection();
      return conversationSection();
    }

    function conversationSection() {
      const wrap = el('div', { class: 'stack' });
      if (d.body) wrap.append(el('div', { class: 'card' }, el('div', { class: 'muted' }, `${d.authorUsername} · ${timeAgo(d.createdAt)}`), el('p', {}, d.body)));

      // interleave conversation comments + reviews by time
      const events = [
        ...d.conversation.map((c) => ({ t: c.createdAt, node: commentCard(c) })),
        ...d.reviews.map((r) => ({ t: r.createdAt, node: reviewCard(r) })),
      ].sort((a, z) => new Date(a.t) - new Date(z.t));
      for (const e of events) wrap.append(e.node);

      if (canComment && d.state === 'open') {
        wrap.append(el('div', { class: 'card' }, commentForm(null, 'Comment')));
        wrap.append(reviewBar());
      }
      wrap.append(mergeBox());
      return wrap;
    }

    function commitsSection() {
      const cs = d.compare?.commits ?? [];
      return el('div', { class: 'card nopad' }, ...cs.map((c) => el('div', { class: 'commit-row' },
        el('a', { href: `#/o/${slug}/${repoSlug}/commit/${c.sha}` }, el('strong', {}, c.summary)),
        el('span', { class: 'mono muted' }, c.shortSha))));
    }

    function filesSection() {
      const files = d.compare?.files ?? [];
      const threadsByFile = {};
      for (const th of d.threads) (threadsByFile[th.filePath] ??= []).push(th);

      return el('div', { class: 'stack' }, ...files.map((f) => {
        const box = el('div', { class: 'card nopad diff-file' });
        box.append(el('div', { class: 'diff-head mono' }, f.oldPath ? `${f.oldPath} → ${f.path}` : f.path,
          el('span', { class: 'add' }, ` +${f.added}`), el('span', { class: 'del' }, ` −${f.deleted}`)));
        if (f.isBinary || !f.patch) { box.append(el('div', { class: 'diff-binary muted' }, 'No textual diff')); return box; }

        let newLine = 0, oldLine = 0;
        for (const raw of f.patch.split('\n')) {
          let cls = 'ctx', lno = '';
          if (raw.startsWith('@@')) {
            const m = raw.match(/\+(\d+)/); newLine = m ? +m[1] - 1 : newLine;
            const mo = raw.match(/-(\d+)/); oldLine = mo ? +mo[1] - 1 : oldLine;
            cls = 'hunk';
          } else if (raw.startsWith('+') && !raw.startsWith('+++')) { cls = 'add'; newLine++; lno = newLine; }
          else if (raw.startsWith('-') && !raw.startsWith('---')) { cls = 'del'; oldLine++; }
          else if (raw.startsWith('diff ') || raw.startsWith('index ') || raw.startsWith('+++') || raw.startsWith('---')) cls = 'meta';
          else { newLine++; oldLine++; lno = newLine; }

          const row = el('div', { class: `pl ${cls}`, 'data-line': lno || '' });
          row.textContent = raw || ' ';
          if (canComment && d.state === 'open' && (cls === 'add' || cls === 'ctx') && lno) {
            row.classList.add('commentable');
            row.addEventListener('click', () => openInlineForm(box, f.path, lno));
          }
          box.append(row);

          for (const th of (threadsByFile[f.path] || []).filter((t) => t.line === lno && lno)) {
            box.append(threadCard(th));
          }
        }
        // file-level threads (line == null)
        for (const th of (threadsByFile[f.path] || []).filter((t) => t.line == null)) box.append(threadCard(th));
        return box;
      }));
    }

    function openInlineForm(container, path, line) {
      if (container.querySelector('.inline-form')) return;
      const holder = el('div', { class: 'inline-form card' });
      holder.append(commentForm(null, 'Add review comment', { filePath: path, line, onDone: () => refresh() }, () => holder.remove()));
      container.append(holder);
    }

    function threadCard(th) {
      const card = el('div', { class: 'thread card' + (th.isResolved ? ' resolved' : '') });
      card.append(el('div', { class: 'thread-loc muted mono' }, `${th.filePath}${th.line ? ':' + th.line : ''}`,
        th.isResolved && el('span', { class: 'pill' }, `resolved${th.resolvedByUsername ? ' by ' + th.resolvedByUsername : ''}`)));
      for (const c of th.comments) card.append(commentCard(c));
      if (canComment && d.state === 'open') {
        card.append(commentForm(th.id, 'Reply', { onDone: () => refresh() }));
        card.append(el('button', { class: 'small', onclick: async () => {
          await api.post(`${P(slug, repoSlug)}/${number}/threads/${th.id}/${th.isResolved ? 'unresolve' : 'resolve'}`);
          refresh();
        } }, th.isResolved ? 'Unresolve' : 'Resolve'));
      }
      return card;
    }

    function commentCard(c) {
      const card = el('div', { class: 'comment' + (c.isPending ? ' pending' : '') },
        el('div', { class: 'muted' }, `${c.authorUsername} · ${timeAgo(c.createdAt)}`, c.isPending && ' · pending'),
        el('p', {}, c.body));
      if (c.authorUsername === session.user?.username && d.state === 'open') {
        card.append(el('div', { class: 'row' },
          el('button', { class: 'small', onclick: () => {
            const ta = el('textarea', { rows: 3 }); ta.value = c.body;
            card.replaceChildren(ta, el('button', { class: 'small primary', onclick: async () => {
              await api.patch(`${P(slug, repoSlug)}/${number}/comments/${c.id}`, { body: ta.value }); refresh();
            } }, 'Save'));
          } }, 'Edit'),
          el('button', { class: 'small danger', onclick: async () => {
            if (!await confirmDialog('Delete this comment?')) return;
            await api.del(`${P(slug, repoSlug)}/${number}/comments/${c.id}`); refresh();
          } }, 'Delete')));
      }
      return card;
    }

    function reviewCard(r) {
      const label = { approve: 'approved', request_changes: 'requested changes', comment: 'reviewed' }[r.state];
      return el('div', { class: `card review-event review-${r.state}` },
        el('strong', {}, r.authorUsername), ` ${label} `, el('span', { class: 'muted' }, timeAgo(r.createdAt)),
        r.body && el('p', {}, r.body));
    }

    function commentForm(threadId, label, opts = {}, onCancel) {
      const ta = el('textarea', { rows: threadId ? 2 : 4, placeholder: label });
      const submit = async (pending) => {
        if (!ta.value.trim()) return;
        await api.post(`${P(slug, repoSlug)}/${number}/comments`, {
          body: ta.value, threadId, filePath: opts.filePath, line: opts.line, side: 'new', pending,
        });
        (opts.onDone || refresh)();
      };
      const buttons = el('div', { class: 'row' },
        el('button', { class: 'small primary', onclick: () => submit(false).catch(errorToast) }, label));
      if (opts.filePath) buttons.append(el('button', { class: 'small', onclick: () => submit(true).catch(errorToast) }, 'Start a review'));
      if (onCancel) buttons.append(el('button', { class: 'small', onclick: onCancel }, 'Cancel'));
      return el('div', { class: 'comment-form' }, ta, buttons);
    }

    function reviewBar() {
      if (d.authorUsername === session.user?.username) return null;
      const ta = el('textarea', { rows: 2, placeholder: 'Review summary (optional)' });
      const send = (st) => api.post(`${P(slug, repoSlug)}/${number}/reviews`, { state: st, body: ta.value })
        .then(refresh).catch(errorToast);
      return el('div', { class: 'card review-bar' }, el('strong', {}, 'Finish your review'), ta,
        el('div', { class: 'row' },
          el('button', { class: 'small', onclick: () => send('comment') }, 'Comment'),
          el('button', { class: 'small good', onclick: () => send('approve') }, 'Approve'),
          el('button', { class: 'small danger', onclick: () => send('request_changes') }, 'Request changes')));
    }

    function mergeBox() {
      if (d.state === 'merged') return el('div', { class: 'card good-note' }, `Merged by ${d.mergedByUsername} · ${d.mergeMethod} · ${timeAgo(d.mergedAt)}`);
      if (d.state === 'closed') return el('div', { class: 'card muted' }, 'This pull request is closed.');
      const m = d.merge;
      const box = el('div', { class: 'card merge-box' });
      if (checks.length) {
        box.append(el('div', { class: 'checks-list' }, ...checks.map((c) => el('div', { class: 'check-row' },
          el('span', { class: `pill ${c.state === 'success' ? 'ok' : c.state === 'pending' ? 'pending' : 'bad'}` }, c.state),
          ` ${c.context}`, c.description && el('span', { class: 'muted' }, ` — ${c.description}`)))));
      }
      const notes = [];
      if (m.blockedByDraft) notes.push('Marked as a draft.');
      if (m.hasConflicts) notes.push(`Conflicts in ${m.conflictPaths.join(', ')}.`);
      if (m.blockedByReview) notes.push('Changes requested.');
      if (m.approvals) notes.push(`${m.approvals} approval(s).`);
      box.append(el('div', { class: m.mergeable ? 'good-note' : 'bad-note' }, notes.join(' ') || (m.mergeable ? 'Ready to merge.' : 'Not mergeable.')));

      if (canWrite) {
        const methods = [
          m.allowMerge && ['merge', 'Merge commit'],
          m.allowSquash && ['squash', 'Squash and merge'],
          m.allowRebase && ['rebase', 'Rebase and merge'],
        ].filter(Boolean);
        const sel = el('select', {}, ...methods.map(([v, l]) => el('option', { value: v }, l)));
        box.append(el('div', { class: 'row' }, sel,
          el('button', { class: 'primary', disabled: !m.mergeable, onclick: async () => {
            try { await api.post(`${P(slug, repoSlug)}/${number}/merge`, { method: sel.value }); toast('Merged.', 'ok'); refresh(); }
            catch (err) { errorToast(err); }
          } }, 'Merge pull request')));
      }
      if (d.isDraft && (canWrite || d.authorUsername === session.user?.username)) {
        box.append(el('button', { class: 'small', onclick: async () => {
          await api.patch(`${P(slug, repoSlug)}/${number}`, { isDraft: false }); refresh();
        } }, 'Mark ready for review'));
      }
      return box;
    }

    async function refresh() {
      const fresh = await renderPullDetail(slug, repoSlug, number);
      document.getElementById('view').replaceChildren(fresh);
    }

    rebuild();
    return shell(b, null, 'pulls', host);
  }
}
