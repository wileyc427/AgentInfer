import starlight from '@astrojs/starlight';
import { defineConfig } from 'astro/config';
import starlightBlog from 'starlight-blog';

const repository = 'https://github.com/wileyc427/AgentInfer';

// GitHub Pages for this repository, which is what exists today. Moving to a
// domain is two lines — `site` becomes it and `base` goes away — plus a CNAME
// in public/. Worth doing before any helpLinkUri is published, because a
// helpLinkUri is compiled into the analyzer and cannot be corrected for the
// versions already carrying it.
const site = 'https://wileyc427.github.io';
const base = '/AgentInfer';

export default defineConfig({
  site,
  base,
  integrations: [
    starlight({
      title: 'AgentInfer',
      description:
        'Object-oriented agents for .NET, with tool schemas, permissions and '
        + 'reply binding checked at build time.',
      social: [{ icon: 'github', label: 'GitHub', href: repository }],
      editLink: { baseUrl: `${repository}/edit/main/` },
      plugins: [starlightBlog({ title: 'Notes' })],
      sidebar: [
        {
          label: 'Reference',
          // Generated from docs/ by scripts/sync-docs.mjs, so the sidebar is
          // whatever that directory holds rather than a list to maintain.
          items: [{ autogenerate: { directory: 'reference' } }],
        },
      ],
    }),
  ],
});
