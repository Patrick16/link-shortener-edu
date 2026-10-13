import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { TopLinkEntry } from '../types'
import { TopLinksPanel } from './TopLinksPanel'

describe('TopLinksPanel', () => {
  it('shows a loading hint, not an empty-state claim, while the first list is pending', () => {
    render(<TopLinksPanel items={[]} status="connecting" />)

    expect(screen.getByText('Loading…')).toBeInTheDocument()
    expect(screen.queryByText(/No clicks/)).not.toBeInTheDocument()
  })

  it('shows the empty state once the list is loaded and empty', () => {
    render(<TopLinksPanel items={[]} status="live" />)

    expect(screen.getByText('No clicks in the last 7 days yet.')).toBeInTheDocument()
  })

  it('renders the links in the order given, with their click counts', () => {
    const items: TopLinkEntry[] = [
      { hash: 'bbb22222', clicks: 9 },
      { hash: 'aaa11111', clicks: 1 },
    ]

    render(<TopLinksPanel items={items} status="live" />)

    const rows = within(screen.getByRole('list')).getAllByRole('listitem')
    expect(rows).toHaveLength(2)
    expect(rows[0]).toHaveTextContent('bbb22222')
    expect(rows[0]).toHaveTextContent('9 clicks')
    expect(rows[1]).toHaveTextContent('aaa11111')
    expect(rows[1]).toHaveTextContent('1 click')
    expect(rows[1]).not.toHaveTextContent('1 clicks')
  })

  it.each([
    ['connecting', 'Connecting…'],
    ['live', 'Live'],
    ['reconnecting', 'Reconnecting…'],
    ['offline', 'Offline'],
  ] as const)('labels the %s status as "%s"', (status, label) => {
    render(<TopLinksPanel items={[]} status={status} />)

    expect(screen.getByText(label)).toBeInTheDocument()
  })
})
