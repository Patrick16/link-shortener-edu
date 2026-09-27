import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { controlApi } from '../api/controlApi'
import { ScaleControl } from './ScaleControl'

vi.mock('../api/controlApi', async () => {
  const actual = await vi.importActual<typeof import('../api/controlApi')>('../api/controlApi')
  return { ...actual, controlApi: { ...actual.controlApi, scale: vi.fn() } }
})

describe('ScaleControl', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(controlApi.scale).mockResolvedValue({ serviceId: 'link-api', replicas: 1, success: true, output: '' })
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('scales without a confirmation prompt for a small replica count', async () => {
    const user = userEvent.setup()
    const confirmSpy = vi.spyOn(window, 'confirm')
    render(<ScaleControl serviceId="link-api" currentReplicas={1} />)
    await user.clear(screen.getByRole('spinbutton'))
    await user.type(screen.getByRole('spinbutton'), '5')

    await user.click(screen.getByRole('button', { name: /Scale to/ }))

    expect(confirmSpy).not.toHaveBeenCalled()
    expect(controlApi.scale).toHaveBeenCalledWith('link-api', 5)
  })

  it('asks for confirmation before scaling well past a safe local replica count, and honors a decline', async () => {
    // Regression: raising the cap to 100 with no guardrail meant a single click could start 100
    // full containers at once on what's meant to be one developer's laptop, not a real orchestrator.
    const user = userEvent.setup()
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<ScaleControl serviceId="link-api" currentReplicas={1} />)
    await user.clear(screen.getByRole('spinbutton'))
    await user.type(screen.getByRole('spinbutton'), '80')

    await user.click(screen.getByRole('button', { name: /Scale to/ }))

    expect(confirmSpy).toHaveBeenCalledOnce()
    expect(controlApi.scale).not.toHaveBeenCalled()
  })

  it('proceeds with scaling once a large replica count is confirmed', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<ScaleControl serviceId="link-api" currentReplicas={1} />)
    await user.clear(screen.getByRole('spinbutton'))
    await user.type(screen.getByRole('spinbutton'), '80')

    await user.click(screen.getByRole('button', { name: /Scale to/ }))

    expect(controlApi.scale).toHaveBeenCalledWith('link-api', 80)
  })
})
