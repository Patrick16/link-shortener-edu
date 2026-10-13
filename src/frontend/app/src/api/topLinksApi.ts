import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { getAccessToken } from './client'

const REPORTING_API_URL = import.meta.env.VITE_REPORTING_API_URL as string

// The access token is read through accessTokenFactory on every (re)connect, so a token refreshed
// by AuthContext is picked up at the next reconnect without rebuilding the connection. The server
// validates the token only during the handshake, so an open connection outlives the token's expiry
// until it next drops - acceptable for a read-only dashboard feed.
export function createTopLinksConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${REPORTING_API_URL}/hub/top-links`, {
      accessTokenFactory: () => getAccessToken() ?? '',
      // Auth is the bearer token, not cookies. The SignalR client sends credentials by default, and
      // a cross-origin credentialed request needs Access-Control-Allow-Credentials on the server,
      // which ReportingApi deliberately doesn't grant - so the browser rejects negotiate and the
      // panel stays offline. Turning credentials off avoids that and leaves no cookie for an XSS to
      // ride on.
      withCredentials: false,
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
}
