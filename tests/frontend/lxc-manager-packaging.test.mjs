import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdtemp } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';

test('native package builds Linux binaries at the manifest entrypoint for each architecture', async () => {
  const repo=path.resolve(import.meta.dirname,'../..');
  const plugin=path.join(repo,'plugins/lxc-manager');
  const manifest=JSON.parse(await readFile(path.join(plugin,'plugin.json'),'utf8'));
  assert.equal(manifest.runtime,'native');
  assert.equal(manifest.entrypoint,'lxc-manager');
  const output=await mkdtemp(path.join(os.tmpdir(),'lwt-lxc-package-'));
  for (const [arch,machine] of [['amd64',62],['arm64',183]]) {
    const binary=path.join(output,arch,manifest.entrypoint);
    execFileSync('go',['build','-o',binary,'./cmd/lxc-manager'],{cwd:plugin,env:{...process.env,CGO_ENABLED:'0',GOOS:'linux',GOARCH:arch}});
    const bytes=await readFile(binary);
    assert.deepEqual([...bytes.subarray(0,4)],[0x7f,0x45,0x4c,0x46]);
    assert.equal(bytes.readUInt16LE(18),machine);
  }
  for (const name of ['Dockerfile','Dockerfile.dev']) {
    const text=await readFile(path.join(repo,name),'utf8');
    assert.match(text,/go build[^\n]*\/cmd\/lxc-manager/);
    assert.match(text,/\/app\/plugins\/lxc-manager\/lxc-manager/);
  }
});
