import type { QuickLink } from './quickLink'

// Cross-cutting, unlike quickLink.ts's per-node links - observability isn't represented as a node
// on the graph at all (see CLAUDE.md's "Cross-cutting" section), so there's nothing to attach these
// to. Ports match docker-compose.yml's aspire-dashboard/prometheus/jaeger/grafana services.
//
// Grafana/Prometheus/Jaeger only run under `-Observability Full`; Aspire Dashboard only runs under
// the default `-Observability Aspire` - the two sets are mutually exclusive (see docker-compose.yml's
// otel-collector comment) and `-Observability None` runs neither, so whichever set wasn't started
// just won't load. The `title` tooltips spell that out instead of trying to detect live state.
export const observabilityLinks: (QuickLink & { title: string })[] = [
  { label: 'Grafana', url: 'http://localhost:3000', title: 'Runs under -Observability Full' },
  { label: 'Prometheus', url: 'http://localhost:9090', title: 'Runs under -Observability Full' },
  { label: 'Jaeger', url: 'http://localhost:16686', title: 'Runs under -Observability Full' },
  { label: 'Aspire Dashboard', url: 'http://localhost:18888', title: 'Runs under -Observability Aspire (the default)' },
]
