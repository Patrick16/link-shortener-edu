import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { EnumToggleControl } from './EnumToggleControl'
import type { MongoReadPreference } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const OPTIONS: { value: MongoReadPreference; label: string }[] = [
  { value: 'primary', label: 'Primary' },
  { value: 'secondaryPreferred', label: 'Secondary preferred' },
]

// Only affects traffic-service's reads - writes always go to whichever mongo1/2/3 is currently
// primary regardless of this setting (driver auto-discovers it). Recreates traffic-service.
export function MongoReadPreferenceControl(_props: CapabilityControlProps) {
  const [preference, setPreference] = useState<MongoReadPreference | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getMongoReadPreference()
      .then((s) => setPreference(s.preference))
      .catch(() => {})
  }, [])

  async function apply(next: MongoReadPreference) {
    setBusy(true)
    setError(null)
    try {
      const s = await controlApi.setMongoReadPreference(next)
      setPreference(s.preference)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  if (!preference) {
    return null
  }

  return (
    <div className="pgcat-pool-control">
      <EnumToggleControl label="Read preference (traffic-service)" options={OPTIONS} value={preference} busy={busy} onChange={apply} />
      <p className="infra-toggle-description">Only affects reads - writes always go to whichever mongo1/2/3 is currently primary. Recreates traffic-service, takes a few seconds.</p>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
