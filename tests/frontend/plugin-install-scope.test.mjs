import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  buildAggregatedCommand,
  getDependencyScope,
  preferredInstallTarget,
  resolveInstallDistro,
} from '../../src/LinuxWebTool.WebHost/wwwroot/app/components/plugins/pluginCommandBuilder.js';

test('legacy dependencies remain container scoped and unknown scopes fail closed', () => {
  assert.equal(getDependencyScope({}), 'container');
  assert.equal(getDependencyScope({ scope: 'host' }), 'host');
  assert.equal(getDependencyScope({ scope: 'arbitrary' }), 'unknown');
});

test('host install target is selected only when required dependencies are host scoped', () => {
  assert.equal(preferredInstallTarget([{ id: 'incus', required: true, scope: 'host' }]), 'host');
  assert.equal(preferredInstallTarget([
    { id: 'incus', required: true, scope: 'host' },
    { id: 'curl', required: true },
  ]), 'container');
});

test('host install guide uses only a detected supported host distro', () => {
  assert.equal(resolveInstallDistro('host', { hostDistroKey: 'debian' }), 'debian');
  assert.equal(resolveInstallDistro('host', { hostDistroKey: 'unknown' }), '');
  assert.equal(resolveInstallDistro('container', {}), 'alpine');
});

test('aggregated install command includes dependencies for the selected target only', () => {
  const deps = [
    { id: 'incus', scope: 'host', installGuide: { debian: 'apt install -y incus' } },
    { id: 'curl', installGuide: { debian: 'apt install -y curl' } },
  ];
  const selected = new Set(['incus', 'curl']);
  const command = buildAggregatedCommand({}, deps, 'debian', selected, false, 'host');
  assert.equal(command, 'apt update && apt install -y incus');
});

test('LXC manager recommends the container-only Incus package on Debian', async () => {
  const manifestPath = new URL('../../plugins/lxc-manager/plugin.json', import.meta.url);
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const incus = manifest.dependencies.find((dependency) => dependency.id === 'incus-service');

  assert.ok(incus);
  assert.equal(incus.scope, 'host');
  assert.ok(incus.installGuide.debian.includes('incus-base'));
});
