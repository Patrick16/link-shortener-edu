import { describe, expect, it } from 'vitest'
import { statusColor } from './statusColor'

describe('statusColor', () => {
  it.each([
    ['running', '#22c55e'],
    ['exited', '#ef4444'],
    ['paused', '#f59e0b'],
    ['restarting', '#f59e0b'],
  ])('maps %s to %s', (state, expected) => {
    expect(statusColor(state)).toBe(expected)
  })

  it('falls back to gray for an unknown state', () => {
    expect(statusColor('created')).toBe('#9ca3af')
  })

  it('falls back to gray for undefined', () => {
    expect(statusColor(undefined)).toBe('#9ca3af')
  })
})
