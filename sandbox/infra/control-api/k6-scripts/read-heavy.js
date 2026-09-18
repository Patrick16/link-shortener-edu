import http from 'k6/http';
import { check, sleep } from 'k6';

// Unlike smoke/spike (which each iteration creates a new link), this creates ONE link up front in
// setup() and every VU just re-visits it for the whole run - a cache-hit-heavy read pattern, good
// for seeing Redis's effect on RedirectApi in isolation from the write path's own behavior.
const LINK_API = __ENV.LINK_API_URL || 'http://nginx:8082';
const REDIRECT_API = __ENV.REDIRECT_API_URL || 'http://nginx:8083';

export function setup() {
  const originalLink = `https://example.com/read-heavy-fixture-${Date.now()}`;
  const res = http.post(
    `${LINK_API}/Links`,
    JSON.stringify({ originalLink }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  const hash = res.json('shortenLink');

  // The write goes through RabbitMQ -> ShortenerService before it's actually queryable - give it
  // a moment so VUs aren't all 404-ing against a link that technically "exists" but isn't
  // persisted yet (see the same race smoke.js's own pause works around).
  sleep(1);

  return { hash };
}

export default function (data) {
  const res = http.get(`${REDIRECT_API}/${data.hash}`, { redirects: 0 });
  check(res, { 'redirect works (302)': (r) => r.status === 302 });
}
