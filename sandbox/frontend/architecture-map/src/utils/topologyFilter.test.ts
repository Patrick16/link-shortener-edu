import { describe, expect, it } from 'vitest'
import { filterArchitecture, isActive } from './topologyFilter'
import type { ArchitectureData } from '../types/architecture'
import type { InfraStatus } from '../types/controlApi'

function status(overrides: Partial<InfraStatus> = {}): InfraStatus {
  return { nginxBypassed: false, pgcatEnabled: true, cacheEnabled: true, messagingMode: 'rabbitmq', ...overrides }
}

describe('isActive', () => {
  it('is always active when activeWhen is absent', () => {
    expect(isActive(undefined, status())).toBe(true)
  })

  it('is always active when status has not loaded yet (null)', () => {
    expect(isActive({ cacheEnabled: false }, null)).toBe(true)
  })

  it('matches a single AND group', () => {
    expect(isActive({ cacheEnabled: true }, status())).toBe(true)
    expect(isActive({ cacheEnabled: false }, status())).toBe(false)
  })

  it('requires every key in a group to match', () => {
    expect(isActive({ cacheEnabled: true, pgcatEnabled: false }, status())).toBe(false)
  })

  it('matches an OR of groups if any one group matches (nginx dual-role case)', () => {
    const activeWhen = [{ nginxBypassed: false }, { messagingMode: 'grpc' as const }]
    expect(isActive(activeWhen, status({ nginxBypassed: true, messagingMode: 'rabbitmq' }))).toBe(false)
    expect(isActive(activeWhen, status({ nginxBypassed: true, messagingMode: 'grpc' }))).toBe(true)
    expect(isActive(activeWhen, status({ nginxBypassed: false, messagingMode: 'rabbitmq' }))).toBe(true)
  })
})

const data: ArchitectureData = {
  components: [
    { id: 'a', name: 'A', type: 'service', icon: '', description: '', details: { purpose: '', technologies: [] } },
    {
      id: 'b',
      name: 'B',
      type: 'infrastructure',
      icon: '',
      description: '',
      activeWhen: { cacheEnabled: true },
      details: { purpose: '', technologies: [] },
    },
    { id: 'c', name: 'C', type: 'database', icon: '', description: '', details: { purpose: '', technologies: [] } },
  ],
  connections: [
    { from: 'a', to: 'b', label: 'always-tagged edge into a toggled node', protocol: 'x', synchronous: true, activeWhen: { cacheEnabled: true } },
    { from: 'b', to: 'c', label: 'untagged edge whose endpoint gets hidden', protocol: 'x', synchronous: true },
    { from: 'a', to: 'c', label: 'always active', protocol: 'x', synchronous: true },
  ],
}

describe('filterArchitecture', () => {
  it('keeps everything visible before InfraStatus has loaded', () => {
    const visible = filterArchitecture(data, null)
    expect(visible.components.map((c) => c.id)).toEqual(['a', 'b', 'c'])
    expect(visible.connections).toHaveLength(3)
  })

  it('hides a toggled-off component and both its tagged and untagged edges (endpoint backstop)', () => {
    const visible = filterArchitecture(data, status({ cacheEnabled: false }))
    expect(visible.components.map((c) => c.id)).toEqual(['a', 'c'])
    expect(visible.connections.map((c) => `${c.from}->${c.to}`)).toEqual(['a->c'])
  })

  it('shows the toggled node and its edges when the condition matches', () => {
    const visible = filterArchitecture(data, status({ cacheEnabled: true }))
    expect(visible.components.map((c) => c.id)).toEqual(['a', 'b', 'c'])
    expect(visible.connections.map((c) => `${c.from}->${c.to}`)).toEqual(['a->b', 'b->c', 'a->c'])
  })

  it('preserves each surviving connection original index into the unfiltered array', () => {
    const visible = filterArchitecture(data, status({ cacheEnabled: false }))
    expect(visible.connections).toEqual([expect.objectContaining({ from: 'a', to: 'c', originalIndex: 2 })])
  })
})
