import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { InfraStatus } from '../types/controlApi'
import { TopologyPanel } from './TopologyPanel'

vi.mock('../utils/infraConfig', () => ({ applyInfraConfig: vi.fn() }))

function makeStatus(overrides: Partial<InfraStatus> = {}): InfraStatus {
  return {
    nginxBypassed: false,
    pgcatEnabled: true,
    cacheEnabled: true,
    messagingMode: 'rabbitmq',
    haproxyEnabled: true,
    postgresReplicasEnabled: true,
    redisEnabled: true,
    redisSentinelEnabled: true,
    mongoReplicasEnabled: true,
    ...overrides,
  }
}

function toggleButtonFor(label: string): HTMLElement {
  const row = screen.getByText(label).closest('.infra-toggle-row')
  if (!row) throw new Error(`No toggle row found for label "${label}"`)
  const button = row.querySelector('button')
  if (!button) throw new Error(`No toggle button found for label "${label}"`)
  return button
}

const noop = () => {}

describe('TopologyPanel', () => {
  it('pulls in a live-read field that changed externally (e.g. a generic Stop control) when the user never touched it', () => {
    // Regression: haproxyEnabled/postgresReplicasEnabled/redisEnabled/redisSentinelEnabled/
    // mongoReplicasEnabled are read live from Docker, not cached control-api state - a generic
    // start/stop control on the node's own panel can flip one at any time, independent of this
    // modal. Without reconciling, draft kept showing the stale value as "unsaved changes" and Save
    // would push it right back, undoing the manual stop.
    const status = makeStatus({ redisEnabled: true })
    const { rerender } = render(
      <TopologyPanel status={status} onClose={noop} onApplied={noop} onShowAllLabels={noop} onHideAllLabels={noop} />,
    )
    expect(toggleButtonFor('Redis')).toHaveTextContent('ON')

    // Someone stopped redis-master via its own node panel - the next status poll reflects that.
    rerender(
      <TopologyPanel
        status={makeStatus({ redisEnabled: false })}
        onClose={noop}
        onApplied={noop}
        onShowAllLabels={noop}
        onHideAllLabels={noop}
      />,
    )

    expect(toggleButtonFor('Redis')).toHaveTextContent('OFF')
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
  })

  it('does not clobber a pending edit to the same field with a stale status that has not caught up yet', async () => {
    // The other half of the same fix: status lags behind this panel's own pending edit until Save
    // actually applies it - reconciling must not revert the user's own in-progress toggle just
    // because a status poll in between still reports the old live value.
    const user = userEvent.setup()
    const status = makeStatus({ postgresReplicasEnabled: true })
    const { rerender } = render(
      <TopologyPanel status={status} onClose={noop} onApplied={noop} onShowAllLabels={noop} onHideAllLabels={noop} />,
    )

    await user.click(toggleButtonFor('Read replicas (Postgres)'))
    expect(toggleButtonFor('Read replicas (Postgres)')).toHaveTextContent('OFF')

    // A poll tick lands before Save is clicked - status still says ON (unchanged, nobody external
    // touched it) - this must not snap the user's own pending OFF edit back to ON.
    rerender(
      <TopologyPanel status={makeStatus({ postgresReplicasEnabled: true })} onClose={noop} onApplied={noop} onShowAllLabels={noop} onHideAllLabels={noop} />,
    )

    expect(toggleButtonFor('Read replicas (Postgres)')).toHaveTextContent('OFF')
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled()
  })
})
