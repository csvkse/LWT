#!/usr/bin/env node
/**
 * Real Standalone MCP Server for system inspection and calculation.
 * Zero external dependencies (uses standard Node.js runtime).
 * Compliant with Model Context Protocol (2024-11-05).
 */
const readline = require('readline');
const os = require('os');

const rl = readline.createInterface({
  input: process.stdin,
  output: process.stdout,
  terminal: false
});

rl.on('line', (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  try {
    const msg = JSON.parse(trimmed);
    handleMessage(msg);
  } catch (err) {
    process.stderr.write(`JSON parse error: ${err.message}\n`);
  }
});

function send(msg) {
  process.stdout.write(JSON.stringify(msg) + '\n');
}

function handleMessage(msg) {
  if (msg.method === 'initialize') {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        protocolVersion: '2024-11-05',
        capabilities: {
          tools: {
            listChanged: false
          }
        },
        serverInfo: {
          name: 'system-tools',
          version: '1.0.0'
        }
      }
    });
  } else if (msg.method === 'notifications/initialized') {
    // Client notification, no reply required
  } else if (msg.method === 'tools/list') {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        tools: [
          {
            name: 'get_system_info',
            description: 'Returns host system platform, architecture, CPU count, memory usage, and uptime',
            inputSchema: {
              type: 'object',
              properties: {
                detail: { type: 'boolean', description: 'Whether to include extra network and load details' }
              },
              required: []
            }
          },
          {
            name: 'execute_calc',
            description: 'Performs arithmetic calculation (add, subtract, multiply, divide)',
            inputSchema: {
              type: 'object',
              properties: {
                operation: { type: 'string', enum: ['add', 'sub', 'mul', 'div'], description: 'Math operation' },
                a: { type: 'number', description: 'First operand' },
                b: { type: 'number', description: 'Second operand' }
              },
              required: ['operation', 'a', 'b']
            }
          },
          {
            name: 'echo_message',
            description: 'Echoes back the provided message with timestamp and prefix',
            inputSchema: {
              type: 'object',
              properties: {
                message: { type: 'string', description: 'Message to echo' }
              },
              required: ['message']
            }
          }
        ]
      }
    });
  } else if (msg.method === 'tools/call') {
    const params = msg.params || {};
    const toolName = params.name;
    const args = params.arguments || {};

    if (toolName === 'get_system_info') {
      const totalMemMb = Math.round(os.totalmem() / 1024 / 1024);
      const freeMemMb = Math.round(os.freemem() / 1024 / 1024);
      const info = {
        platform: os.platform(),
        arch: os.arch(),
        hostname: os.hostname(),
        cpus: os.cpus().length,
        totalMemoryMB: totalMemMb,
        freeMemoryMB: freeMemMb,
        usedMemoryMB: totalMemMb - freeMemMb,
        uptimeSeconds: Math.round(os.uptime()),
        nodeVersion: process.version,
        timestamp: new Date().toISOString()
      };
      if (args.detail) {
        info.loadavg = os.loadavg();
      }
      send({
        jsonrpc: '2.0',
        id: msg.id,
        result: {
          content: [
            {
              type: 'text',
              text: JSON.stringify(info, null, 2)
            }
          ],
          isError: false
        }
      });
    } else if (toolName === 'execute_calc') {
      const op = String(args.operation || 'add').toLowerCase();
      const a = Number(args.a ?? 0);
      const b = Number(args.b ?? 0);
      let res;
      let isErr = false;

      switch (op) {
        case 'add': res = a + b; break;
        case 'sub': res = a - b; break;
        case 'mul': res = a * b; break;
        case 'div':
          if (b === 0) {
            res = 'Error: division by zero';
            isErr = true;
          } else {
            res = a / b;
          }
          break;
        default:
          res = `Error: unsupported operation ${op}`;
          isErr = true;
      }

      send({
        jsonrpc: '2.0',
        id: msg.id,
        result: {
          content: [
            {
              type: 'text',
              text: typeof res === 'number' ? `Calculation Result: ${a} ${op} ${b} = ${res}` : String(res)
            }
          ],
          isError: isErr
        }
      });
    } else if (toolName === 'echo_message') {
      const msgText = String(args.message || '');
      send({
        jsonrpc: '2.0',
        id: msg.id,
        result: {
          content: [
            {
              type: 'text',
              text: `[MCP Echo @ ${new Date().toISOString()}] ${msgText}`
            }
          ],
          isError: false
        }
      });
    } else {
      send({
        jsonrpc: '2.0',
        id: msg.id,
        error: {
          code: -32601,
          message: `Tool not found: ${toolName}`
        }
      });
    }
  } else if (msg.method === 'ping') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
  } else {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      error: { code: -32601, message: `Method not found: ${msg.method}` }
    });
  }
}
