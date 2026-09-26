import { ScaleControl } from './ScaleControl'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Thin adapter from the uniform CapabilityControlProps shape to ScaleControl's own narrower props.
export function ScaleCapabilityControl({ serviceId, instances }: CapabilityControlProps) {
  return <ScaleControl serviceId={serviceId} currentReplicas={instances.length} />
}
