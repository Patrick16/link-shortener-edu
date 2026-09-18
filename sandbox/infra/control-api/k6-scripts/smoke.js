import http from 'k6/http';
import { check, sleep } from 'k6';

// Steady low-rate traffic against the real create-link -> redirect flow. Goes through nginx (not
// straight to link-api/redirect-api) - that's the actual external entry point now that those
// services can have multiple replicas, and it's what real traffic (a browser, this script) uses.
const LINK_API = __ENV.LINK_API_URL || 'http://nginx:8082';
const REDIRECT_API = __ENV.REDIRECT_API_URL || 'http://nginx:8083';

// k6 only keeps a status-code breakdown of http_reqs for codes referenced by a threshold - these
// always pass (count>=0), they exist purely to make k6 retain and export the per-code counts so
// the control panel can show which status codes requests actually failed with, not just a single
// failed-request tally. See DockerService.TrackedStatusCodes (must match this list).
export const options = {
  thresholds: {
    'http_reqs{status:200}': ['count>=0'],
    'http_reqs{status:302}': ['count>=0'],
    'http_reqs{status:404}': ['count>=0'],
    'http_reqs{status:500}': ['count>=0'],
    'http_reqs{status:502}': ['count>=0'],
    'http_reqs{status:503}': ['count>=0'],
    'http_reqs{status:504}': ['count>=0'],
    'http_reqs{status:0}': ['count>=0'],
  },
};

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
