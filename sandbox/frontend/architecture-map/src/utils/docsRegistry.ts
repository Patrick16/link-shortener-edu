// Node/pattern docs live outside this project (sandbox/docs/scenarios/{nodes,patterns}/*.md) so
// they sit next to the other scenario docs (see docs/README.md), not buried inside one frontend
// app. import.meta.glob pulls them in as plain strings at build time - no runtime fetch, no
// control-api involvement (see .notes/documentation-plan.md: static import was chosen deliberately
// over serving markdown through an API, since this is a local-only project with no separately
// hosted frontend).
//
// Lookup is by id alone (the markdown filename, e.g. `redis.md` -> id `redis`) - not by an entry
// in architecture.json. A node doc existing or not is exactly what its file's presence says; there
// is no second place (architecture.json's `details.links`) that also has to agree, which is the
// kind of two-places-to-keep-in-sync problem the rest of this doc effort has been trying to avoid.
const nodeDocs = import.meta.glob('../../../../docs/scenarios/nodes/*.md', {
  eager: true,
  query: '?raw',
  import: 'default',
}) as Record<string, string>

const patternDocs = import.meta.glob('../../../../docs/scenarios/patterns/*.md', {
  eager: true,
  query: '?raw',
  import: 'default',
}) as Record<string, string>

// One file per pitfall, not inlined into the node/pattern doc that found it - a single node can
// accumulate a dozen real bugs over time (review-reports/ is already past 150 findings across the
// whole project), and a bug is often relevant to more than one node (see how both cache pitfalls
// below list LinkApi/RedirectApi/Redis under Relatives -> Nodes) - same reasoning as pattern docs
// existing so a shared mechanism isn't written twice.
const pitfallDocs = import.meta.glob('../../../../docs/scenarios/pitfalls/*.md', {
  eager: true,
  query: '?raw',
  import: 'default',
}) as Record<string, string>

function idFromPath(path: string): string {
  const file = path.split('/').pop() ?? path
  return file.replace(/\.md$/, '')
}

const nodeDocsById = new Map(Object.entries(nodeDocs).map(([path, raw]) => [idFromPath(path), raw]))
const patternDocsById = new Map(Object.entries(patternDocs).map(([path, raw]) => [idFromPath(path), raw]))
const pitfallDocsById = new Map(Object.entries(pitfallDocs).map(([path, raw]) => [idFromPath(path), raw]))

export function getNodeDoc(id: string): string | undefined {
  return nodeDocsById.get(id)
}

export function getPatternDoc(id: string): string | undefined {
  return patternDocsById.get(id)
}

export function getPitfallDoc(id: string): string | undefined {
  return pitfallDocsById.get(id)
}
