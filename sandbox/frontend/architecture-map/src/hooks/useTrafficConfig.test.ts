import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { controlApi } from '../api/controlApi'
import type { CustomScenario, EndpointDefinition } from '../types/controlApi'
import { pointsToStages, RAMP_PRESETS, useTrafficConfig } from './useTrafficConfig'

vi.mock('../api/controlApi', () => ({
  controlApi: { listEndpoints: vi.fn(), listDataSources: vi.fn() },
}))

describe('pointsToStages', () => {
  it('converts adjacent points into stage durations and targets', () => {
    const stages = pointsToStages([
      { t: 0, vus: 0 },
      { t: 10, vus: 50 },
      { t: 30, vus: 20 },
    ])

    expect(stages).toEqual([
      { durationSeconds: 10, targetVus: 50 },
      { durationSeconds: 20, targetVus: 20 },
    ])
  })

  it('returns no stages for a single point', () => {
    expect(pointsToStages([{ t: 0, vus: 5 }])).toEqual([])
  })

  it('returns no stages for an empty list', () => {
    expect(pointsToStages([])).toEqual([])
  })
})

describe('useTrafficConfig', () => {
  const endpoint: EndpointDefinition = {
    id: 'create-link',
    serviceId: 'link-api',
    method: 'POST',
    pathTemplate: '/links',
    bodyTemplate: null,
    produces: {},
    consumes: [],
    description: '',
  }

  beforeEach(() => {
    vi.mocked(controlApi.listEndpoints).mockResolvedValue([endpoint])
    vi.mocked(controlApi.listDataSources).mockResolvedValue([])
  })

  it('loads endpoint options on mount', async () => {
    const { result } = renderHook(() => useTrafficConfig())

    await waitFor(() => expect(result.current.endpointOptions).toEqual([endpoint]));
  })

  it('starts with the first ramp preset applied and cannot run with an empty sequence', () => {
    const { result } = renderHook(() => useTrafficConfig())

    expect(result.current.totalDuration).toBe(RAMP_PRESETS[0].totalDurationSeconds)
    expect(result.current.points).toEqual(RAMP_PRESETS[0].points)
    expect(result.current.canRun).toBe(false)
  })

  it('canRun becomes true once a sequence exists and duration mode has 2+ points', () => {
    const { result } = renderHook(() => useTrafficConfig())

    act(() => result.current.setSequence([{ endpointId: 'create-link', pauseAfterSeconds: 0 }]))

    expect(result.current.canRun).toBe(true)
  })

  it('canRun is false in iterations mode when flatVus or iterationsTarget is below 1', () => {
    const { result } = renderHook(() => useTrafficConfig())
    act(() => result.current.setSequence([{ endpointId: 'create-link', pauseAfterSeconds: 0 }]))
    act(() => result.current.setStopMode('iterations'))

    act(() => result.current.setFlatVus(0))
    expect(result.current.canRun).toBe(false)

    act(() => result.current.setFlatVus(10))
    act(() => result.current.setIterationsTarget(0))
    expect(result.current.canRun).toBe(false)
  })

  it('switching the ramp preset applies its own duration and points', () => {
    const { result } = renderHook(() => useTrafficConfig())
    const stress = RAMP_PRESETS.find((p) => p.id === 'stress')!

    act(() => result.current.setRampPreset('stress'))

    expect(result.current.totalDuration).toBe(stress.totalDurationSeconds)
    expect(result.current.points).toEqual(stress.points)
  })

  it('buildRequest in duration mode converts points to stages and uses the first point as vus', () => {
    const { result } = renderHook(() => useTrafficConfig())
    act(() => result.current.setSequence([{ endpointId: 'create-link', pauseAfterSeconds: 0 }]))
    act(() => result.current.setScenarioName('my-scenario'))

    const request = result.current.buildRequest()

    expect(request.scenario).toBe('my-scenario')
    expect(request.vus).toBe(RAMP_PRESETS[0].points[0].vus)
    expect(request.stages).toEqual(pointsToStages(RAMP_PRESETS[0].points))
    expect(request.iterations).toBeUndefined()
  })

  it('buildRequest in iterations mode uses flatVus/iterationsTarget and omits stages', () => {
    const { result } = renderHook(() => useTrafficConfig())
    act(() => result.current.setSequence([{ endpointId: 'create-link', pauseAfterSeconds: 0 }]))
    act(() => result.current.setStopMode('iterations'))
    act(() => result.current.setFlatVus(25))
    act(() => result.current.setIterationsTarget(200))

    const request = result.current.buildRequest()

    expect(request.vus).toBe(25)
    expect(request.iterations).toBe(200)
    expect(request.stages).toBeUndefined()
  })

  it('buildRequest defaults the scenario name to "flow" when left blank', () => {
    const { result } = renderHook(() => useTrafficConfig())

    expect(result.current.buildRequest().scenario).toBe('flow')
  })

  it('loadScenario applies every field from a saved custom scenario', () => {
    const { result } = renderHook(() => useTrafficConfig())
    const saved: CustomScenario = {
      name: 'saved-one',
      steps: [{ endpointId: 'create-link', pauseAfterSeconds: 2 }],
      mode: 'iterations',
      totalDurationSeconds: 45,
      points: [{ t: 0, vus: 3 }, { t: 45, vus: 9 }],
      vus: 7,
      iterations: 42,
    }

    act(() => result.current.loadScenario(saved))

    expect(result.current.sequence).toEqual(saved.steps)
    expect(result.current.stopMode).toBe('iterations')
    expect(result.current.points).toEqual([{ t: 0, vus: 3 }, { t: 45, vus: 9 }])
    expect(result.current.totalDuration).toBe(45)
    expect(result.current.flatVus).toBe(7)
    expect(result.current.iterationsTarget).toBe(42)
  })

  it('resetScenario restores every field to its initial default', () => {
    const { result } = renderHook(() => useTrafficConfig())
    act(() => result.current.setSequence([{ endpointId: 'create-link', pauseAfterSeconds: 0 }]))
    act(() => result.current.setStopMode('iterations'))
    act(() => result.current.setFlatVus(99))

    act(() => result.current.resetScenario())

    expect(result.current.sequence).toEqual([])
    expect(result.current.stopMode).toBe('duration')
    expect(result.current.points).toEqual(RAMP_PRESETS[0].points)
    expect(result.current.totalDuration).toBe(RAMP_PRESETS[0].totalDurationSeconds)
    expect(result.current.flatVus).toBe(10)
    expect(result.current.iterationsTarget).toBe(100)
  })
})
