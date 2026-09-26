import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer } from '../types/controlApi'

// Shared prop shape every capability control receives, regardless of whether it actually uses all
// three fields - lets the registry in controlRegistry.tsx treat every control uniformly.
export interface CapabilityControlProps {
  component: ArchComponent
  serviceId: string
  instances: ManagedContainer[]
}
