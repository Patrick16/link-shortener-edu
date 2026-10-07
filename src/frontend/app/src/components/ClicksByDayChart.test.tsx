import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ClicksByDayChart } from './ClicksByDayChart'

describe('ClicksByDayChart', () => {
  it('renders nothing for an empty bucket list', () => {
    const { container } = render(<ClicksByDayChart buckets={[]} />)

    expect(container).toBeEmptyDOMElement()
  })

  it('renders one bar per bucket, each with its count in a tooltip title', () => {
    render(
      <ClicksByDayChart
        buckets={[
          { key: '2099-01-01', count: 5 },
          { key: '2099-01-02', count: 1 },
        ]}
      />,
    )

    const chart = screen.getByRole('img', { name: 'Clicks by day' })
    expect(chart.querySelectorAll('rect')).toHaveLength(2)
    expect(screen.getByText('2099-01-01: 5 clicks')).toBeInTheDocument()
    expect(screen.getByText('2099-01-02: 1 click')).toBeInTheDocument()
  })
})
