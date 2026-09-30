import { useEffect, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import type { Components } from 'react-markdown'
import { getNodeDoc, getPatternDoc, getPitfallDoc } from '../utils/docsRegistry'

interface Props {
  title: string
  nodeId: string
  onClose: () => void
  onSelectNode: (id: string) => void
}

type StackEntry = { kind: 'pattern' | 'pitfall'; id: string }

// Modeled on AccessInfoModal.tsx (same overlay/close pattern) - kept out of NodePanel's own body,
// same reasoning as that modal: only open on demand, one node at a time, and markdown content
// (headings, code blocks, diffs) is wide/long enough that stacking it into the already-scrolling
// left sidebar made that panel unusable (found live while verifying this feature).
//
// `stack` lets navigation go more than one level deep (node -> pattern -> pitfall -> pattern, ...)
// - pitfall docs got split out from node/pattern docs specifically so a node can link to many of
// them without bloating its own page, which means a pattern doc's Pitfalls list can link into one
// too, so a single patternId/pitfallId pair isn't enough once that link exists.
export function LearnModal({ title, nodeId, onClose, onSelectNode }: Props) {
  const [stack, setStack] = useState<StackEntry[]>([])
  const top = stack[stack.length - 1]

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  const doc = !top
    ? getNodeDoc(nodeId)
    : top.kind === 'pattern'
      ? getPatternDoc(top.id)
      : getPitfallDoc(top.id)

  // Doc-internal Relatives links use a `node:<id>` / `pattern:<id>` / `pitfall:<id>` scheme (see
  // .notes/documentation-plan.md "Doc format") so a link's target kind is unambiguous without a
  // shared id namespace between the three. A `node:` link hands off to the graph's own selection
  // and closes this modal; `pattern:`/`pitfall:` links have no graph node to select, so they push
  // onto this modal's own navigation stack instead, with a way back.
  const components: Components = {
    a: ({ href, children }) => {
      if (href?.startsWith('node:')) {
        const id = href.slice('node:'.length)
        return (
          <a href={`#${href}`} onClick={(e) => { e.preventDefault(); onSelectNode(id); onClose() }}>
            {children}
          </a>
        )
      }
      if (href?.startsWith('pattern:') || href?.startsWith('pitfall:')) {
        const [kind, id] = href.split(':') as [StackEntry['kind'], string]
        return (
          <a href={`#${href}`} onClick={(e) => { e.preventDefault(); setStack((s) => [...s, { kind, id }]) }}>
            {children}
          </a>
        )
      }
      return (
        <a href={href} target="_blank" rel="noreferrer">
          {children}
        </a>
      )
    },
  }

  const headerLabel = !top ? `Learn: ${title}` : top.kind === 'pattern' ? `Pattern: ${top.id}` : 'Pitfall'

  return (
    <div className="learn-modal-overlay" onClick={onClose}>
      <div className="learn-modal" onClick={(e) => e.stopPropagation()}>
        <div className="learn-modal-header">
          <h3>
            {top && (
              <button type="button" className="learn-modal-back" onClick={() => setStack((s) => s.slice(0, -1))}>
                &larr;
              </button>
            )}
            {headerLabel}
          </h3>
          <button onClick={onClose} aria-label="Close">
            &times;
          </button>
        </div>

        <div className="learn-modal-body">
          {doc ? (
            // react-markdown's default urlTransform strips any URL scheme it doesn't recognize
            // (http/https/mailto/tel) down to an empty string - silently turning our `node:`/
            // `pattern:`/`pitfall:` links into `href=""`, which the browser then "follows" as a
            // real navigation to the current page on click (a full reload, wiping all app state -
            // this is what an earlier version of this component did, found by actually clicking
            // the link, not by reading the code). Identity transform is safe here: every doc is
            // authored by us, not third-party content.
            <ReactMarkdown components={components} urlTransform={(url) => url}>
              {doc}
            </ReactMarkdown>
          ) : (
            <p>No docs yet for this one.</p>
          )}
        </div>
      </div>
    </div>
  )
}
