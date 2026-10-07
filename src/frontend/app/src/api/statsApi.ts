import { apiFetch } from './client'
import type { ClickSummary } from '../types'

const REPORTING_API_URL = import.meta.env.VITE_REPORTING_API_URL as string

// Backed by ReportingApi, which reads ClickHouse - a separate read model fed asynchronously from
// the same click.tracked event RedirectApi publishes, not a live query against the write path.
// Every ReportingApi endpoint now requires a Bearer token (see its Program.cs) - unlike LinkApi/
// RedirectApi's optional-JWT posture, there is no anonymous path through it at all, so auth: true
// is not optional here the way it is for linkApi.ts's own calls.
export function getClickSummary(hash: string): Promise<ClickSummary> {
  return apiFetch<ClickSummary>(REPORTING_API_URL, `/reports/${hash}/summary`, { auth: true })
}
