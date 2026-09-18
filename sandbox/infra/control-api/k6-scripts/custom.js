import http from 'k6/http';
import { check, sleep } from 'k6';

// The generic engine behind a custom (endpoint-selection) scenario - every VU picks a random
// endpoint from ENDPOINTS each iteration instead of running one fixed script like smoke/spike/
// read-heavy. LINK_API_URL/REDIRECT_API_URL already respect the nginx-bypass infra toggle (set by
// DockerService.RunTrafficAsync); AUTH_API_URL is always direct since nginx never fronted AuthApi.
//
// See smoke.js for why these thresholds exist - must match DockerService.TrackedStatusCodes.
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

const LINK_API = __ENV.LINK_API_URL || 'http://nginx:8082';
const REDIRECT_API = __ENV.REDIRECT_API_URL || 'http://nginx:8083';
const AUTH_API = __ENV.AUTH_API_URL || 'http://auth-api:8080';

// Must match DockerService.KnownEndpoints. Falls back to the create+redirect happy path if the
// request somehow reached here with nothing selected (Program.cs already rejects that, this is
// just so the script never runs zero-length and hangs picking from an empty array).
const ENDPOINTS = (__ENV.ENDPOINTS || 'create,redirect')
  .split(',')
  .map((e) => e.trim())
  .filter(Boolean);

// Per-VU state (k6 gives each VU its own JS module instance, so this is never shared across VUs) -
// lets a "redirect" pick reuse a hash this same VU just created with "create", instead of every
// redirect always hitting the one setup() fixture.
let vuHash = null;

export function setup() {
  // A fixture link so a run that only selects "redirect" still has something real to hit.
  const originalLink = `https://example.com/custom-fixture-${Date.now()}`;
  const res = http.post(
    `${LINK_API}/Links`,
    JSON.stringify({ originalLink }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  const hash = res.json('shortenLink');

  // Same async-persist race smoke.js/read-heavy.js pause for - give ShortenerService a moment.
  sleep(1);

  return { fixtureHash: hash };
}

function doCreate() {
  const originalLink = `https://example.com/${Math.random().toString(36).slice(2)}`;
  const res = http.post(
    `${LINK_API}/Links`,
    JSON.stringify({ originalLink }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  check(res, { 'link created (200)': (r) => r.status === 200 });
  const hash = res.json('shortenLink');
  if (hash) vuHash = hash;
}

function doRedirect(fixtureHash) {
  const hash = vuHash || fixtureHash;
  const res = http.get(`${REDIRECT_API}/${hash}`, { redirects: 0 });
  check(res, { 'redirect works (302)': (r) => r.status === 302 });
}

function doRegister() {
  const email = `custom-${__VU}-${Date.now()}-${Math.random().toString(36).slice(2)}@example.com`;
  const res = http.post(
    `${AUTH_API}/register`,
    JSON.stringify({ name: 'custom-load-test', email, password: 'Password123!' }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  check(res, { 'registered (200)': (r) => r.status === 200 });
}

export default function (data) {
  const endpoint = ENDPOINTS[Math.floor(Math.random() * ENDPOINTS.length)];
  if (endpoint === 'create') doCreate();
  else if (endpoint === 'redirect') doRedirect(data.fixtureHash);
  else if (endpoint === 'register') doRegister();

  sleep(0.2);
}
