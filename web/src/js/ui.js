// Tiny DOM + feedback helpers shared by every view. No framework — just enough to keep views terse.

export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === 'class') node.className = v;
    else if (k === 'html') node.innerHTML = v;
    else if (k === 'text') node.textContent = v;
    else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v);
    else if (k === 'value') node.value = v;
    else node.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return node;
}

export function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild);
  return node;
}

let toastHost;
export function toast(message, kind = 'info') {
  toastHost ??= document.body.appendChild(el('div', { class: 'toast-host' }));
  const t = el('div', { class: `toast ${kind}`, text: message });
  toastHost.append(t);
  setTimeout(() => t.classList.add('leaving'), 3200);
  setTimeout(() => t.remove(), 3600);
}

export function errorToast(err) {
  toast(err?.message || 'Something went wrong.', 'bad');
}

// Minimal form builder: fields -> {name, label, type?, value?, required?, hint?}
// Field options:
//   name, label, type, value, required, autocomplete, placeholder, hint, options (select)
//   deriveFrom: 'otherField'  — auto-fill from another field until the user edits this one
//   derive: (value) => string — transform applied to the derived value (default: identity)
//   check: async (value) => ({ ok, message }) | null — debounced live validation, shown inline
export function form({ fields, submitLabel, onSubmit }) {
  const controls = {};
  const values = () => Object.fromEntries(Object.entries(controls).map(([k, c]) => [k, c.value]));

  const f = el('form', { class: 'stack', onsubmit: async (e) => {
    e.preventDefault();
    const btn = f.querySelector('button[type=submit]');
    btn.disabled = true;
    try {
      await onSubmit(values());
    } catch (err) {
      errorToast(err);
    } finally {
      btn.disabled = false;
    }
  }});

  for (const field of fields) {
    if (field.type === 'select') {
      const sel = el('select', {}, ...field.options.map((o) =>
        el('option', { value: o.value, selected: o.value === field.value }, o.label)));
      controls[field.name] = sel;
      f.append(el('label', { class: 'field' }, el('span', { text: field.label }), sel,
        field.hint && el('small', { text: field.hint })));
      continue;
    }

    const input = el('input', {
      type: field.type || 'text',
      value: field.value || '',
      required: field.required,
      autocomplete: field.autocomplete || 'off',
      placeholder: field.placeholder || '',
    });
    controls[field.name] = input;

    const status = el('small', { class: 'field-status' });
    f.append(el('label', { class: 'field' }, el('span', { text: field.label }), input,
      field.hint && el('small', { text: field.hint }), field.check ? status : null));

    // live check (debounced)
    if (field.check) {
      let timer;
      const runCheck = () => {
        clearTimeout(timer);
        const v = input.value.trim();
        status.textContent = '';
        status.className = 'field-status';
        if (!v) return;
        timer = setTimeout(async () => {
          try {
            const res = await field.check(v, values());
            if (!res) return;
            status.textContent = res.message || (res.ok ? 'Available' : 'Not available');
            status.className = 'field-status ' + (res.ok ? 'ok' : 'bad');
          } catch { /* ignore transient check errors */ }
        }, 350);
      };
      input.addEventListener('input', runCheck);
    }
  }

  // deriveFrom wiring (after all controls exist)
  for (const field of fields) {
    if (!field.deriveFrom || !controls[field.deriveFrom] || !controls[field.name]) continue;
    const src = controls[field.deriveFrom];
    const dst = controls[field.name];
    const xform = field.derive || ((x) => x);
    let userEdited = !!dst.value;
    let syntheticWrite = false;
    dst.addEventListener('input', () => { if (!syntheticWrite) userEdited = true; });
    src.addEventListener('input', () => {
      if (userEdited) return;
      dst.value = xform(src.value);
      syntheticWrite = true;
      dst.dispatchEvent(new Event('input'));   // trigger the derived field's own check
      syntheticWrite = false;
    });
  }

  f.append(el('button', { type: 'submit', class: 'primary' }, submitLabel));
  return f;
}

export function confirmDialog(message) {
  return new Promise((resolve) => {
    const backdrop = el('div', { class: 'modal-backdrop' });
    const cancel = () => { backdrop.remove(); resolve(false); };
    const ok = () => { backdrop.remove(); resolve(true); };
    backdrop.append(el('div', { class: 'modal' },
      el('p', { text: message }),
      el('div', { class: 'row end' },
        el('button', { onclick: cancel }, 'Cancel'),
        el('button', { class: 'danger', onclick: ok }, 'Confirm'))));
    backdrop.addEventListener('click', (e) => { if (e.target === backdrop) cancel(); });
    document.body.append(backdrop);
  });
}

export function timeAgo(iso) {
  const s = Math.floor((Date.now() - new Date(iso)) / 1000);
  const units = [[31536000, 'y'], [2592000, 'mo'], [86400, 'd'], [3600, 'h'], [60, 'm']];
  for (const [secs, label] of units) if (s >= secs) return `${Math.floor(s / secs)}${label} ago`;
  return 'just now';
}
