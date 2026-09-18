// Status colors are reserved for state, never reused for categorical identity - kept in one place
// so every place that shows a container's state (node border, dot, ...) agrees on the mapping.
const STATE_COLOR: Record<string, string> = {
  running: '#22c55e',
  exited: '#ef4444',
  paused: '#f59e0b',
  restarting: '#f59e0b',
}

export function statusColor(state: string | undefined): string {
  if (!state) return '#9ca3af'
  return STATE_COLOR[state] ?? '#9ca3af'
}
