import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, unlink, rmdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { summarize, compareBaseline, createReport, measure, saveReport } from '../../scripts/smoke-performance.mjs';

test('percentiles use nearest rank and preserve failures and sample count', () => {
  const result = summarize([{ durationMs: 10, ok: true }, { durationMs: 20, ok: false }, { durationMs: 30, ok: true }], 1000);
  assert.equal(result.samples, 3);
  assert.equal(result.p50Ms, 20);
  assert.equal(result.p95Ms, 30);
  assert.equal(result.p99Ms, 30);
  assert.equal(result.failures, 1);
  assert.equal(result.errorRate, 1 / 3);
  assert.equal(result.observedRequestsPerSecond, 3);
  assert.equal(summarize([], 0).p99Ms, null);
});

test('failed sample preserves evidence and stops further requests', async () => {
  const report = createReport();
  report.methodology.warmupPerEndpoint = 0;
  let calls = 0;
  await assert.rejects(measure(report, async () => { if (++calls === 2) throw new Error('injected'); }), /injected/);
  assert.equal(calls, 2);
  assert.equal(report.endpoints.login.samples, 2);
  assert.equal(report.endpoints.login.failures, 1);
  assert.equal(report.endpoints.login.errorRate, .5);
  assert.equal(report.endpoints.authCheck, undefined);
});

test('baseline comparison is advisory and ignores absent or zero baselines', () => {
  const current = { endpoints: { login: { p95Ms: 150 }, commands: { p95Ms: 20 } } };
  const baseline = { endpoints: { login: { p95Ms: 100 }, commands: { p95Ms: 0 } } };
  assert.deepEqual(compareBaseline(current, baseline), [{ endpoint: 'login', baselineP95Ms: 100, currentP95Ms: 150, changePercent: 50 }]);
});

test('missing advisory baseline still saves the performance evidence', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'lwt-perf-test-'));
  const filename = path.join(directory, 'performance.json');
  const savedBaseline = process.env.SMOKE_PERF_BASELINE;
  const savedSummary = process.env.GITHUB_STEP_SUMMARY;
  try {
    process.env.SMOKE_PERF_BASELINE = path.join(directory, 'missing.json');
    delete process.env.GITHUB_STEP_SUMMARY;
    const report = createReport();
    report.status = 'passed';
    await saveReport(report, filename);
    const written = JSON.parse(await readFile(filename, 'utf8'));
    assert.equal(written.status, 'passed');
    assert.equal(written.baselineUnavailable, 'Error');
    assert.equal(written.schemaVersion, 1);
  } finally {
    if (savedBaseline === undefined) delete process.env.SMOKE_PERF_BASELINE; else process.env.SMOKE_PERF_BASELINE = savedBaseline;
    if (savedSummary === undefined) delete process.env.GITHUB_STEP_SUMMARY; else process.env.GITHUB_STEP_SUMMARY = savedSummary;
    await unlink(filename).catch(() => {});
    await rmdir(directory);
  }
});
