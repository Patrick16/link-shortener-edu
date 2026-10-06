import { describe, expect, it } from 'vitest'
import architectureData from '../data/architecture.json'
import type { ArchComponent, ArchConnection, ArchitectureData } from '../types/architecture'
import { computeLayout, edgeKey, NODE_HEIGHT, NODE_WIDTH } from './layoutGraph'

function component(id: string): ArchComponent {
  return {
    id,
    name: id,
    type: 'service',
    icon: 'box',
    description: '',
    details: { purpose: '', technologies: [] },
  }
}

function connection(from: string, to: string): ArchConnection {
  return { from, to, label: '', protocol: 'http', synchronous: true }
}

describe('computeLayout', () => {
  it('returns a finite {x, y} position for every component, even with no connections', () => {
    const components = [component('a'), component('b'), component('c')]

    const { positions } = computeLayout(components, [])

    for (const c of components) {
      expect(positions[c.id]).toBeDefined()
      expect(Number.isFinite(positions[c.id].x)).toBe(true)
      expect(Number.isFinite(positions[c.id].y)).toBe(true)
    }
  })

  it('lays out every component reachable through connections, not just directly-connected ones', () => {
    const components = [component('a'), component('b'), component('c')]
    const connections = [connection('a', 'b'), connection('b', 'c')]

    const { positions } = computeLayout(components, connections)

    expect(Object.keys(positions).sort()).toEqual(['a', 'b', 'c'])
  })

  it('places a downstream node to the right of its upstream node (rankdir LR)', () => {
    const components = [component('a'), component('b')]
    const connections = [connection('a', 'b')]

    const { positions } = computeLayout(components, connections)

    expect(positions.b.x).toBeGreaterThan(positions.a.x)
  })

  it('converts dagre center positions to React Flow top-left positions using NODE_WIDTH/NODE_HEIGHT', () => {
    // The direct check for this finding: layoutGraph.ts's conversion (node.x - NODE_WIDTH / 2) must
    // use the same constants Diagram.tsx renders nodes at - previously duplicated as bare literals
    // in both files with nothing enforcing they matched. None of these ids (made up, not real
    // architecture.json components) have a region, so this is also an implicit check that an
    // unregioned component still gets laid out fine on its own.
    const components = [component('solo')]

    const { positions } = computeLayout(components, [])

    // A single node with no connections is centered by dagre at (marginx + width/2, marginy + height/2).
    expect(positions.solo.x).toBeCloseTo(20, 0)
    expect(positions.solo.y).toBeCloseTo(20, 0)
    expect(NODE_WIDTH).toBeGreaterThan(0)
    expect(NODE_HEIGHT).toBeGreaterThan(0)
  })

  it('returns an empty layout for no components', () => {
    expect(computeLayout([], [])).toEqual({ positions: {}, regions: [], edgePaths: {} })
  })

  it('does not throw when a connection references an unknown component id', () => {
    const components = [component('a')]
    const connections = [connection('a', 'does-not-exist')]

    expect(() => computeLayout(components, connections)).not.toThrow()
  })

  it('groups real architecture.json component ids into a region box that contains them', () => {
    // Real ids (see utils/regions.ts) - 'a'/'b'/'c' above are intentionally unregioned to prove a
    // component works without one; these are the inverse case; 'nginx' and 'link-api' are deliberately
    // in different regions (region-lb vs region-api) to also prove two different boxes come out.
    const components = [component('nginx'), component('link-api')]
    const connections = [connection('nginx', 'link-api')]

    const { positions, regions } = computeLayout(components, connections)

    const lb = regions.find((r) => r.id === 'region-lb')
    const api = regions.find((r) => r.id === 'region-api')
    expect(lb).toBeDefined()
    expect(api).toBeDefined()
    expect(lb!.id).not.toBe(api!.id)

    // nginx's node box must fall entirely within its own region's bounding box.
    const nginxPos = positions.nginx
    expect(nginxPos.x).toBeGreaterThanOrEqual(lb!.x)
    expect(nginxPos.y).toBeGreaterThanOrEqual(lb!.y)
    expect(nginxPos.x + NODE_WIDTH).toBeLessThanOrEqual(lb!.x + lb!.width)
    expect(nginxPos.y + NODE_HEIGHT).toBeLessThanOrEqual(lb!.y + lb!.height)
  })

  it('omits a region with no currently-visible members instead of rendering an empty box', () => {
    // Neither component below belongs to region-cache (see utils/regions.ts) - if the topology
    // filter hid every cache node, no 'region-cache' box should be emitted at all.
    const components = [component('nginx'), component('link-api')]

    const { regions } = computeLayout(components, [])

    expect(regions.some((r) => r.id === 'region-cache')).toBe(false)
  })

  it('never lets two region boxes overlap, even for the full real architecture.json graph', () => {
    // Regression test for a real bug: region padding/label-height once exceeded nodesep, so two
    // regions stacked in the same rank band (e.g. Cache above API) overlapped onscreen.
    const data = architectureData as unknown as ArchitectureData
    const { regions } = computeLayout(data.components, data.connections)

    for (let i = 0; i < regions.length; i++) {
      for (let j = i + 1; j < regions.length; j++) {
        const a = regions[i]
        const b = regions[j]
        const overlaps = a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y
        expect(overlaps, `${a.id} should not overlap ${b.id}`).toBe(false)
      }
    }
  })

  it('spreads edges that converge on the same target across distinct lanes, within a region', () => {
    // Regression test for a real bug: several edges fanning into one node (redis-master's three
    // Sentinels, all in region-cache - see utils/regions.ts) used to collapse into one
    // indistinguishable bundle because React Flow's built-in step routing only ever looks at one
    // edge's own two endpoints - dagre's own interior routing points (what edgePaths exposes) are
    // what give parallel edges through the same column gap distinct x "lanes" instead. Real ids
    // matter here, not arbitrary ones - lane separation only applies within a single region/group
    // (see computeLayout's own comment on why), and only real architecture.json ids resolve to one.
    const components = [component('redis-sentinel-1'), component('redis-sentinel-2'), component('redis-sentinel-3'), component('redis-master')]
    const connections = [
      connection('redis-sentinel-1', 'redis-master'),
      connection('redis-sentinel-2', 'redis-master'),
      connection('redis-sentinel-3', 'redis-master'),
    ]

    const { edgePaths } = computeLayout(components, connections)

    const paths = ['redis-sentinel-1', 'redis-sentinel-2', 'redis-sentinel-3'].map((id) => edgePaths[edgeKey(id, 'redis-master')])
    for (const path of paths) expect(path.length).toBeGreaterThan(0)
    // At least two of the three converging edges must take a visibly different route approaching
    // redis-master (not just a different point far from it, back near their distinct sources,
    // which would trivially always differ) - otherwise they'd still draw on top of each other for
    // the shared stretch right where they actually converge.
    const approachXs = new Set(paths.map((p) => Math.round(p[p.length - 1].x)))
    expect(approachXs.size).toBeGreaterThan(1)
  })

  it('does not lane-separate a cross-region edge (no shared group to route it through)', () => {
    const components = [component('nginx'), component('link-api')]
    const connections = [connection('nginx', 'link-api')]

    const { edgePaths } = computeLayout(components, connections)

    expect(edgePaths[edgeKey('nginx', 'link-api')]).toEqual([])
  })
})
