import { describe, expect, it } from 'vitest'
import type { ArchComponent, ComponentDetails } from '../types/architecture'
import { getQuickLinks } from './quickLink'

function component(id: string, type: ArchComponent['type'], details?: Partial<ComponentDetails>): ArchComponent {
  return {
    id,
    name: id,
    type,
    icon: 'box',
    description: '',
    details: { purpose: '', technologies: [], ...details },
  }
}

describe('getQuickLinks', () => {
  it('returns no links for a plain infrastructure component with no matching branch', () => {
    expect(getQuickLinks(component('nginx', 'infrastructure'))).toEqual([])
  })

  it('links the frontend app to its own port', () => {
    const links = getQuickLinks(component('frontend-app', 'frontend', { port: 5173 }))

    expect(links).toEqual([{ label: 'Open app', url: 'http://localhost:5173' }])
  })

  it('does not link the frontend app when it has no port recorded', () => {
    expect(getQuickLinks(component('frontend-app', 'frontend'))).toEqual([])
  })

  it('links any service with a port to its own Scalar docs', () => {
    const links = getQuickLinks(component('auth-api', 'service', { port: 8081 }))

    expect(links).toEqual([{ label: 'API docs (Scalar)', url: 'http://localhost:8081/scalar' }])
  })

  it('does not link a service with no port (e.g. a worker with no HTTP surface)', () => {
    expect(getQuickLinks(component('shortener-service', 'worker', { port: undefined }))).toEqual([])
  })

  it('gives link-api both its Scalar docs and its own SQLite fallback viewer', () => {
    const links = getQuickLinks(component('link-api', 'service', { port: 8082 }))

    expect(links).toEqual([
      { label: 'API docs (Scalar)', url: 'http://localhost:8082/scalar' },
      { label: 'SQLite fallback', url: 'http://localhost:8086' },
    ])
  })

  it('gives redirect-api both its Scalar docs and its own (different) SQLite fallback viewer', () => {
    const links = getQuickLinks(component('redirect-api', 'service', { port: 8083 }))

    expect(links).toEqual([
      { label: 'API docs (Scalar)', url: 'http://localhost:8083/scalar' },
      { label: 'SQLite fallback', url: 'http://localhost:8087' },
    ])
  })

  it('links rabbitmq to its management dashboard using its own managementPort', () => {
    const links = getQuickLinks(component('rabbitmq', 'infrastructure', { managementPort: 15672 }))

    expect(links).toEqual([{ label: 'RabbitMQ dashboard', url: 'http://localhost:15672' }])
  })

  it('does not link rabbitmq when it has no managementPort recorded', () => {
    expect(getQuickLinks(component('rabbitmq', 'infrastructure'))).toEqual([])
  })

  it.each(['redis-master', 'redis-sentinel-1', 'redis-replica-1'])(
    'links every member of the redis family (%s) to the shared RedisInsight instance',
    (id) => {
      expect(getQuickLinks(component(id, 'infrastructure'))).toEqual([{ label: 'RedisInsight', url: 'http://localhost:5540' }])
    },
  )

  it.each(['mongo-primary', 'mongo-secondary-1'])(
    'links every member of the mongo family (%s) to the shared Mongo Express instance',
    (id) => {
      expect(getQuickLinks(component(id, 'infrastructure'))).toEqual([{ label: 'Mongo Express', url: 'http://localhost:8085' }])
    },
  )

  it('links a database-type component to the shared pgweb instance', () => {
    expect(getQuickLinks(component('links-db', 'database'))).toEqual([{ label: 'pgweb', url: 'http://localhost:8084' }])
  })

  it('links pgcat (the pooler, not itself a "database"-typed component) to pgweb too', () => {
    expect(getQuickLinks(component('pgcat', 'infrastructure'))).toEqual([{ label: 'pgweb', url: 'http://localhost:8084' }])
  })

  it('links haproxy to its own stats page, not pgweb', () => {
    expect(getQuickLinks(component('haproxy', 'infrastructure'))).toEqual([{ label: 'HAProxy stats', url: 'http://localhost:8405/stats' }])
  })

  it('links every postgres replica to pgweb too, even though it is "infrastructure"-typed', () => {
    expect(getQuickLinks(component('postgres-replica-1', 'infrastructure'))).toEqual([{ label: 'pgweb', url: 'http://localhost:8084' }])
  })

  it('links clickhouse to its own built-in Play UI', () => {
    expect(getQuickLinks(component('clickhouse', 'infrastructure'))).toEqual([{ label: 'ClickHouse Play', url: 'http://localhost:8123/play' }])
  })

  it('does not link the primary postgres container itself unless it is database-typed', () => {
    // Only databases, pgcat, and *replicas* get pgweb per the current branch - "postgres" itself
    // (the primary, not a "postgres-replica*" id) with type 'infrastructure' falls through every
    // branch. This test exists to catch a future id/type change silently gaining or losing this link.
    expect(getQuickLinks(component('postgres', 'infrastructure'))).toEqual([])
  })
})
