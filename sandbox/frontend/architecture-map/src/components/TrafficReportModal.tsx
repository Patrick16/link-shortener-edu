import { useEffect } from 'react'
import type { ReactNode } from 'react'
import { TrafficReportView } from './TrafficReportView'
import type { BottleneckVerdict, RunSnapshot, TraceHopStats, TrafficReport } from '../types/controlApi'

interface Props {
  title: string
  subtitle?: string
  report: TrafficReport
  verdict?: BottleneckVerdict | null
  traceHops?: TraceHopStats[] | null
  snapshot?: RunSnapshot | null
  onClose: () => void
  // Extra sections rendered above the report itself (RunHistoryPanel's steps/infra/rename
  // controls) - the modal only owns the overlay/card/report, not every possible header action.
  children?: ReactNode
}

// Same overlay/card pattern as CompareRunsModal/AccessInfoModal, holding one TrafficReportView -
// shared by the "just finished" view (TrafficResultPanel) and the "looking at a past run" view
// (RunHistoryPanel), so a report is always read in a focused modal instead of competing for space
// with the sidebar/header it was opened from.
export function TrafficReportModal({ title, subtitle, report, verdict, traceHops, snapshot, onClose, children }: Props) {
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  return (
    <div className="traffic-report-modal-overlay" onClick={onClose}>
      <div className="traffic-report-modal" onClick={(e) => e.stopPropagation()}>
        <div className="traffic-report-modal-header">
          <div>
            <h3>{title}</h3>
            {subtitle && <p className="traffic-report-modal-subtitle">{subtitle}</p>}
          </div>
          <button onClick={onClose} aria-label="Close">
            &times;
          </button>
        </div>

        <div className="traffic-report-modal-scroll">
          {children}
          <TrafficReportView report={report} verdict={verdict} traceHops={traceHops} snapshot={snapshot} />
        </div>
      </div>
    </div>
  )
}
