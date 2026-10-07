import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { controlApi } from '../api/controlApi'
import { RunHistoryPanel } from './RunHistoryPanel'
import type { RunSummary } from '../types/controlApi'

vi.mock('../api/controlApi', () => ({
  controlApi: { listRuns: vi.fn(), getRun: vi.fn(), deleteRun: vi.fn(), clearRuns: vi.fn() },
}))

function summary(id: string, scenario = id): RunSummary {
  return { id, timestamp: '2099-01-01T00:00:00Z', scenario, httpRequests: 10, failedRequests: 0, exitCode: 0, httpRequestRate: 1 }
}

async function renderWithRuns(runs: RunSummary[]) {
  vi.mocked(controlApi.listRuns).mockResolvedValue(runs)
  const user = userEvent.setup()
  render(<RunHistoryPanel lastSavedRunId={null} onReuseRun={() => {}} />)
  await waitFor(() => expect(screen.getByLabelText(`Select run ${runs[0].scenario}`)).toBeInTheDocument())
  return user
}

describe('RunHistoryPanel bulk delete', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('removes only the runs that actually deleted successfully, keeping a failed one visible', async () => {
    // Regression: Promise.all(...).catch(() => {}) swallowed a per-id failure and removed every
    // checked id from the list regardless of whether its DELETE call actually succeeded - a run
    // whose file was never deleted disappeared from the UI as if it had been.
    const runs = [summary('run-a'), summary('run-b')]
    const user = await renderWithRuns(runs)
    vi.mocked(controlApi.deleteRun).mockImplementation((id) =>
      id === 'run-a' ? Promise.resolve(new Response()) : Promise.reject(new Error('network error')),
    )

    await user.click(screen.getByLabelText('Select run run-a'))
    await user.click(screen.getByLabelText('Select run run-b'))
    await user.click(screen.getByRole('button', { name: /Delete/ }))

    await waitFor(() => expect(screen.queryByLabelText('Select run run-a')).not.toBeInTheDocument())
    expect(screen.getByLabelText('Select run run-b')).toBeInTheDocument()
    expect(screen.getByText(/Failed to delete 1 of the selected run/)).toBeInTheDocument()
  })

  it('removes every checked run and shows no failure note when all deletes succeed', async () => {
    const runs = [summary('run-a'), summary('run-b')]
    const user = await renderWithRuns(runs)
    vi.mocked(controlApi.deleteRun).mockResolvedValue(new Response())

    await user.click(screen.getByLabelText('Select run run-a'))
    await user.click(screen.getByLabelText('Select run run-b'))
    await user.click(screen.getByRole('button', { name: /Delete/ }))

    await waitFor(() => expect(screen.queryByLabelText('Select run run-a')).not.toBeInTheDocument())
    expect(screen.queryByLabelText('Select run run-b')).not.toBeInTheDocument()
    expect(screen.queryByText(/Failed to delete/)).not.toBeInTheDocument()
  })
})

describe('RunHistoryPanel clear history', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('does nothing if the confirmation is declined', async () => {
    const runs = [summary('run-a')]
    const user = await renderWithRuns(runs)
    vi.spyOn(window, 'confirm').mockReturnValue(false)

    await user.click(screen.getByRole('button', { name: /Clear history/ }))

    expect(controlApi.clearRuns).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Select run run-a')).toBeInTheDocument()
  })

  it('clears the list once confirmed and every file deleted successfully', async () => {
    const runs = [summary('run-a')]
    const user = await renderWithRuns(runs)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.mocked(controlApi.clearRuns).mockResolvedValue({ failedCount: 0 })

    await user.click(screen.getByRole('button', { name: /Clear history/ }))

    await waitFor(() => expect(screen.queryByLabelText('Select run run-a')).not.toBeInTheDocument())
    expect(screen.getByText(/No runs yet/)).toBeInTheDocument()
    expect(screen.queryByText(/Failed to/)).not.toBeInTheDocument()
  })

  it('shows a failure message and re-fetches instead of assuming a full clear when some files failed to delete', async () => {
    // F4's backend fix reports a partial failure via failedCount rather than throwing - the
    // frontend must not just blindly setRuns([]) when that count is nonzero, since some runs are
    // still actually on disk.
    const runs = [summary('run-a'), summary('run-b')]
    const user = await renderWithRuns(runs)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.mocked(controlApi.clearRuns).mockResolvedValue({ failedCount: 1 })
    vi.mocked(controlApi.listRuns).mockResolvedValue([summary('run-b')])

    await user.click(screen.getByRole('button', { name: /Clear history/ }))

    await waitFor(() => expect(screen.getByText(/Failed to delete 1 run - still shown below\./)).toBeInTheDocument())
    expect(screen.getByLabelText('Select run run-b')).toBeInTheDocument()
  })

  it('shows a generic failure message and leaves the list untouched if clearRuns itself rejects', async () => {
    const runs = [summary('run-a')]
    const user = await renderWithRuns(runs)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.mocked(controlApi.clearRuns).mockRejectedValue(new Error('network error'))

    await user.click(screen.getByRole('button', { name: /Clear history/ }))

    await waitFor(() => expect(screen.getByText(/Failed to clear run history/)).toBeInTheDocument())
    expect(screen.getByLabelText('Select run run-a')).toBeInTheDocument()
  })

  it('ignores a stale in-flight refresh() response that resolves after a clear already completed', async () => {
    // F1 regression: refresh() is auto-triggered whenever lastSavedRunId changes, which is exactly
    // when "Clear history" first becomes visible. A slow GET /api/runs started right before the
    // user clicks Clear used to repopulate the list with the pre-clear data once it finally
    // resolved, silently undoing the clear.
    const runs = [summary('run-a')]
    vi.mocked(controlApi.listRuns).mockResolvedValueOnce(runs)
    const { rerender } = render(<RunHistoryPanel lastSavedRunId={null} onReuseRun={() => {}} />)
    await waitFor(() => expect(screen.getByLabelText('Select run run-a')).toBeInTheDocument())

    let resolveStaleRefresh!: (value: RunSummary[]) => void
    vi.mocked(controlApi.listRuns).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveStaleRefresh = resolve
        }),
    )
    // Simulates a run finishing while the stale GET above is still in flight.
    rerender(<RunHistoryPanel lastSavedRunId="run-b" onReuseRun={() => {}} />)

    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.mocked(controlApi.clearRuns).mockResolvedValue({ failedCount: 0 })
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: /Clear history/ }))
    await waitFor(() => expect(screen.queryByLabelText('Select run run-a')).not.toBeInTheDocument())

    // The stale GET (issued before the clear) finally resolves with the pre-clear list - it must
    // be ignored rather than repopulating runs.
    await act(async () => {
      resolveStaleRefresh(runs)
      await Promise.resolve()
    })

    expect(screen.queryByLabelText('Select run run-a')).not.toBeInTheDocument()
    expect(screen.getByText(/No runs yet/)).toBeInTheDocument()
  })
})
