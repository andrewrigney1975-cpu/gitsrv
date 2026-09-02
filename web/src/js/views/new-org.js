import { api } from '../api.js';
import { session } from '../session.js';
import { el, form, toast } from '../ui.js';
import { navigate } from '../router.js';
import { suggestSlug, orgSlugCheck } from '../features/slug.js';

export function renderNewOrg() {
  const wrap = el('div', { class: 'card narrow' });
  wrap.append(el('h1', {}, 'New organisation'));
  wrap.append(form({
    fields: [
      { name: 'name', label: 'Name', required: true },
      { name: 'slug', label: 'URL slug', required: true, hint: 'lowercase letters, digits, single - or _',
        deriveFrom: 'name', derive: suggestSlug, check: orgSlugCheck() },
      { name: 'description', label: 'Description' },
    ],
    submitLabel: 'Create organisation',
    onSubmit: async (v) => {
      const org = await api.post('/api/orgs/', v);
      await session.refresh();
      toast(`Created ${org.name}.`, 'ok');
      navigate(`/o/${org.slug}`);
    },
  }));
  return wrap;
}
