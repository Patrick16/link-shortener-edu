import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { controlApi } from '../api/controlApi'
import type { ManagedContainer, ResourceSample } from '../types/controlApi'
import { useLiveStack } from './useLiveStack'

vi.mock('../api/controlApi', () => ({
  controlApi: { listContainers: vi.fn(), hubUrl: 'http://control-api.test/hub/status' },
}))

// A minimal fake HubConnection: captures the handlers useLiveStack registers via `.on(...)` so
// tests can invoke them directly to simulate a SignalR push, instead of needing a real hub.
// vi.hoisted since vi.mock's factory below is hoisted above normal top-level declarations.
const { handlers, fakeConnection } = vi.hoisted(() => {
  const handlers: Record<string, (...args: unknown[]) => void> = {}
  const fakeConnection = {
    on: vi.fn((event: string, handler: (...args: unknown[]) => void) => {
      handlers[event] = handler
    }),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
  }
  return { handlers, fakeConnection }
})

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl() {
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    build() {
      return fakeConnection
    }
  },
}))

function container(containerId: string, serviceId = 'link-api', containerNumber = 1): ManagedContainer {
  return { serviceId, containerId, containerNumber, state: 'running', status: 'Up' }
}

function sample(containerId: string, serviceId = 'link-api'): ResourceSample {
  return {
    serviceId,
    containerId,
    containerNumber: 1,
    cpuPercent: 1,
    memoryUsageBytes: 1,
    memoryLimitBytes: 1,
    tcpConnections: 0,
    timestamp: '2099-01-01T00:00:00Z',
  }
}

describe('useLiveStack', () => {
  beforeEach(() => {
    vi.mocked(controlApi.listContainers).mockResolvedValue([])
    for (const key of Object.keys(handlers)) delete handlers[key]
    fakeConnection.on.mockClear()
    fakeConnection.start.mockClear()
    fakeConnection.stop.mockClear()
  })

  it('accumulates resourceHistory per containerId from resourceStatsUpdated pushes', async () => {
    vi.mocked(controlApi.listContainers).mockResolvedValue([container('c1')])
    const { result } = renderHook(() => useLiveStack())
    await waitFor(() => expect(result.current.loading).toBe(false))

    act(() => handlers.resourceStatsUpdated([sample('c1')]))

    expect(result.current.resourceHistory).toHaveProperty('c1')
    expect(result.current.resourceHistory.c1).toHaveLength(1)
  })

  it('prunes history for a containerId that no longer appears in a later containersUpdated push', async () => {
    // The regression: container ids churn on every scale up/down - a retired container's history
    // must not be retained forever once containersUpdated reports it gone.
    vi.mocked(controlApi.listContainers).mockResolvedValue([container('c1')])
    const { result } = renderHook(() => useLiveStack())
    await waitFor(() => expect(result.current.loading).toBe(false))
    act(() => handlers.resourceStatsUpdated([sample('c1')]))
    expect(result.current.resourceHistory).toHaveProperty('c1')

    // c1 scaled down, replaced by c2 - the full current set, per StatusPollerService's contract.
    act(() => handlers.containersUpdated([container('c2')]))

    expect(result.current.resourceHistory).not.toHaveProperty('c1')
  })

  it('keeps history for a containerId that is still present in a later containersUpdated push', async () => {
    vi.mocked(controlApi.listContainers).mockResolvedValue([container('c1')])
    const { result } = renderHook(() => useLiveStack())
    await waitFor(() => expect(result.current.loading).toBe(false))
    act(() => handlers.resourceStatsUpdated([sample('c1')]))

    act(() => handlers.containersUpdated([container('c1')]))

    expect(result.current.resourceHistory).toHaveProperty('c1')
  })
})
