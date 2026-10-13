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
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
}
