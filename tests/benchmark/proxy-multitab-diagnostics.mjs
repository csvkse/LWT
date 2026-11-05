import { createHmac } from 'node:crypto';

export function originToken(url, key) {
  try {
    const parsed = new URL(url);
    if (!['http:', 'https:'].includes(parsed.protocol)) return null;
    return createHmac('sha256', key).update(parsed.hostname.toLowerCase()).digest('hex').slice(0, 16);
  } catch { return null; }
}

export function resourceToken(url, key) {
  try {
    const parsed = new URL(url);
    if (!['http:', 'https:'].includes(parsed.protocol)) return null;
    return createHmac('sha256', key).update(parsed.origin + parsed.pathname).digest('hex').slice(0, 16);
  } catch { return null; }
}

export function rotateModes(modes, sample) {
  if (!modes.length) return [];
  const offset = sample % modes.length;
  return [...modes.slice(offset), ...modes.slice(0, offset)];
}

export function timingDurations(timing) {
  const result = {};
  for (const [name, start, end] of [
    ['proxyMs', 'proxyStart', 'proxyEnd'],
    ['dnsMs', 'dnsStart', 'dnsEnd'],
    ['connectMs', 'connectStart', 'connectEnd'],
    ['waitMs', 'sendEnd', 'receiveHeadersEnd'],
  ]) {
    if (Number.isFinite(timing?.[start]) && timing[start] >= 0 && Number.isFinite(timing?.[end]) && timing[end] >= timing[start]) {
      result[name] = timing[end] - timing[start];
    }
  }
  return result;
}

export function requestTimelineEntry(event, tab) {
  return {
    startUnixMs: Number.isFinite(event.wallTime) ? event.wallTime * 1000 : null,
    tab,
    resourceType: event.type ?? null,
    status: event.response.status,
    protocol: event.response.protocol ?? null,
    phases: timingDurations(event.response.timing),
  };
}

export function failedRequestTotal(pages) {
  return pages.reduce((total, page) => total + page.failedRequests, 0);
}

export function finalizeRequestCounts(outcomes, completed, failed) {
  outcomes.forEach((outcome, index) => {
    outcome.requests = completed[index];
    outcome.failedRequests = failed[index];
  });
}

export function navigationMetrics(nav) {
  if (!nav) return null;
  const elapsed = (end) => Number.isFinite(end) && end > 0 && end >= nav.startTime ? end - nav.startTime : null;
  return {
    ttfbMs: nav.requestStart > 0 && nav.responseStart >= nav.requestStart ? nav.responseStart - nav.requestStart : null,
    domReadyMs: elapsed(nav.domContentLoadedEventEnd),
    loadEventMs: elapsed(nav.loadEventEnd),
    firstPaintMs: elapsed(nav.firstContentfulPaint),
  };
}

export function sanitizeProxyStatus(status) {
  const pick = (source, keys) => Object.fromEntries(keys.filter((key) => Number.isFinite(source?.[key])).map((key) => [key, source[key]]));
  return {
    ...pick(status, ['activeConns', 'totalConns', 'peakActiveConns', 'rejectedConns', 'maxActiveSessions', 'httpActiveRequests', 'httpPendingRequests', 'httpRejectedRequests', 'httpQueueTimeouts']),
    dns: pick(status.dns, ['hits', 'misses', 'staleHits', 'evictions', 'cacheSize']),
    phases: Object.fromEntries(['dnsResolve', 'upstreamDial'].map((name) => [name, pick(status.phases?.[name], ['count', 'totalNs', 'maxNs'])])),
    runtime: pick(status.runtime, ['goroutines', 'gomaxprocs', 'heapObjectsBytes', 'gcCycles']),
  };
}
