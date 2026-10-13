# A CORS/credentials mismatch silently blocked the SignalR negotiate request

**Category:** infrastructure-bug **Status:** fixed

A browser WebSocket/SignalR client sends credentials (cookies) by default on its negotiate
request. If the server's CORS policy doesn't explicitly allow credentials for that origin, the
browser doesn't throw a visible error on the client — it just rejects the response, and
`HubConnection.start()` rejects too. The failure never reaches server logs, since the server
either never sees the request or answers it before the browser's own CORS check discards it.

**Concrete instance in this project:** [ReportingApi](node:reporting-api)'s CORS policy
(`AddFrontendCors`) defaults `allowCredentials` to `false` — correct for its ClickHouse report
endpoints, which only ever use a Bearer token. The new SignalR hub (`/hub/top-links`) shares the
same policy, and the SignalR JS client sends `withCredentials: true` by default on its negotiate
request regardless of whether the app actually uses cookies. The mismatch (client sends
credentials, server's policy doesn't allow them for that origin) made the browser reject the
cross-origin negotiate response outright — `connection.start()` failed, and the dashboard's
top-links panel stayed "Offline" with nothing informative in either the browser console or
ReportingApi's own logs, since the request never completed a real CORS preflight exchange.

Two fixes were available: grant `allowCredentials: true` on the server (ReportingApi already uses
an explicit origin list, which credentials requires — never `"*"`), or tell the client not to send
credentials at all, since this hub's only real auth is the Bearer token already carried via
`accessTokenFactory`, not a cookie. The second was chosen — it's the narrower change, and it
avoids making another possible credential (a cookie) relevant to an endpoint that doesn't need one.

Mistake (`bd47119`, `src/frontend/app/src/api/topLinksApi.ts`):

```typescript
return new HubConnectionBuilder()
  .withUrl(`${REPORTING_API_URL}/hub/top-links`, {
    accessTokenFactory: () => getAccessToken() ?? '',
  })
  .withAutomaticReconnect()
  // ...
```

Do this instead (`a2b405e`, same file):

```typescript
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
  // ...
```

**Provenance:** before and after are real commits (`bd47119` and `a2b405e`).
**Not reproduced live:** found by code review, not by observing a real failed connection in a
browser — the failure mode (silent rejection, nothing logged server-side) follows from documented
CORS/credentials behavior rather than an induced repro.

## Relatives

### Nodes

- [ReportingApi](node:reporting-api) — the server side of this mismatch (its CORS policy)

### Patterns

None yet.
