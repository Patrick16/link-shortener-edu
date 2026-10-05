import { apiFetch } from './client'
import type { ClickSummary } from '../types'

const REPORTING_API_URL = import.meta.env.VITE_REPORTING_API_URL as string

// Backed by ReportingApi, which reads ClickHouse - a separate read model fed asynchronously from
// the same click.tracked event RedirectApi publishes, not a live query against the write path.
// No auth: see ReportingApi's Program.cs for why this follows LinkApi's "never require a token"
// posture rather than RedirectApi/LinkApi's optional-JWT one.
export function getClickSummary(hash: string): Promise<ClickSummary> {
  return apiFetch<ClickSummary>(REPORTING_API_URL, `/reports/${hash}/summary`)
}
