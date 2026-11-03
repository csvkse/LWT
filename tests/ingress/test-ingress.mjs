// tests/ingress/test-ingress.mjs
// 验证平台级通用 Ingress 路由、Web Fallback 伪装回退与全双工流直通

import http from 'node:http';

const DEV_HOST = process.env.DEV_HOST || '10.126.1.70';
const DEV_PORT = process.env.DEV_PORT || 5271;
const INGRESS_PATH = '/ingress/socks5';

function fetchHttp(urlPath, headers = {}) {
    return new Promise((resolve, reject) => {
        const req = http.request({
            hostname: DEV_HOST,
            port: DEV_PORT,
            path: urlPath,
            method: 'GET',
            headers: headers,
            timeout: 5000
        }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => resolve({
                statusCode: res.statusCode,
                headers: res.headers,
                body: data
            }));
        });
        req.on('error', reject);
        req.on('timeout', () => { req.destroy(); reject(new Error('HTTP request timeout')); });
        req.end();
    });
}

function testWebSocketDuplex(urlPath) {
    return new Promise((resolve, reject) => {
        const wsUrl = `ws://${DEV_HOST}:${DEV_PORT}${urlPath}`;
        const ws = new globalThis.WebSocket(wsUrl);

        const timeout = setTimeout(() => {
            ws.close();
            reject(new Error('WebSocket connection timed out'));
        }, 5000);

        ws.onopen = () => {
            // 发送 SOCKS5 握手认证包: VER=5, NMETHODS=1, METHOD=0 (No Auth)
            ws.send(new Uint8Array([0x05, 0x01, 0x00]));
        };

        ws.onmessage = async (event) => {
            clearTimeout(timeout);
            let buf;
            if (event.data instanceof Blob) {
                const arrayBuffer = await event.data.arrayBuffer();
                buf = Buffer.from(arrayBuffer);
            } else if (event.data instanceof ArrayBuffer) {
                buf = Buffer.from(event.data);
            } else {
                buf = Buffer.from(event.data);
            }
            ws.close();
            resolve({
                success: buf.length >= 2 && buf[0] === 0x05 && buf[1] === 0x00,
                responseHex: buf.toString('hex')
            });
        };

        ws.onerror = (err) => {
            clearTimeout(timeout);
            reject(err);
        };
    });
}

async function run() {
    console.log(`====> 开始验证平台级通用 Ingress 核心能力 (目标: http://${DEV_HOST}:${DEV_PORT}) <====\n`);

    // 1. 验证 Web Fallback 伪装回退机制
    console.log('[Test 1] 验证探测流量与未携带 Upgrade 头的请求自动伪装回退 (Web Fallback)...');
    try {
        const res = await fetchHttp(INGRESS_PATH);
        console.log(`  响应状态码: ${res.statusCode} (预期: 200 OK)`);
        console.log(`  Content-Type: ${res.headers['content-type']}`);
        const isHtml = res.body.includes('<!DOCTYPE html>') || res.body.includes('<html');
        console.log(`  返回内容是否为 SPA 默认首页: ${isHtml}`);
        if (res.statusCode === 200 && isHtml) {
            console.log('  ✅ [PASS] Web Fallback 伪装成功！主动探测流量被完美流转至宿主前端，零特征泄露。\n');
        } else {
            console.error('  ❌ [FAIL] 未能正确回退至 SPA 前端首页');
        }
    } catch (err) {
        console.error('  ❌ [FAIL] HTTP 请求异常:', err.message);
    }

    // 2. 验证全双工 WebSocket 直通与微服务对接
    console.log('[Test 2] 验证全双工 WebSocket 升级直通与 SOCKS5 协议双向交互...');
    try {
        const wsRes = await testWebSocketDuplex(INGRESS_PATH);
        console.log(`  握手协商响应数据 (Hex): ${wsRes.responseHex}`);
        if (wsRes.success) {
            console.log('  ✅ [PASS] 全双工流式直通验证成功！微服务通过声明式 Ingress 与宿主完成双向数据流互通。\n');
        } else {
            console.error('  ❌ [FAIL] 接收到的协议协商包不符合预期');
        }
    } catch (err) {
        console.error('  ❌ [FAIL] WebSocket 直通异常:', err.message);
    }

    console.log('====> 平台级通用 Ingress 验证完成 <====');
}

run().catch(console.error);
