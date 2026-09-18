import { useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'

export function FlushCacheControl() {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function flush() {
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      const res = await controlApi.flushRedisCache()
      setResult(res.flushed)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flush-cache-control">
      <button onClick={flush} disabled={busy}>
        {busy ? 'Flushing...' : 'Flush cache (cold start)'}
      </button>
      {result && <span className="flush-cache-result">{result}</span>}
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
