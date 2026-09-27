import { describe, expect, it } from 'vitest'
import type { ArchComponent, ArchConnection } from '../types/architecture'
import { computeLayout, NODE_HEIGHT, NODE_WIDTH } from './layoutGraph'

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

    const layout = computeLayout(components, [])

    for (const c of components) {
      expect(layout[c.id]).toBeDefined()
      expect(Number.isFinite(layout[c.id].x)).toBe(true)
      expect(Number.isFinite(layout[c.id].y)).toBe(true)
    }
  })

  it('lays out every component reachable through connections, not just directly-connected ones', () => {
    const components = [component('a'), component('b'), component('c')]
    const connections = [connection('a', 'b'), connection('b', 'c')]

    const layout = computeLayout(components, connections)

    expect(Object.keys(layout).sort()).toEqual(['a', 'b', 'c'])
  })

  it('places a downstream node to the right of its upstream node (rankdir LR)', () => {
    const components = [component('a'), component('b')]
    const connections = [connection('a', 'b')]

    const layout = computeLayout(components, connections)

    expect(layout.b.x).toBeGreaterThan(layout.a.x)
  })

  it('converts dagre center positions to React Flow top-left positions using NODE_WIDTH/NODE_HEIGHT', () => {
    // The direct check for this finding: layoutGraph.ts's conversion (node.x - NODE_WIDTH / 2) must
    // use the same constants Diagram.tsx renders nodes at - previously duplicated as bare literals
    // in both files with nothing enforcing they matched.
    const components = [component('solo')]

    const layout = computeLayout(components, [])

    // A single node with no connections is centered by dagre at (marginx + width/2, marginy + height/2).
    expect(layout.solo.x).toBeCloseTo(20, 0)
    expect(layout.solo.y).toBeCloseTo(20, 0)
    expect(NODE_WIDTH).toBeGreaterThan(0)
    expect(NODE_HEIGHT).toBeGreaterThan(0)
  })

  it('returns an empty layout for no components', () => {
    expect(computeLayout([], [])).toEqual({})
  })

  it('does not throw when a connection references an unknown component id', () => {
    const components = [component('a')]
    const connections = [connection('a', 'does-not-exist')]

    expect(() => computeLayout(components, connections)).not.toThrow()
  })
})
