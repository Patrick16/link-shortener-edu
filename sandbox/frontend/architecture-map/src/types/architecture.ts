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
  position: { x: number; y: number }
  scenarios: string[]
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
  scenarios: string[]
}

export interface ArchScenario {
  id: string
  name: string
  description: string
  focus: string
}

export interface ArchitectureData {
  components: ArchComponent[]
  connections: ArchConnection[]
  scenarios: ArchScenario[]
}
