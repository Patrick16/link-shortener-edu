import type { ArchComponent } from '../types/architecture'

export interface AccessInfoEntry {
  label: string
  value: string
}

export interface AccessInfo {
  // Shown as the modal title - defaults to the component's own name if the caller doesn't override it.
  note?: string
  entries: AccessInfoEntry[]
}

// Everything here is hardcoded from sandbox/docker-compose.yml (dev-only, no real secrets - see its
// own top comment) rather than derived from architecture.json, because the values that matter here
// (guest/guest, postgres/postgres, host-mapped ports) live in the compose file, not the component
// data used to draw the graph. Kept separate from quickLink.ts: that file answers "where do I click
// to open this node's UI", this one answers "what do I type once I'm there or in a terminal".
export function getAccessInfo(component: ArchComponent): AccessInfo | null {
  switch (component.id) {
    case 'rabbitmq':
      return {
        entries: [
          { label: 'Dashboard', value: 'http://localhost:15672' },
          { label: 'Username', value: 'guest' },
          { label: 'Password', value: 'guest' },
          { label: 'AMQP connection string', value: 'amqp://guest:guest@localhost:5672' },
        ],
      }

    case 'redis-master':
      return {
        entries: [
          { label: 'Dashboard', value: 'http://localhost:5540' },
          { label: 'Login', value: 'No login - open the URL, no database is pre-registered' },
          { label: 'Add database in RedisInsight - Host', value: 'redis-master' },
          { label: 'Add database in RedisInsight - Port', value: '6379' },
          { label: 'redis-cli from host', value: 'redis-cli -h localhost -p 6379' },
        ],
      }

    case 'redis-replica1':
    case 'redis-replica2':
      return {
        entries: [
          { label: 'Dashboard', value: 'http://localhost:5540 (RedisInsight - shared, points at redis-master)' },
          { label: 'Note', value: 'No host port mapped - reachable only from inside the docker network, e.g. via `docker compose exec ' + component.id + ' redis-cli`' },
        ],
      }

    case 'redis-sentinel-1':
    case 'redis-sentinel-2':
    case 'redis-sentinel-3': {
      const hostPort = { 'redis-sentinel-1': 26379, 'redis-sentinel-2': 26380, 'redis-sentinel-3': 26381 }[component.id]
      return {
        entries: [
          { label: 'Note', value: 'Not RedisInsight-visible (it is not Sentinel-aware) - inspect via redis-cli' },
          { label: 'redis-cli from host', value: `redis-cli -p ${hostPort} sentinel master mymaster` },
          {
            label: 'App connection string (all 3 sentinels)',
            value: 'redis-sentinel-1:26379,redis-sentinel-2:26379,redis-sentinel-3:26379,serviceName=mymaster',
          },
        ],
      }
    }

    case 'mongo1':
    case 'mongo2':
    case 'mongo3': {
      const hostPort = { mongo1: 27017, mongo2: 27018, mongo3: 27019 }[component.id]
      return {
        entries: [
          { label: 'Dashboard', value: 'http://localhost:8085' },
          { label: 'Login', value: 'No login (Mongo Express basic auth is disabled for this stack)' },
          { label: 'Replica set connection string (host)', value: 'mongodb://localhost:27017,localhost:27018,localhost:27019/?replicaSet=rs0' },
          { label: `mongosh direct to this member`, value: `mongosh mongodb://localhost:${hostPort}` },
        ],
      }
    }

    case 'pgcat':
      return {
        entries: [
          { label: 'Note', value: 'No dashboard UI - pgcat is a connection pooler, connect with any Postgres client' },
          { label: 'psql (e.g. links_db)', value: 'psql -h localhost -p 6432 -U postgres -d links_db' },
          { label: 'Password', value: 'postgres' },
          { label: 'Connection string', value: 'postgresql://postgres:postgres@localhost:6432/links_db' },
        ],
      }

    case 'users-db':
    case 'links-db':
    case 'clicks-db': {
      const dbName = { 'users-db': 'users_db', 'links-db': 'links_db', 'clicks-db': 'clicks_db' }[component.id]
      return {
        entries: [
          { label: 'Note', value: 'Same primary Postgres instance as the other two databases, port 5432' },
          { label: 'psql', value: `psql -h localhost -p 5432 -U postgres -d ${dbName}` },
          { label: 'Password', value: 'postgres' },
          { label: 'Connection string', value: `postgresql://postgres:postgres@localhost:5432/${dbName}` },
        ],
      }
    }

    case 'postgres-replica1':
    case 'postgres-replica2': {
      const hostPort = component.id === 'postgres-replica1' ? 5433 : 5434
      return {
        entries: [
          { label: 'Note', value: 'Read-only hot standby - streams the whole cluster (all 3 databases), same login as the primary' },
          { label: 'psql', value: `psql -h localhost -p ${hostPort} -U postgres` },
          { label: 'Password', value: 'postgres' },
          { label: 'Connection string', value: `postgresql://postgres:postgres@localhost:${hostPort}/links_db` },
        ],
      }
    }

    default:
      return null
  }
}
