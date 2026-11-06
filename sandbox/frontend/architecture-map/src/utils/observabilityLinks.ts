import type { QuickLink } from './quickLink'

// Cross-cutting, unlike quickLink.ts's per-node links - observability isn't represented as a node
// on the graph at all (see CLAUDE.md's "Cross-cutting" section), so there's nothing to attach these
// to. Ports match docker-compose.yml's aspire-dashboard/prometheus/jaeger/grafana services.
//
// Grafana/Prometheus/Jaeger only run under `-Observability Full`; Aspire Dashboard only runs under
// the default `-Observability Aspire` - the two sets are mutually exclusive (see docker-compose.yml's
// otel-collector comment) and `-Observability None` runs neither, so whichever set wasn't started
// just won't load. The `title` tooltips spell that out instead of trying to detect live state.
// traceUrlTemplate (Lesson Mode's traced-request lessons only) deep-links straight to one trace
// by id, in place of just the tool's own home page - {{traceId}} is substituted by
// lessons.ts/resolveTraceLink. Jaeger's shape is its own stable, documented UI route; Aspire
// Dashboard's has NOT been verified against a running dashboard yet - confirm it before a lesson
// ships relying on it (see .notes/PLAN.md's Lesson Mode entry).
export const observabilityLinks: (QuickLink & { title: string; traceUrlTemplate?: string })[] = [
  { label: 'Grafana', url: 'http://localhost:3000', title: 'Runs under -Observability Full' },
  { label: 'Prometheus', url: 'http://localhost:9090', title: 'Runs under -Observability Full' },
  { label: 'Jaeger', url: 'http://localhost:16686', title: 'Runs under -Observability Full', traceUrlTemplate: 'http://localhost:16686/trace/{{traceId}}' },
  {
    label: 'Aspire Dashboard',
    url: 'http://localhost:18888',
    title: 'Runs under -Observability Aspire (the default)',
    traceUrlTemplate: 'http://localhost:18888/traces/detail/{{traceId}}',
  },
]
