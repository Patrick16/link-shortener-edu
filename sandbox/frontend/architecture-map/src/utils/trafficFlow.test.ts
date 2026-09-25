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

  it('follows a live Redis failover - flags the promoted replica instead of the demoted master', () => {
    const roles = { 'redis-master': 'unreachable', 'redis-replica1': 'master', 'redis-replica2': 'replica' }

    expect(isTrafficFlowEdge('redirect-api', 'redis-replica1', roles)).toBe(true)
    expect(isTrafficFlowEdge('redirect-api', 'redis-master', roles)).toBe(false)
  })

  it('follows a live Mongo primary election the same way', () => {
    const roles = { mongo1: 'unreachable', mongo2: 'primary', mongo3: 'secondary' }

    expect(isTrafficFlowEdge('traffic-service', 'mongo2', roles)).toBe(true)
    expect(isTrafficFlowEdge('traffic-service', 'mongo1', roles)).toBe(false)
  })

  it('falls back to the default leader id when no role data is available yet', () => {
    expect(isTrafficFlowEdge('redirect-api', 'redis-master', {})).toBe(true)
    expect(isTrafficFlowEdge('traffic-service', 'mongo1', {})).toBe(true)
  })
})
