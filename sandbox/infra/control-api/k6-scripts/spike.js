import http from 'k6/http';
import { check } from 'k6';

// No sleep between iterations, and no pause between create and redirect (unlike smoke.js) - each
// VU hammers the flow as fast as it can. This deliberately hits LinkApi's real eventual-consistency
// window (a link's hash is returned before ShortenerService has actually persisted it via
// RabbitMQ), so a wave of "redirect works (302)" check failures here is expected behavior, not a
// bug - that race is exactly what this scenario is for demonstrating.
const LINK_API = __ENV.LINK_API_URL || 'http://link-api:8080';
const REDIRECT_API = __ENV.REDIRECT_API_URL || 'http://redirect-api:8080';

export default function () {
  const originalLink = `https://example.com/${Math.random().toString(36).slice(2)}`;

  const createRes = http.post(
    `${LINK_API}/Links`,
    JSON.stringify({ originalLink }),
    { headers: { 'Content-Type': 'application/json' } },
  );

  check(createRes, { 'link created (200)': (r) => r.status === 200 });

  const hash = createRes.json('shortenLink');
  if (!hash) {
    return;
  }

  const redirectRes = http.get(`${REDIRECT_API}/${hash}`, { redirects: 0 });
  check(redirectRes, { 'redirect works (302)': (r) => r.status === 302 });
}
