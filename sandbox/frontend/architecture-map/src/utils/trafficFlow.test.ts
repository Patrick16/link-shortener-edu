import { describe, expect, it } from 'vitest'
import { isTrafficFlowEdge, TRAFFIC_FLOW_EDGES } from './trafficFlow'

describe('isTrafficFlowEdge', () => {
  it('returns true for every edge in the known traffic path', () => {
    for (const [from, to] of TRAFFIC_FLOW_EDGES) {
      expect(isTrafficFlowEdge(from, to)).toBe(true)
    }
  })

  it('returns false for an edge not on the traffic path', () => {
    expect(isTrafficFlowEdge('link-api', 'redirect-api')).toBe(false)
  })

  it('is directional - reversing a known edge is not a match', () => {
    expect(isTrafficFlowEdge('nginx', 'k6')).toBe(false)
  })

  it('returns false for unrelated node names', () => {
    expect(isTrafficFlowEdge('foo', 'bar')).toBe(false)
  })
})
