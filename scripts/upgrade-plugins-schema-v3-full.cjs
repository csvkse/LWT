const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'plugins');

const schemaV3Updates = {
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
      timeoutSeconds: 120,
      cleanTargets: ['mcp-bridge']
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
    },
    resources: {
      limits: { cpu: '1000m', memory: '512Mi', pids: 100 },
      requests: { cpu: '200m', memory: '128Mi' }
    },
    probes: {
      liveness: { type: 'SocketCheck', timeoutSeconds: 3 },
      readiness: { type: 'SocketCheck', timeoutSeconds: 5 }
    },
    events: {
      publishes: ['mcp.tunnel.connected', 'mcp.security.alert'],
      subscribes: ['system.maintenance']
    },
    permissions: ['network:listen', 'storage:readwrite']
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
      timeoutSeconds: 120,
      cleanTargets: ['lxc-manager']
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
    },
    resources: {
      limits: { cpu: '1000m', memory: '512Mi', pids: 100 },
      requests: { cpu: '200m', memory: '128Mi' }
    },
    probes: {
      liveness: { type: 'SocketCheck', timeoutSeconds: 3 },
      readiness: { type: 'SocketCheck', timeoutSeconds: 5 }
    },
    events: {
      publishes: ['lxc.instance.state_changed'],
      subscribes: ['system.cgroup.limit_update']
    },
    permissions: ['network:listen', 'storage:readwrite', 'socket:incus']
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
      watch: ['cmd/**/*.go', 'internal/**/*.go'],
      autoCompileOnSave: true,
      timeoutSeconds: 120,
      cleanTargets: ['socks5-proxy']
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'socks5-service': {
          description: 'SOCKS5 代理网关控制服务',
          operations: ['status', 'stats', 'reload']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '256Mi', pids: 50 },
      requests: { cpu: '100m', memory: '64Mi' }
    },
    probes: {
      liveness: { type: 'SocketCheck', timeoutSeconds: 3 },
      readiness: { type: 'SocketCheck', timeoutSeconds: 5 }
    },
    events: {
      publishes: ['proxy.connection.opened'],
      subscribes: []
    },
    permissions: ['network:listen', 'network:egress']
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
      watch: ['cmd/**/*.go', 'internal/**/*.go'],
      autoCompileOnSave: true,
      timeoutSeconds: 120,
      cleanTargets: ['socks5-forwarder']
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'forwarder-service': {
          description: 'SOCKS5 流量多路转发服务',
          operations: ['status', 'routes']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '256Mi', pids: 50 },
      requests: { cpu: '100m', memory: '64Mi' }
    },
    probes: {
      liveness: { type: 'SocketCheck', timeoutSeconds: 3 },
      readiness: { type: 'SocketCheck', timeoutSeconds: 5 }
    },
    events: {
      publishes: ['forwarder.traffic.routed'],
      subscribes: []
    },
    permissions: ['network:listen', 'network:egress']
  },
  'demo-dotnet-service': {
    manifestVersion: 3,
    profile: 'Resident',
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
      fallbackCommand: 'dotnet build -c Release -o bin/publish',
      artifacts: ['bin/publish/demo-dotnet-service.dll'],
      watch: ['Program.cs', '*.csproj'],
      autoCompileOnSave: true,
      timeoutSeconds: 120,
      cleanTargets: ['bin/', 'obj/']
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
    },
    resources: {
      limits: { cpu: '500m', memory: '256Mi', pids: 50 },
      requests: { cpu: '100m', memory: '64Mi' }
    },
    probes: {
      liveness: { type: 'SocketCheck', timeoutSeconds: 3 },
      readiness: { type: 'SocketCheck', timeoutSeconds: 5 }
    },
    events: {
      publishes: ['dotnet.service.started'],
      subscribes: []
    },
    permissions: ['network:listen', 'storage:readwrite']
  },
  'demo-nativeaot-wasm': {
    manifestVersion: 3,
    profile: 'Wasm',
    archetype: {
      profile: 'Wasm',
      lifecycle: 'SourceCompiled',
      protocol: 'WasmAbi',
      language: 'csharp'
    },
    layout: {
      sourceEntry: 'Program.cs',
      targetArtifact: 'demo-nativeaot-wasm.wasm',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    build: {
      toolchain: 'bflat',
      command: 'bflat build --arch wasm --os wasi Program.cs -o demo-nativeaot-wasm.wasm',
      fallbackCommand: 'dotnet publish -c Release /p:TargetArchitecture=wasm',
      artifacts: ['demo-nativeaot-wasm.wasm'],
      watch: ['Program.cs'],
      autoCompileOnSave: true,
      timeoutSeconds: 60,
      cleanTargets: ['demo-nativeaot-wasm.wasm']
    },
    interface: {
      protocol: 'WasmAbi',
      services: {
        'calculator': {
          description: 'C# NativeAOT 极速微函数算力',
          operations: ['fib', 'calc', 'echo', 'info']
        }
      }
    },
    resources: {
      limits: { cpu: '200m', memory: '64Mi', pids: 1 },
      requests: { cpu: '50m', memory: '16Mi' }
    },
    probes: {
      liveness: { type: 'WasmModuleCheck', timeoutSeconds: 2 },
      readiness: { type: 'WasmModuleCheck', timeoutSeconds: 2 }
    },
    events: {
      publishes: ['wasm.fib.computed'],
      subscribes: []
    },
    permissions: ['wasm:execute']
  },
  'demo-dotnet-wasm': {
    manifestVersion: 3,
    profile: 'Wasm',
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
      timeoutSeconds: 120,
      cleanTargets: ['demo-dotnet-wasm.wasm']
    },
    interface: {
      protocol: 'WasmAbi',
      services: {
        'dotnet-wasm': {
          description: 'C# WASI 算力服务',
          operations: ['fib', 'calc', 'echo', 'info']
        }
      }
    },
    resources: {
      limits: { cpu: '200m', memory: '64Mi', pids: 1 },
      requests: { cpu: '50m', memory: '16Mi' }
    },
    probes: {
      liveness: { type: 'WasmModuleCheck', timeoutSeconds: 2 },
      readiness: { type: 'WasmModuleCheck', timeoutSeconds: 2 }
    },
    events: {
      publishes: ['wasm.dotnet.computed'],
      subscribes: []
    },
    permissions: ['wasm:execute']
  },
  'demo-wasm-function': {
    manifestVersion: 3,
    profile: 'Wasm',
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
      timeoutSeconds: 30,
      cleanTargets: ['main.wasm']
    },
    interface: {
      protocol: 'WasmAbi',
      services: {
        'wasm-calc': {
          description: 'WAT WebAssembly 极速计算函数',
          operations: ['fib', 'calc', 'echo', 'info']
        }
      }
    },
    resources: {
      limits: { cpu: '200m', memory: '64Mi', pids: 1 },
      requests: { cpu: '50m', memory: '16Mi' }
    },
    probes: {
      liveness: { type: 'WasmModuleCheck', timeoutSeconds: 2 },
      readiness: { type: 'WasmModuleCheck', timeoutSeconds: 2 }
    },
    events: {
      publishes: ['wasm.function.executed'],
      subscribes: []
    },
    permissions: ['wasm:execute']
  },
  'demo-serverless-api': {
    manifestVersion: 3,
    profile: 'Serverless',
    archetype: {
      profile: 'Serverless',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'javascript'
    },
    layout: {
      sourceEntry: 'index.js',
      targetArtifact: 'index.js',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'serverless-api': {
          description: 'Node.js Serverless 动态弹性计算服务',
          operations: ['status', 'compute', 'echo']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '128Mi', pids: 30 },
      requests: { cpu: '100m', memory: '32Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['serverless.function.invoked'],
      subscribes: []
    },
    permissions: ['network:listen']
  },
  'demo-js-webhook': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'javascript'
    },
    layout: {
      sourceEntry: 'index.js',
      targetArtifact: 'index.js',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'js-webhook': {
          description: 'Node.js Webhook 事件接收微服务',
          operations: ['status', 'hook', 'ping']
        }
      }
    },
    resources: {
      limits: { cpu: '300m', memory: '128Mi', pids: 30 },
      requests: { cpu: '100m', memory: '32Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['webhook.received'],
      subscribes: []
    },
    permissions: ['network:listen', 'storage:readwrite']
  },
  'demo-py-api': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'python'
    },
    layout: {
      sourceEntry: 'main.py',
      targetArtifact: 'main.py',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'py-api': {
          description: 'Python 轻量异步微服务 API',
          operations: ['status', 'ping', 'echo']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '256Mi', pids: 30 },
      requests: { cpu: '100m', memory: '64Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['py.api.pong'],
      subscribes: []
    },
    permissions: ['network:listen']
  },
  'chatgpt-channel': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'python'
    },
    layout: {
      sourceEntry: 'worker.py',
      targetArtifact: 'worker.py',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'chatgpt-worker': {
          description: 'ChatGPT 消息中继网关',
          operations: ['status', 'chat']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '256Mi', pids: 30 },
      requests: { cpu: '100m', memory: '64Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['chatgpt.message.routed'],
      subscribes: []
    },
    permissions: ['network:listen', 'network:egress']
  },
  'demo-sh-daemon': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'bash'
    },
    layout: {
      sourceEntry: 'daemon.sh',
      targetArtifact: 'daemon.sh',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'sh-daemon': {
          description: 'Bash 进程与系统资源常驻监控',
          operations: ['status', 'report']
        }
      }
    },
    resources: {
      limits: { cpu: '200m', memory: '64Mi', pids: 20 },
      requests: { cpu: '50m', memory: '16Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['sh.daemon.tick'],
      subscribes: []
    },
    permissions: ['system:process_read']
  },
  'demo-ps-daemon': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'Interpreted',
      protocol: 'Http',
      language: 'powershell'
    },
    layout: {
      sourceEntry: 'daemon.ps1',
      targetArtifact: 'daemon.ps1',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'ps-daemon': {
          description: 'PowerShell 跨平台守护进程',
          operations: ['status', 'metrics']
        }
      }
    },
    resources: {
      limits: { cpu: '300m', memory: '128Mi', pids: 20 },
      requests: { cpu: '50m', memory: '32Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['ps.daemon.tick'],
      subscribes: []
    },
    permissions: ['system:metrics_read']
  },
  'caddy-manager': {
    manifestVersion: 3,
    profile: 'OneShot',
    archetype: {
      profile: 'OneShot',
      lifecycle: 'PrebuiltBinary',
      protocol: 'CliArgs',
      language: 'native'
    },
    layout: {
      sourceEntry: 'caddy',
      targetArtifact: 'caddy',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'caddy-cli': {
          description: 'Caddy 原生反向代理管理工具',
          operations: ['validate', 'reload']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '128Mi', pids: 20 },
      requests: { cpu: '100m', memory: '32Mi' }
    },
    probes: {
      liveness: { type: 'ExecCheck', timeoutSeconds: 3 },
      readiness: { type: 'ExecCheck', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['caddy.config.validated'],
      subscribes: []
    },
    permissions: ['process:execute']
  },
  'caddy-server-manager': {
    manifestVersion: 3,
    profile: 'OneShot',
    archetype: {
      profile: 'OneShot',
      lifecycle: 'Interpreted',
      protocol: 'CliArgs',
      language: 'javascript'
    },
    layout: {
      sourceEntry: 'hooks/status.js',
      targetArtifact: 'hooks/status.js',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'caddy-server-ops': {
          description: 'Caddy 现代 Web 运维脚本控制器',
          operations: ['status', 'start', 'stop', 'restart']
        }
      }
    },
    resources: {
      limits: { cpu: '300m', memory: '128Mi', pids: 20 },
      requests: { cpu: '50m', memory: '32Mi' }
    },
    probes: {
      liveness: { type: 'ExecCheck', timeoutSeconds: 3 },
      readiness: { type: 'ExecCheck', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['caddy.server.reloaded'],
      subscribes: []
    },
    permissions: ['process:execute']
  },
  'ssh-access-guard': {
    manifestVersion: 3,
    profile: 'OneShot',
    archetype: {
      profile: 'OneShot',
      lifecycle: 'Interpreted',
      protocol: 'CliArgs',
      language: 'bash'
    },
    layout: {
      sourceEntry: 'hooks/records.sh',
      targetArtifact: 'hooks/records.sh',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'ssh-guard': {
          description: 'SSH 访问审计守卫',
          operations: ['status', 'records', 'failed', 'banned_list', 'ban_ip', 'unban_ip']
        }
      }
    },
    resources: {
      limits: { cpu: '200m', memory: '64Mi', pids: 20 },
      requests: { cpu: '50m', memory: '16Mi' }
    },
    probes: {
      liveness: { type: 'ExecCheck', timeoutSeconds: 3 },
      readiness: { type: 'ExecCheck', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['ssh.ip.banned', 'ssh.ip.unbanned'],
      subscribes: []
    },
    permissions: ['file:auth_log_read']
  },
  'sample-healthcheck': {
    manifestVersion: 3,
    profile: 'OneShot',
    archetype: {
      profile: 'OneShot',
      lifecycle: 'Interpreted',
      protocol: 'CliArgs',
      language: 'bash'
    },
    layout: {
      sourceEntry: 'hooks/check.sh',
      targetArtifact: 'hooks/check.sh',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'healthcheck': {
          description: '宿主环境健康检测探针',
          operations: ['check']
        }
      }
    },
    resources: {
      limits: { cpu: '200m', memory: '64Mi', pids: 20 },
      requests: { cpu: '50m', memory: '16Mi' }
    },
    probes: {
      liveness: { type: 'ExecCheck', timeoutSeconds: 3 },
      readiness: { type: 'ExecCheck', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['system.health.checked'],
      subscribes: []
    },
    permissions: ['system:metrics_read']
  },
  'native-command-demo': {
    manifestVersion: 3,
    profile: 'OneShot',
    archetype: {
      profile: 'OneShot',
      lifecycle: 'PrebuiltBinary',
      protocol: 'CliArgs',
      language: 'native'
    },
    layout: {
      sourceEntry: '/bin/ls',
      targetArtifact: '/bin/ls',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'CliArgs',
      services: {
        'native-cmd': {
          description: '原生系统指令安全包装器',
          operations: ['status']
        }
      }
    },
    resources: {
      limits: { cpu: '100m', memory: '32Mi', pids: 10 },
      requests: { cpu: '50m', memory: '16Mi' }
    },
    probes: {
      liveness: { type: 'ExecCheck', timeoutSeconds: 2 },
      readiness: { type: 'ExecCheck', timeoutSeconds: 2 }
    },
    events: {
      publishes: ['native.command.completed'],
      subscribes: []
    },
    permissions: ['process:execute']
  },
  'native-managed': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'PrebuiltBinary',
      protocol: 'Http',
      language: 'native'
    },
    layout: {
      sourceEntry: 'bin/native-service',
      targetArtifact: 'bin/native-service',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'native-managed-svc': {
          description: '原生受控常驻微服务',
          operations: ['status', 'health']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '256Mi', pids: 50 },
      requests: { cpu: '100m', memory: '64Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['native.managed.started'],
      subscribes: []
    },
    permissions: ['network:listen']
  },
  'native-resident': {
    manifestVersion: 3,
    profile: 'Resident',
    archetype: {
      profile: 'Resident',
      lifecycle: 'PrebuiltBinary',
      protocol: 'Http',
      language: 'native'
    },
    layout: {
      sourceEntry: 'bin/resident-app',
      targetArtifact: 'bin/resident-app',
      storageDir: 'storage',
      runtimeDir: 'run'
    },
    interface: {
      protocol: 'Http',
      endpoint: 'http://127.0.0.1:{port}',
      healthEndpoint: '/health',
      services: {
        'native-resident-svc': {
          description: '原生常驻守护微服务',
          operations: ['status', 'metrics']
        }
      }
    },
    resources: {
      limits: { cpu: '500m', memory: '256Mi', pids: 50 },
      requests: { cpu: '100m', memory: '64Mi' }
    },
    probes: {
      liveness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 },
      readiness: { type: 'HttpCheck', url: 'http://127.0.0.1:{port}/health', timeoutSeconds: 3 }
    },
    events: {
      publishes: ['native.resident.started'],
      subscribes: []
    },
    permissions: ['network:listen']
  }
};

let count = 0;
for (const [id, patch] of Object.entries(schemaV3Updates)) {
  const pJson = path.join(root, id, 'plugin.json');
  if (!fs.existsSync(pJson)) {
    console.warn(`[Skip] Missing plugin.json in: ${id}`);
    continue;
  }
  const original = JSON.parse(fs.readFileSync(pJson, 'utf8'));
  const merged = {
    ...original,
    ...patch
  };

  fs.writeFileSync(pJson, JSON.stringify(merged, null, 2) + '\n', 'utf8');
  
  // 确保 storage 与 run 目录存在
  const storagePath = path.join(root, id, 'storage');
  if (!fs.existsSync(storagePath)) {
    fs.mkdirSync(storagePath, { recursive: true });
    fs.writeFileSync(path.join(storagePath, '.gitkeep'), '', 'utf8');
  }
  const runPath = path.join(root, id, 'run');
  if (!fs.existsSync(runPath)) {
    fs.mkdirSync(runPath, { recursive: true });
    fs.writeFileSync(path.join(runPath, '.gitkeep'), '', 'utf8');
  }

  // 针对编译产物目录 bin 的初始化
  if (merged.layout?.targetArtifact && merged.layout.targetArtifact.startsWith('bin/')) {
    const binDir = path.join(root, id, path.dirname(merged.layout.targetArtifact));
    if (!fs.existsSync(binDir)) {
      fs.mkdirSync(binDir, { recursive: true });
      fs.writeFileSync(path.join(binDir, '.gitkeep'), '', 'utf8');
    }
  }

  count++;
  console.log(`[OK] Upgraded ${id} -> Schema v3.1.0 standard (Layout, Probes, Resources, Events, Storage, Run)`);
}

console.log(`\n🎉 Full Schema v3.1.0 Upgrade Completed: ${count} plugins modernized!`);
