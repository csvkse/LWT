import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { chromium } from '@playwright/test';

test('browser retains and reconciles an interrupted creation', async t => {
  let createCalls = 0;
  const operationKeys = new Set();
  let submitted = null;
  let tasks = [];
  let rejectBeforeSubmit = false;
  const html = await readFile(new URL('../../plugins/lxc-manager/ui/index.html', import.meta.url));
  const js = await readFile(new URL('../../plugins/lxc-manager/ui/app.js', import.meta.url));
  const server = http.createServer(async (req, res) => {
    if (req.url === '/index.html') { res.writeHead(200, {'content-type':'text/html'}); res.end(html); return; }
    if (req.url === '/app.js') { res.writeHead(200, {'content-type':'text/javascript'}); res.end(js); return; }
    res.setHeader('content-type','application/json');
    if (req.url === '/plugins/lxc-manager/api/capabilities') { res.end(JSON.stringify({status:'Ready',version:'test'})); return; }
    if (req.url === '/plugins/lxc-manager/api/discovery') { res.end(JSON.stringify({instances:[],images:[{fingerprint:'abcdef123456',description:'Debian',architecture:'x86_64'}],pools:[{name:'pool1',driver:'zfs',status:'Created'}],networks:[{name:'br0'}]})); return; }
    if (req.url === '/plugins/lxc-manager/api/managed') { res.end(JSON.stringify([{key:'0123456789abcdef0123456789abcdef',verified:true,spec:{name:'demo',imageFingerprint:'abcdef123456',cpu:1,memoryMiB:512,diskGiB:8,storagePool:'pool1',network:'br0'}},{key:'11111111111111111111111111111111',verified:false,spec:{name:'drift'}}])); return; }
    if (req.url === '/plugins/lxc-manager/api/tasks') { res.end(JSON.stringify(tasks)); return; }
    if (req.url === '/plugins/lxc-manager/api/preflight') { res.end(JSON.stringify({allowed:true,reasons:[]})); return; }
    if (req.url === '/plugins/lxc-manager/api/instances' && req.method === 'POST') {
      if (rejectBeforeSubmit) { res.writeHead(409);res.end(JSON.stringify({code:'not_submitted'}));return; }
      let body=''; for await (const chunk of req) body+=chunk;
      const payload=JSON.parse(body); operationKeys.add(payload.key); submitted=payload; createCalls++; req.socket.destroy(); return;
    }
    if (req.url === '/plugins/lxc-manager/api/instances/reconcile' && req.method === 'POST') {
      let body=''; for await (const chunk of req) body+=chunk;
      const bodyObject=JSON.parse(body);
      res.end(JSON.stringify({state:bodyObject.key===submitted?.key?'Completed':'NeedsReconciliation'})); return;
    }
    if (req.url === '/plugins/lxc-manager/api/lifecycle' && req.method === 'POST') { res.end(JSON.stringify({state:'Submitted'})); return; }
    if (req.url === '/plugins/lxc-manager/api/lifecycle/reconcile' && req.method === 'POST') { res.end(JSON.stringify({state:'Completed'})); return; }
    res.writeHead(404); res.end('{}');
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  t.after(()=>new Promise(resolve=>server.close(resolve)));
  const browser=await chromium.launch({channel:'msedge',headless:true});
  t.after(()=>browser.close());
  const page=await browser.newPage();
  page.setDefaultTimeout(5000);
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  const base=`http://127.0.0.1:${server.address().port}`;
  await page.goto(`${base}/index.html`);
  assert.equal(await page.getByRole('button',{name:'停止 drift'}).isDisabled(),true);
  await page.locator('[name=name]').fill('demo');
  await page.getByRole('button',{name:'检查配置'}).click();
  await page.getByRole('button',{name:'创建容器'}).click();
  await page.getByText('提交结果未确认',{exact:false}).waitFor();
  assert.ok(createCalls>=1);
  assert.equal(operationKeys.size,1);
  await page.reload();
  await page.getByText('存在尚未核对的创建任务。').waitFor();
  await page.getByRole('button',{name:'核对任务'}).click();
  await page.getByText('创建任务核对完成。').waitFor();
  assert.equal(operationKeys.size,1);
  await page.getByRole('button',{name:'停止 demo'}).click();
  await page.getByText('任务状态：Submitted。请核对任务。').waitFor();
  await page.reload();
  await page.getByText('存在尚未核对的操作任务。').waitFor();
  await page.getByRole('button',{name:'核对任务'}).click();
  await page.getByText('操作任务核对完成。').waitFor();
  tasks=[{key:'22222222222222222222222222222222',state:'Submitted',details:{owner:'0123456789abcdef0123456789abcdef',spec:{name:'demo'},action:'stop'}},{key:'33333333333333333333333333333333',state:'NeedsReconciliation'}];
  await page.reload();
  await page.getByText('信息不完整，请人工核对',{exact:false}).waitFor();
  await page.getByRole('button',{name:'恢复核对 demo'}).click();
  await page.getByRole('button',{name:'核对任务'}).click();
  await page.getByText('操作任务核对完成。').waitFor();
  tasks=[];rejectBeforeSubmit=true;
  await page.reload();
  await page.locator('[name=name]').fill('demo2');
  await page.getByRole('button',{name:'检查配置'}).click();
  await page.getByRole('button',{name:'创建容器'}).click();
  await page.getByText('服务端已明确拒绝，未提交创建任务。').waitFor();
  assert.equal(await page.evaluate(()=>localStorage.getItem('lwt.lxc-manager.pending-create.v1')),null);
  assert.deepEqual(errors,[]);
});
