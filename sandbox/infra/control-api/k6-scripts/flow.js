import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';

// Every traffic run goes through this one script now. Which real endpoints get called, in what
// order, how data flows from one call into the next, and how long to pause afterward is entirely
// driven by STEPS_JSON - a JSON array control-api built server-side from
// DockerService.EndpointRegistry, resolved from the ordered step list the UI's sequence builder
// sent. This script has no built-in knowledge of the app's actual routes; it's a generic
// interpreter over {id, serviceId, method, pathTemplate, bodyTemplate, produces, pauseAfterSeconds}
// objects. There's no fixed pause between iterations either - if every step's pauseAfterSeconds is
// 0, VUs loop as fast as the target can respond, which is a legitimate thing to want (e.g. a Stress
// preset), not an oversight.
//
// POOL_JSON is a second, optional input: real values control-api preloaded from the app itself
// (see DockerService.FetchDataPoolAsync) before this container ever started, e.g. a few thousand
// real link hashes. Without it, a sequence that only resolves a link (no Create step of its own)
// hits the exact same fixture link every iteration - fine for checking the route works at all, but
// useless for exercising cache/DB behavior across many different rows. SharedArray is k6's own
// mechanism for exactly this: a large read-only dataset held once and shared across every VU
// instead of copied per-VU.
const BASE_URLS = {
  'auth-api': __ENV.AUTH_API_URL || 'http://auth-api:8080',
  'link-api': __ENV.LINK_API_URL || 'http://nginx:8082',
  'redirect-api': __ENV.REDIRECT_API_URL || 'http://nginx:8083',
};

const STEPS = JSON.parse(__ENV.STEPS_JSON || '[]');
const POOL_VAR = __ENV.POOL_VAR || null;
const POOL_MODE = __ENV.POOL_MODE || 'sequential';
const POOL_VUS_HINT = Math.max(1, Number(__ENV.POOL_VUS_HINT || 1));
const POOL = new SharedArray('dataPool', () => JSON.parse(__ENV.POOL_JSON || '[]'));

// Must match DockerService.TrackedStatusCodes exactly (the codes, not the labels). Every request is
// tagged with which step it came from (below), so status codes can be broken down per endpoint in
// the report instead of one pooled total - but k6 only retains/exports a tag combination that a
// threshold explicitly references, and the set of step ids is only known at runtime (whatever
// sequence the UI sent), so the thresholds themselves have to be built dynamically here rather than
// listed as a static object literal the way a fixed status-only breakdown could be.
const TRACKED_STATUS_CODES = ['200', '302', '401', '404', '409', '500', '502', '503', '504', '0'];

function buildThresholds() {
  const thresholds = {};
  const stepIds = [...new Set(STEPS.map((s) => s.id))];
  for (const stepId of stepIds) {
    for (const code of TRACKED_STATUS_CODES) {
      thresholds[`http_reqs{step:${stepId},status:${code}}`] = ['count>=0'];
    }
  }
  return thresholds;
}

export const options = { thresholds: buildThresholds() };

function substitute(template, vars) {
  if (template == null) return null;
  return template.replace(/\{\{(\w+)\}\}/g, (_, name) => (vars[name] !== undefined ? String(vars[name]) : ''));
}

export function setup() {
  // A fixture link so a sequence that resolves a link without ever creating one in the same
  // iteration (e.g. testing "Resolve link" on its own) still has something real to hit, rather than
  // a request against an empty hash. Overwritten per-iteration below the moment a Create step runs.
  const res = http.post(
    `${BASE_URLS['link-api']}/Links`,
    JSON.stringify({ originalLink: `https://example.com/flow-fixture-${Date.now()}` }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  const hash = res.json('shortenLink');

  // Same async-persist race every other script in this project pauses for - give ShortenerService
  // a moment before any VU might try to resolve this fixture.
  sleep(1);

  return { fixtureHash: hash };
}

export default function (data) {
  const rand = Math.random().toString(36).slice(2);
  // Seeded once per iteration, not once per step - a Register step followed by a Login step in the
  // same sequence needs to see the *same* generated email/password to log in as the user it just
  // registered, not two unrelated identities.
  const vars = {
    email: `flow-${rand}@example.com`,
    password: 'Password123!',
    originalLink: `https://example.com/${rand}`,
    hash: data.fixtureHash,
  };

  // Overrides the fixture default with a value drawn from the preloaded pool, if one was
  // requested - before the sequence runs, so a Create step still later in the same sequence (an
  // uncommon but valid combo) overwrites it with a freshly created value exactly like it always
  // has, via the ordinary `produces` handling below. "sequential" spreads reads across VUs/
  // iterations via __VU/__ITER rather than a shared mutable counter (k6 VUs don't share memory);
  // it's an even-ish walk through the pool, not a strict guarantee once VUs ramp up or down mid-run.
  if (POOL_VAR && POOL.length > 0) {
    const index = POOL_MODE === 'random'
      ? Math.floor(Math.random() * POOL.length)
      : (__VU - 1 + __ITER * POOL_VUS_HINT) % POOL.length;
    vars[POOL_VAR] = POOL[index];
  }

  for (const step of STEPS) {
    const base = BASE_URLS[step.serviceId];
    const path = substitute(step.pathTemplate, vars);
    const url = `${base}${path}`;
    const body = substitute(step.bodyTemplate, vars);

    const res = step.method === 'GET'
      ? http.get(url, { redirects: 0, tags: { step: step.id } })
      : http.request(step.method, url, body, { headers: { 'Content-Type': 'application/json' }, tags: { step: step.id } });

    check(res, { [`${step.serviceId} ${step.method} ${step.pathTemplate}`]: (r) => r.status >= 200 && r.status < 400 });

    for (const [varName, field] of Object.entries(step.produces || {})) {
      const value = res.json(field);
      if (value !== undefined && value !== null) vars[varName] = value;
    }

    if (step.pauseAfterSeconds > 0) sleep(step.pauseAfterSeconds);
  }
}
