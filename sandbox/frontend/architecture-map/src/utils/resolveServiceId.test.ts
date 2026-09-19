import { describe, expect, it } from 'vitest'
import type { ArchComponent } from '../types/architecture'
import { resolveServiceId } from './resolveServiceId'

function component(id: string, links?: Record<string, string>): ArchComponent {
  return {
    id,
    name: id,
    type: 'service',
    icon: 'box',
    description: '',
    position: { x: 0, y: 0 },
    details: { purpose: '', technologies: [], links },
  }
}

describe('resolveServiceId', () => {
  it('returns the component id directly when it is already a known service id', () => {
    const known = new Set(['link-api'])

    expect(resolveServiceId(component('link-api'), known)).toBe('link-api')
  })

  it('resolves a "-db" component through its compose link suffix', () => {
    const known = new Set(['postgres'])
    const links = component('links-db', { compose: 'docker-compose.yml#postgres' })

    expect(resolveServiceId(links, known)).toBe('postgres')
  })

  it('returns null when neither the id nor the compose suffix is known', () => {
    const known = new Set(['postgres'])
    const orphan = component('mystery-db', { compose: 'docker-compose.yml#nonexistent' })

    expect(resolveServiceId(orphan, known)).toBeNull()
  })

  it('returns null when there is no compose link at all', () => {
    const known = new Set(['postgres'])

    expect(resolveServiceId(component('links-db'), known)).toBeNull()
  })

  it('returns null when the compose link has no "#" suffix', () => {
    const known = new Set(['postgres'])
    const noHash = component('links-db', { compose: 'docker-compose.yml' })

    expect(resolveServiceId(noHash, known)).toBeNull()
  })
})
