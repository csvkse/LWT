// tests/ingress/test-ingress.mjs
// 验证平台级通用 Ingress 多路径别名、零信任前置鉴权守卫、主动探测 Web Fallback 伪装与全双工流直通

import http from 'node:http';

const DEV_HOST = process.env.DEV_HOST || '10.126.1.70';
const DEV_PORT = process.env.DEV_PORT || 5271;
const PRIMARY_PATH = '/stream/live';
const ALIAS_PATH = '/telemetry/metrics';
const VALID_TOKEN = 'lwt_live_stream_token_8f3a9e';

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

function testWebSocket(urlPath, protocols) {
    return new Promise((resolve, reject) => {
        const wsUrl = `ws://${DEV_HOST}:${DEV_PORT}${urlPath}`;
        const ws = protocols ? new globalThis.WebSocket(wsUrl, protocols) : new globalThis.WebSocket(wsUrl);

        const timeout = setTimeout(() => {
            ws.close();
            reject(new Error('WebSocket connection timed out'));
        }, 5000);

        ws.onopen = () => {
            // 连接成功，发送 SOCKS5 握手认证包: VER=5, NMETHODS=1, METHOD=0 (No Auth)
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
                connected: true,
                protocol: ws.protocol,
                responseHex: buf.toString('hex'),
                isSocksHandshake: buf.length >= 2 && buf[0] === 0x05 && buf[1] === 0x00
            });
        };

        ws.onerror = (err) => {
            clearTimeout(timeout);
            // 握手失败被拒（例如触发了 Web Fallback 返回 200 OK HTML 导致 WebSocket 握手失败）
            resolve({
                connected: false,
                error: err.message || 'Handshake failed'
            });
        };
    });
}

async function run() {
    console.log(`====> 开始验证平台级 Ingress 动态多别名与防主动探测伪装流 (目标: http://${DEV_HOST}:${DEV_PORT}) <====\n`);

    // 1. 模拟 GFW 主动探测器（Active Prober）：未携带 Token 请求主路径与别名路径
    console.log('[Test 1] 模拟外部主动探测（未携带 Token）：验证 Web Fallback 伪装回退...');
    for (const testPath of [PRIMARY_PATH, ALIAS_PATH]) {
        try {
            const res = await fetchHttp(testPath);
            const isHtml = res.body.includes('<!DOCTYPE html>') || res.body.includes('<html');
            console.log(`  探测路径: ${testPath}`);
            console.log(`    响应状态码: ${res.statusCode} (预期: 200 OK)`);
            console.log(`    返回内容是否为 SPA 首页: ${isHtml}`);
            if (res.statusCode === 200 && isHtml) {
                console.log(`    ✅ [PASS] 探测流量被完美流转至宿主前端，零代理特征泄露。`);
            } else {
                console.error(`    ❌ [FAIL] 路径 ${testPath} 未能正确回退至 SPA 前端首页`);
            }
        } catch (err) {
            console.error(`    ❌ [FAIL] HTTP 请求异常:`, err.message);
        }

        // 尝试通过 WebSocket 探测握手
        const probeWs = await testWebSocket(testPath);
        if (!probeWs.connected) {
            console.log(`    ✅ [PASS] 未携带 Token 的探测 WebSocket 握手被成功阻断，探测器无法获得任何 05 00 SOCKS 响应。\n`);
        } else {
            console.error(`    ❌ [FAIL] 未授权连接不应建立 WebSocket 成功`);
        }
    }

    // 2. 验证多别名 + URL Query Token 合法连接
    console.log('[Test 2] 验证通过中立路径别名 (/telemetry/metrics) + Query Token 进行全双工直通...');
    try {
        const legitWs = await testWebSocket(`${ALIAS_PATH}?token=${VALID_TOKEN}`);
        console.log(`  别名连接状态: connected=${legitWs.connected}`);
        console.log(`  SOCKS5 协商响应 Hex: ${legitWs.responseHex}`);
        if (legitWs.connected && legitWs.isSocksHandshake) {
            console.log('  ✅ [PASS] 别名路径与 Token 守卫验证成功！已平滑建立全双工双向数据流。\n');
        } else {
            console.error('  ❌ [FAIL] 别名路径握手数据不符合预期:', legitWs);
        }
    } catch (err) {
        console.error('  ❌ [FAIL] 别名连接异常:', err.message);
    }

    // 3. 验证原生 WebSocket 子协议（Sec-WebSocket-Protocol）无痕安全鉴权
    console.log('[Test 3] 验证标准 WebSocket 子协议 (RFC 6455 Sec-WebSocket-Protocol) 无感鉴权...');
    try {
        const subprotocolWs = await testWebSocket(PRIMARY_PATH, ['lwt.v1', `lwt-token.${VALID_TOKEN}`]);
        console.log(`  子协议连接状态: connected=${subprotocolWs.connected}`);
        console.log(`  协商生效子协议: ${subprotocolWs.protocol}`);
        console.log(`  SOCKS5 协商响应 Hex: ${subprotocolWs.responseHex}`);
        if (subprotocolWs.connected && subprotocolWs.protocol === 'lwt.v1' && subprotocolWs.isSocksHandshake) {
            console.log('  ✅ [PASS] 子协议无痕鉴权直通成功！完全契合 RFC 6455 规范，无 URL 泄露风险。\n');
        } else {
            console.error('  ❌ [FAIL] 子协议握手数据不符合预期:', subprotocolWs);
        }
    } catch (err) {
        console.error('  ❌ [FAIL] 子协议连接异常:', err.message);
    }

    console.log('====> 平台级 Ingress 动态多别名与防主动探测伪装流验证完成 <====');
}

run().catch(console.error);
