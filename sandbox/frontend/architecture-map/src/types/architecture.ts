import type { InfraStatus } from './controlApi'

export interface ComponentDetails {
  purpose: string
  technologies: string[]
  port?: number
  managementPort?: number
  keyFormat?: string
  topology?: string
  links?: Record<string, string>
}

// A single object is an AND of every key it names against the live InfraStatus (absent keys
// don't constrain). An array is an OR of such groups - needed for nginx, which has two
// independent jobs (HTTP reverse proxy vs internal gRPC fan-out) and must stay visible if either
// one is active, not just both. Absent entirely means "always active" (every node/connection
// without this field, which is most of them).
export type ActiveWhen = Partial<InfraStatus> | Partial<InfraStatus>[]

export interface ArchComponent {
  id: string
  name: string
  type: 'service' | 'worker' | 'infrastructure' | 'database' | 'frontend'
  icon: string
  description: string
  capabilities?: string[]
  activeWhen?: ActiveWhen
  details: ComponentDetails
}

export interface ArchConnection {
  from: string
  to: string
  label: string
  protocol: string
  format?: string
  synchronous: boolean
  notes?: string
  activeWhen?: ActiveWhen
}

export interface ArchitectureData {
  components: ArchComponent[]
  connections: ArchConnection[]
}
