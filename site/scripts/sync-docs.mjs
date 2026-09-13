// Copies ../docs/*.md into the Starlight content collection.
//
// A copy at build time rather than a loader pointed at ../docs, and rather than
// frontmatter added to the files themselves. docs/*.md is read on GitHub far
// more than here, and YAML frontmatter renders there as a table above every
// page; stripping the `# ` heading to avoid a duplicate title would leave those
// files untitled where most people read them.
//
// The copy runs on every build, so the two cannot drift. src/content/docs/
// reference/ is generated and ignored by git.
import { readdir, readFile, writeFile, mkdir, rm } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, '..', '..', 'docs');
const target = join(here, '..', 'src', 'content', 'docs', 'reference');

/** The first `# ` line, which is the page's title everywhere else. */
const titleOf = (text) => text.match(/^# (.+)$/m)?.[1];

/**
 * `](tools.md)` and `](tools.md#ain016)` become `](../tools/)`.
 *
 * Relative rather than root-relative on purpose: a root-relative link has to
 * know the site's base path, and this site has one. A sibling link does not.
 */
const relink = (text) =>
  text.replace(/\]\((?!https?:)([\w.-]+)\.md(#[\w-]*)?\)/g, (_, name, anchor) =>
    `](../${name}/${anchor ?? ''})`);

const escape = (value) => value.replace(/"/g, '\\"');

await rm(target, { recursive: true, force: true });
await mkdir(target, { recursive: true });

const files = (await readdir(source)).filter((name) => name.endsWith('.md'));
if (files.length === 0) throw new Error(`No markdown found in ${source}`);

for (const file of files) {
  const raw = await readFile(join(source, file), 'utf8');
  const title = titleOf(raw);

  if (!title) throw new Error(`${file} has no '# ' heading to take a title from`);

  // The heading becomes the frontmatter title, so drop it from the body or
  // every page renders its name twice.
  const body = relink(raw.replace(/^# .+\n+/m, ''));

  await writeFile(
    join(target, file),
    `---\ntitle: "${escape(title)}"\n---\n\n${body}`,
    'utf8');
}

console.log(`sync-docs: ${files.length} page(s) from docs/`);
