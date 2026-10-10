import { performance } from 'node:perf_hooks';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { cpus, totalmem } from 'node:os';

export function summarize(samples, elapsedMs) {
  const sorted = samples.map(s => s.durationMs).sort((a, b) => a - b);
  const percentile = p => sorted.length ? sorted[Math.ceil(p * sorted.length) - 1] : null;
  const failures = samples.filter(s => !s.ok).length;
  return {
    samples: samples.length, failures, errorRate: samples.length ? failures / samples.length : null,
    minMs: sorted[0] ?? null, maxMs: sorted.at(-1) ?? null,
    p50Ms: percentile(.5), p95Ms: percentile(.95), p99Ms: percentile(.99),
    elapsedMs, observedRequestsPerSecond: elapsedMs > 0 ? samples.length * 1000 / elapsedMs : null,
  };
}

export function compareBaseline(current, baseline) {
  return Object.entries(current.endpoints).flatMap(([endpoint, values]) => {
    const previous = baseline.endpoints?.[endpoint]?.p95Ms;
    return previous > 0 && values.p95Ms !== null
      ? [{ endpoint, baselineP95Ms: previous, currentP95Ms: values.p95Ms, changePercent: (values.p95Ms / previous - 1) * 100 }]
      : [];
  });
}

export function createReport() {
  return {
    schemaVersion: 1, generatedAt: new Date().toISOString(),
    context: { commit: process.env.GITHUB_SHA || null, runnerOS: process.platform, runnerArch: process.arch,
      node: process.version, target: process.env.SMOKE_TARGET || 'unspecified', targetMode: process.env.SMOKE_TARGET_MODE || 'unknown',
      logicalCores: cpus().length, cpuModel: cpus()[0]?.model || null, hostMemoryBytes: totalmem(),
      runner: process.env.RUNNER_NAME ? 'github-actions' : 'local' },
    methodology: { concurrency: 1, samplesPerEndpoint: 20, warmupPerEndpoint: 2,
      percentile: 'nearest-rank', mode: 'advisory', includesResponseBody: true,
      throughput: 'observed sequential rate; not saturation capacity', p99: 'small-sample descriptive statistic' },
    startup: { readyMs: null, probes: 0, ready: false }, status: 'running', endpoints: {},
  };
}

export async function measure(report, invoke) {
  for (const endpoint of ['login', 'authCheck', 'commands', 'easyTierNodes']) {
    for (let i = 0; i < report.methodology.warmupPerEndpoint; i++) await invoke(endpoint);
    const samples = [];
    const started = performance.now();
    for (let i = 0; i < report.methodology.samplesPerEndpoint; i++) {
      const begin = performance.now();
      let ok = false;
      try { await invoke(endpoint); ok = true; }
      finally {
        samples.push({ durationMs: performance.now() - begin, ok });
        // Preserve partial evidence if any functional contract fails.
        report.endpoints[endpoint] = { ...summarize(samples, performance.now() - started), rawSamples: samples };
      }
    }
  }
}

export async function saveReport(report, filename) {
  if (process.env.SMOKE_PERF_BASELINE) {
    try {
      const baseline = JSON.parse(await readFile(process.env.SMOKE_PERF_BASELINE, 'utf8'));
      report.baselineComparison = compareBaseline(report, baseline);
      report.baselineCompatibility = {
        sameTarget: baseline.context?.target === report.context.target,
        sameTargetMode: baseline.context?.targetMode === report.context.targetMode,
        sameRunnerOS: baseline.context?.runnerOS === report.context.runnerOS,
        sameRunnerArch: baseline.context?.runnerArch === report.context.runnerArch,
        sameCPU: baseline.context?.cpuModel === report.context.cpuModel && baseline.context?.logicalCores === report.context.logicalCores,
        sameNodeMajor: baseline.context?.node?.split('.')[0] === report.context.node.split('.')[0],
        sameMethodology: JSON.stringify(baseline.methodology) === JSON.stringify(report.methodology),
      };
    } catch (error) { report.baselineUnavailable = error.name; }
  }
  await mkdir(path.dirname(filename), { recursive: true });
  await writeFile(filename, JSON.stringify(report, null, 2) + '\n');
  if (process.env.GITHUB_STEP_SUMMARY) {
    const { appendFile } = await import('node:fs/promises');
    const rows = Object.entries(report.endpoints).map(([key, s]) => `| ${key} | ${s.samples} | ${s.p50Ms?.toFixed(1)} | ${s.p95Ms?.toFixed(1)} | ${s.p99Ms?.toFixed(1)} | ${s.failures} |`);
    await appendFile(process.env.GITHUB_STEP_SUMMARY, `\n### Performance smoke (${report.context.target})\n\nStatus: ${report.status}. Startup ready: ${report.startup.readyMs ?? 'unavailable'} ms. Advisory, sequential, 20 samples; P99 is descriptive.\n\n| Endpoint | Samples | P50 ms | P95 ms | P99 ms | Failures |\n| --- | --- | --- | --- | --- | --- |\n${rows.join('\n')}\n`);
  }
}
