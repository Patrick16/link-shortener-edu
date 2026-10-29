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

  it('follows the nginx-bypassed topology - k6 hits link-api/redirect-api directly, not through nginx', () => {
    const status = { nginxBypassed: true, messagingMode: 'rabbitmq' as const, pgcatEnabled: true, haproxyEnabled: true }

    expect(isTrafficFlowEdge('k6', 'link-api', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('k6', 'redirect-api', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('k6', 'nginx', {}, status)).toBe(false)
    expect(isTrafficFlowEdge('nginx', 'link-api', {}, status)).toBe(false)
  })

  it('follows the sync-gRPC messaging topology instead of RabbitMQ', () => {
    const status = { nginxBypassed: false, messagingMode: 'grpc' as const, pgcatEnabled: true, haproxyEnabled: true }

    expect(isTrafficFlowEdge('link-api', 'nginx', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('nginx', 'shortener-service', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('redirect-api', 'nginx', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('nginx', 'traffic-service', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('link-api', 'rabbitmq', {}, status)).toBe(false)
    expect(isTrafficFlowEdge('rabbitmq', 'shortener-service', {}, status)).toBe(false)
  })

  it('follows the haproxy-bypassed topology - DB-facing hops go straight to pgcat', () => {
    const status = { nginxBypassed: false, messagingMode: 'rabbitmq' as const, pgcatEnabled: true, haproxyEnabled: false }

    expect(isTrafficFlowEdge('shortener-service', 'pgcat', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('redirect-api', 'pgcat', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('traffic-service', 'pgcat', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('shortener-service', 'haproxy', {}, status)).toBe(false)
    expect(isTrafficFlowEdge('haproxy', 'pgcat', {}, status)).toBe(false)
  })

  it('follows the pgcat-bypassed topology - DB-facing hops go straight to the database', () => {
    const status = { nginxBypassed: false, messagingMode: 'rabbitmq' as const, pgcatEnabled: false, haproxyEnabled: true }

    expect(isTrafficFlowEdge('shortener-service', 'links-db', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('redirect-api', 'links-db', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('traffic-service', 'clicks-db', {}, status)).toBe(true)
    expect(isTrafficFlowEdge('shortener-service', 'haproxy', {}, status)).toBe(false)
    expect(isTrafficFlowEdge('pgcat', 'links-db', {}, status)).toBe(false)
  })
})
