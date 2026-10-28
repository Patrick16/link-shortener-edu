import { useEffect, useId, useRef, useState } from 'react'
import mermaid from 'mermaid'

// One counter-based id per mounted diagram - mermaid.render needs a unique DOM id per call, and
// two diagrams open across modal navigation (or React re-mounting the same component) must never
// collide on it.
let renderCounter = 0

interface Props {
  source: string
}

// Docs are static markdown authored by us (see LearnModal's own urlTransform comment for the same
// trust assumption) - a ```mermaid fenced block's content is rendered as a diagram instead of a
// plain code block. Themed per the OS color scheme at mount time rather than wired to live scheme
// changes: this is a short-lived modal, not worth the extra listener/re-render plumbing.
export function MermaidDiagram({ source }: Props) {
  const reactId = useId()
  const [svg, setSvg] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const containerRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const isDark = window.matchMedia('(prefers-color-scheme: dark)').matches
    mermaid.initialize({ startOnLoad: false, theme: isDark ? 'dark' : 'default', securityLevel: 'strict' })

    const id = `mermaid-${reactId.replace(/[:]/g, '')}-${renderCounter++}`
    let cancelled = false

    mermaid
      .render(id, source)
      .then(({ svg }) => {
        if (!cancelled) setSvg(svg)
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err))
      })

    return () => {
      cancelled = true
    }
  }, [source, reactId])

  if (error) {
    // Fall back to the raw source as a code block rather than hiding the content on a bad diagram.
    return (
      <pre className="mermaid-error">
        <code>{source}</code>
      </pre>
    )
  }

  return <div ref={containerRef} className="mermaid-diagram" dangerouslySetInnerHTML={svg ? { __html: svg } : undefined} />
}
