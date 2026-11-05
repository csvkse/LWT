import test from 'node:test';
import assert from 'node:assert/strict';
import { rotateModes, timingDurations, requestTimelineEntry, failedRequestTotal, sanitizeProxyStatus, originToken, resourceToken, navigationMetrics, finalizeRequestCounts } from './proxy-multitab-diagnostics.mjs';

test('sample order rotates without dropping a mode', () => {
  const modes = ['direct', 'http', 'socks'];
  assert.deepEqual(rotateModes(modes, 0), ['direct', 'http', 'socks']);
  assert.deepEqual(rotateModes(modes, 1), ['http', 'socks', 'direct']);
  assert.deepEqual(rotateModes(modes, 2), ['socks', 'direct', 'http']);
});

test('missing CDP phase timings are excluded rather than counted as zero', () => {
  assert.deepEqual(timingDurations({ proxyStart: -1, proxyEnd: -1, dnsStart: 2, dnsEnd: 5, connectStart: 5, connectEnd: 12, sendEnd: 13, receiveHeadersEnd: 25 }), { dnsMs: 3, connectMs: 7, waitMs: 12 });
});

test('request timeline preserves timing and omits URL and headers', () => {
  const entry = requestTimelineEntry({ wallTime: 100, type: 'Document', response: { status: 200, protocol: 'h2', url: 'https://example.com/private', headers: { authorization: 'secret' }, timing: { requestTime: 10, sendEnd: 5, receiveHeadersEnd: 20 } } }, 2);
  assert.deepEqual(entry, { startUnixMs: 100000, tab: 2, resourceType: 'Document', status: 200, protocol: 'h2', phases: { waitMs: 15 } });
});

test('a single failed resource is not hidden by the median', () => {
  assert.equal(failedRequestTotal([{ failedRequests: 0 }, { failedRequests: 0 }, { failedRequests: 1 }]), 1);
});

test('late failures from an already loaded page count until the batch finishes', () => {
  const outcomes = [{ failedRequests: 0, requests: 1 }, { failedRequests: 0, requests: 1 }];
  finalizeRequestCounts(outcomes, [2, 1], [1, 0]);
  assert.equal(failedRequestTotal(outcomes), 1);
  assert.equal(outcomes[0].requests, 2);
});

test('status sampling excludes usernames, addresses and unrelated metadata', () => {
  const snapshot = sanitizeProxyStatus({ username: 'secret', listen: 'private', activeConns: 2, rejectedConns: 1, dns: { hits: 3, hostname: 'private' }, phases: { upstreamDial: { count: 4, totalNs: 20, maxNs: 10, target: 'private' } }, runtime: { goroutines: 5, pid: 123 } });
  assert.equal(snapshot.activeConns, 2);
  assert.equal(snapshot.dns.hits, 3);
  assert.equal(snapshot.phases.upstreamDial.count, 4);
  assert.equal(JSON.stringify(snapshot).includes('private'), false);
  assert.equal(JSON.stringify(snapshot).includes('secret'), false);
});

test('origin grouping ignores credentials, path and query', () => {
  const first = originToken('https://user:pass@example.com/a?token=secret', 'test-key');
  assert.equal(first, originToken('https://example.com/b', 'test-key'));
  assert.notEqual(first, originToken('https://other.example/b', 'test-key'));
  assert.equal(first.includes('example'), false);
});

test('resource grouping ignores credentials and query but distinguishes paths', () => {
  assert.equal(resourceToken('https://user:pass@example.com/a.js?secret=x', 'key'), resourceToken('https://example.com/a.js?other=y', 'key'));
  assert.notEqual(resourceToken('https://example.com/a.js', 'key'), resourceToken('https://example.com/b.js', 'key'));
});

test('partial navigation retains DOM and paint while missing load and TTFB remain null', () => {
  const got = navigationMetrics({ startTime: 0, requestStart: 0, responseStart: 0, domContentLoadedEventEnd: 150, loadEventEnd: 0, firstContentfulPaint: 120 });
  assert.deepEqual(got, { ttfbMs: null, domReadyMs: 150, loadEventMs: null, firstPaintMs: 120 });
});
