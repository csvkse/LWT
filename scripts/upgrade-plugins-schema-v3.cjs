const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'plugins');

const pluginUpdates = {
  'demo-dotnet-wasm': {
    manifestVersion: 3,
    archetype: {
      profile: 'Wasm',
      lifecycle: 'SourceCompiled',
      protocol: 'WasmAbi',
      language: 'csharp'
    },
    layout: {
      sourceEntry: 'Program.cs',
      targetArtifact: 'demo-dotnet-wasm.wasm',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'dotnet',
      command: 'dotnet publish -c Release /p:TargetArchitecture=wasm',
      artifacts: ['demo-dotnet-wasm.wasm'],
      watch: ['Program.cs'],
      autoCompileOnSave: true,
      timeoutSeconds: 120
    },
    interface: {
      protocol: 'WasmAbi',
      services: {
        'dotnet-wasm': {
          description: 'C# WASI 算力服务',
          operations: ['fib', 'calc', 'echo', 'info']
        }
      }
    }
  },
  'demo-wasm-function': {
    manifestVersion: 3,
    archetype: {
      profile: 'Wasm',
      lifecycle: 'SourceCompiled',
      protocol: 'WasmAbi',
      language: 'wat'
    },
    layout: {
      sourceEntry: 'function.wat',
      targetArtifact: 'main.wasm',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'wat2wasm',
      command: 'wat2wasm function.wat -o main.wasm',
      artifacts: ['main.wasm'],
      watch: ['function.wat'],
      autoCompileOnSave: true,
      timeoutSeconds: 30
    },
    interface: {
      protocol: 'WasmAbi',
      services: {
        'wasm-calc': {
          description: 'WAT WebAssembly 极速计算函数',
          operations: ['fib', 'calc', 'echo', 'info']
        }
      }
    }
  },
  'mcp-bridge': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'SourceCompiled',
      protocol: 'Http',
      language: 'go'
    },
    layout: {
      sourceEntry: 'cmd/mcp-bridge/main.go',
      targetArtifact: 'mcp-bridge',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'go',
      command: 'CGO_ENABLED=0 go build -ldflags "-s -w" -o mcp-bridge ./cmd/mcp-bridge',
      fallbackCommand: 'go build -o mcp-bridge ./cmd/mcp-bridge',
      artifacts: ['mcp-bridge'],
      watch: ['cmd/**/*.go', 'internal/**/*.go'],
      autoCompileOnSave: true,
      timeoutSeconds: 120
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'mcp-gateway': {
          description: 'MCP 智能安全网关中继服务',
          operations: ['inspect', 'tunnel', 'status', 'metrics']
        }
      }
    }
  },
  'lxc-manager': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'SourceCompiled',
      protocol: 'Http',
      language: 'go'
    },
    layout: {
      sourceEntry: 'cmd/lxc-manager/main.go',
      targetArtifact: 'lxc-manager',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'go',
      command: 'CGO_ENABLED=0 go build -ldflags "-s -w" -o lxc-manager ./cmd/lxc-manager',
      fallbackCommand: 'go build -o lxc-manager ./cmd/lxc-manager',
      artifacts: ['lxc-manager'],
      watch: ['cmd/**/*.go', 'internal/**/*.go'],
      autoCompileOnSave: true,
      timeoutSeconds: 120
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'lxc-api': {
          description: 'LXC/Incus 容器管理 API',
          operations: ['instances', 'status', 'networks', 'storage']
        }
      }
    }
  },
  'socks5-proxy': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'SourceCompiled',
      protocol: 'Http',
      language: 'go'
    },
    layout: {
      sourceEntry: 'cmd/socks5-proxy/main.go',
      targetArtifact: 'socks5-proxy',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'go',
      command: 'CGO_ENABLED=0 go build -ldflags "-s -w" -o socks5-proxy ./cmd/socks5-proxy',
      fallbackCommand: 'go build -o socks5-proxy ./cmd/socks5-proxy',
      artifacts: ['socks5-proxy'],
      watch: ['cmd/**/*.go'],
      autoCompileOnSave: true,
      timeoutSeconds: 120
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'socks5-service': {
          description: 'SOCKS5 代理网关控制服务',
          operations: ['status', 'stats', 'reload']
        }
      }
    }
  },
  'socks5-forwarder': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'SourceCompiled',
      protocol: 'Http',
      language: 'go'
    },
    layout: {
      sourceEntry: 'cmd/socks5-forwarder/main.go',
      targetArtifact: 'socks5-forwarder',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'go',
      command: 'CGO_ENABLED=0 go build -ldflags "-s -w" -o socks5-forwarder ./cmd/socks5-forwarder',
      fallbackCommand: 'go build -o socks5-forwarder ./cmd/socks5-forwarder',
      artifacts: ['socks5-forwarder'],
      watch: ['cmd/**/*.go'],
      autoCompileOnSave: true,
      timeoutSeconds: 120
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'forwarder-service': {
          description: 'SOCKS5 流量多路转发服务',
          operations: ['status', 'routes']
        }
      }
    }
  },
  'demo-dotnet-service': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'SourceCompiled',
      protocol: 'Http',
      language: 'csharp'
    },
    layout: {
      sourceEntry: 'Program.cs',
      targetArtifact: 'bin/publish/demo-dotnet-service.dll',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'dotnet',
      command: 'dotnet publish -c Release -o bin/publish',
      artifacts: ['bin/publish/demo-dotnet-service.dll'],
      watch: ['Program.cs'],
      autoCompileOnSave: true,
      timeoutSeconds: 120
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'dotnet-service': {
          description: '.NET 10 现代微服务 API',
          operations: ['status', 'hello', 'metrics']
        }
      }
    }
  },
  'demo-py-api': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'python'
    },
    layout: {
      sourceEntry: 'main.py',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'py-api': {
          description: 'Python 轻量异步微服务 API',
          operations: ['status', 'ping', 'echo']
        }
      }
    }
  },
  'chatgpt-channel': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'python'
    },
    layout: {
      sourceEntry: 'worker.py',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'chatgpt-worker': {
          description: 'ChatGPT 消息中继网关',
          operations: ['status', 'chat']
        }
      }
    }
  },
  'demo-js-webhook': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'javascript'
    },
    layout: {
      sourceEntry: 'index.js',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'js-webhook': {
          description: 'Node.js Webhook 事件接收微服务',
          operations: ['status', 'hook', 'ping']
        }
      }
    }
  },
  'demo-sh-daemon': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'bash'
    },
    layout: {
      sourceEntry: 'daemon.sh',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'sh-daemon': {
          description: 'Bash 进程与系统资源常驻监控',
          operations: ['status', 'report']
        }
      }
    }
  },
  'demo-ps-daemon': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'powershell'
    },
    layout: {
      sourceEntry: 'daemon.ps1',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'ps-daemon': {
          description: 'PowerShell 跨平台守护进程',
          operations: ['status', 'metrics']
        }
      }
    }
  },
  'caddy-manager': {
    manifestVersion: 3,
    archetype: {
      profile: 'OneShot',
      lifecycle: 'PrebuiltBinary',
      protocol: 'CliArgs',
      language: 'native'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'caddy-cli': {
          description: 'Caddy 原生反向代理管理工具',
          operations: ['validate', 'reload']
        }
      }
    }
  },
  'caddy-server-manager': {
    manifestVersion: 3,
    archetype: {
      profile: 'OneShot',
      lifecycle: 'Interpreted',
      protocol: 'CliArgs',
      language: 'javascript'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'caddy-server-ops': {
          description: 'Caddy 现代 Web 运维脚本控制器',
          operations: ['status', 'start', 'stop', 'restart']
        }
      }
    }
  },
  'ssh-access-guard': {
    manifestVersion: 3,
    archetype: {
      profile: 'OneShot',
      lifecycle: 'Interpreted',
      protocol: 'CliArgs',
      language: 'bash'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'ssh-guard': {
          description: 'SSH 访问审计守卫',
          operations: ['status', 'records', 'failed', 'banned_list', 'ban_ip', 'unban_ip']
        }
      }
    }
  },
  'sample-healthcheck': {
    manifestVersion: 3,
    archetype: {
      profile: 'OneShot',
      lifecycle: 'Interpreted',
      protocol: 'CliArgs',
      language: 'bash'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'healthcheck': {
          description: '宿主环境健康检测探针',
          operations: ['check']
        }
      }
    }
  },
  'native-command-demo': {
    manifestVersion: 3,
    archetype: {
      profile: 'OneShot',
      lifecycle: 'PrebuiltBinary',
      protocol: 'CliArgs',
      language: 'native'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'native-cmd': {
          description: '原生系统指令安全包装器',
          operations: ['status']
        }
      }
    }
  },
  'native-managed': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'PrebuiltBinary',
      protocol: 'Http',
      language: 'native'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'native-managed-svc': {
          description: '原生受控常驻微服务',
          operations: ['status', 'health']
        }
      }
    }
  },
  'native-resident': {
    manifestVersion: 3,
    archetype: {
      profile: 'Resident',
      lifecycle: 'PrebuiltBinary',
      protocol: 'Http',
      language: 'native'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      services: {
        'native-resident-svc': {
          description: '原生常驻守护微服务',
          operations: ['status', 'metrics']
        }
      }
    }
  }
};

let updatedCount = 0;

for (const [id, patch] of Object.entries(pluginUpdates)) {
  const pJsonPath = path.join(root, id, 'plugin.json');
  if (!fs.existsSync(pJsonPath)) {
    console.warn(`[Skip] plugin.json not found for: ${id}`);
    continue;
  }
  const original = JSON.parse(fs.readFileSync(pJsonPath, 'utf8'));
  const updated = {
    ...original,
    ...patch
  };
  fs.writeFileSync(pJsonPath, JSON.stringify(updated, null, 2) + '\n', 'utf8');
  updatedCount++;
  console.log(`[OK] Upgraded ${id} -> Schema v3.1`);
}

console.log(`\nUpgrade completed! ${updatedCount} plugins upgraded to Schema v3.1.`);
