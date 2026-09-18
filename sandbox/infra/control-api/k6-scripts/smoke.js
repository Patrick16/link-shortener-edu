import http from 'k6/http';
import { check, sleep } from 'k6';

// Steady low-rate traffic against the real create-link -> redirect flow. Talks to the compose
// service names directly (this container runs on the same compose network), not localhost.
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
    sleep(1);
    return;
  }

  // LinkApi returns the hash before ShortenerService has actually persisted it (it goes through
  // RabbitMQ first) - a real eventual-consistency window, not a script bug. A short pause here
  // keeps this scenario a "happy path" check; spike.js deliberately skips it to stress that exact
  // race instead.
  sleep(0.3);

  const redirectRes = http.get(`${REDIRECT_API}/${hash}`, { redirects: 0 });
  check(redirectRes, { 'redirect works (302)': (r) => r.status === 302 });

  sleep(1);
}
