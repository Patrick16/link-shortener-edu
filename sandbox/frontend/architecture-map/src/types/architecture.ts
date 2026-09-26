export interface ComponentDetails {
  purpose: string
  technologies: string[]
  port?: number
  managementPort?: number
  keyFormat?: string
  topology?: string
  links?: Record<string, string>
}

export interface ArchComponent {
  id: string
  name: string
  type: 'service' | 'worker' | 'infrastructure' | 'database' | 'frontend'
  icon: string
  description: string
  capabilities?: string[]
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
}

export interface ArchitectureData {
  components: ArchComponent[]
  connections: ArchConnection[]
}
