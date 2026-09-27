import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { PinnedMetrics } from './PinnedMetrics'
import type { ArchComponent } from '../types/architecture'
import type { ManagedContainer, ResourceSample } from '../types/controlApi'

function component(id: string): ArchComponent {
  return { id, name: id, type: 'service', icon: '', description: '', details: { purpose: '', technologies: [] } }
}

function sample(overrides: Partial<ResourceSample> = {}): ResourceSample {
  return {
    serviceId: 'link-api',
    containerId: 'c1',
    containerNumber: 1,
    cpuPercent: 0,
    memoryUsageBytes: 0,
    memoryLimitBytes: 0,
    tcpConnections: 0,
    timestamp: '2099-01-01T00:00:00Z',
    ...overrides,
  }
}

function renderOnePinned() {
  const meta = new Map([['link-api', component('link-api')]])
  const container: ManagedContainer = { serviceId: 'link-api', containerId: 'c1', state: 'running', status: 'Up', containerNumber: 1 }
  render(
    <PinnedMetrics
      pinnedIds={['link-api']}
      metaById={meta}
      knownServiceIds={new Set(['link-api'])}
      containers={{ 'link-api': [container] }}
      resourceHistoryByContainer={{ c1: [sample({ cpuPercent: 5, tcpConnections: 3 })] }}
      onUnpin={() => {}}
    />,
  )
}

describe('PinnedMetrics sort header accessibility', () => {
  it('gives an unsorted column a plain "Sort by <label>" accessible name', () => {
    renderOnePinned()

    expect(screen.getByRole('button', { name: 'Sort by RAM MB' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sort by TCP' })).toBeInTheDocument()
  })

  it('describes the initially-active column\'s sort direction in words, not just the ▾ glyph', () => {
    // cpuPercent/desc is the initial state - a screen reader must be able to tell this without
    // relying on the visual arrow character alone.
    renderOnePinned()

    expect(screen.getByRole('button', { name: /Sort by CPU %, currently sorted descending\. Activate to sort ascending\./ })).toBeInTheDocument()
  })

  it('updates the accessible name when the active column\'s direction is toggled', async () => {
    const user = userEvent.setup()
    renderOnePinned()
    const cpuHeader = screen.getByRole('button', { name: /Sort by CPU %/ })

    await user.click(cpuHeader)

    expect(screen.getByRole('button', { name: /Sort by CPU %, currently sorted ascending\. Activate to sort descending\./ })).toBeInTheDocument()
  })

  it('updates the accessible name when switching the active column to a different one', async () => {
    const user = userEvent.setup()
    renderOnePinned()

    await user.click(screen.getByRole('button', { name: 'Sort by RAM MB' }))

    // Switching to a not-yet-active column always starts at "desc" (handleSortClick's own rule).
    expect(screen.getByRole('button', { name: /Sort by RAM MB, currently sorted descending\. Activate to sort ascending\./ })).toBeInTheDocument()
    // The previously-active column goes back to its plain, unsorted label.
    expect(screen.getByRole('button', { name: 'Sort by CPU %' })).toBeInTheDocument()
  })

  it('hides the decorative sort arrow glyph from the accessibility tree', () => {
    renderOnePinned()

    const arrow = document.querySelector('.pinned-metrics-sort-arrow')
    expect(arrow).not.toBeNull()
    expect(arrow).toHaveAttribute('aria-hidden', 'true')
  })
})
