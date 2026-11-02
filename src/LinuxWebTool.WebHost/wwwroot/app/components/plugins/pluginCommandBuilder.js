export const RUNTIME_NAMES = {
  0: 'Node.js',
  1: 'Python 3',
  2: '.NET SDK',
  3: 'Bash',
  4: 'PowerShell',
  5: 'Native',
  6: 'Go',
};

export const RUNTIME_DEFAULT_COMMANDS = {
  0: {
    alpine: 'apk add --no-cache nodejs npm',
    debian: 'apt update && apt install -y nodejs npm',
    rhel: 'dnf install -y nodejs npm',
    windows: 'winget install OpenJS.NodeJS.LTS',
  },
  1: {
    alpine: 'apk add --no-cache python3 py3-pip',
    debian: 'apt update && apt install -y python3 python3-pip',
    rhel: 'dnf install -y python3 python3-pip',
    windows: 'winget install Python.Python.3.12',
  },
  2: {
    alpine: 'curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet',
    debian: 'apt update && apt install -y dotnet-sdk-10.0 || apt install -y dotnet-sdk-8.0',
    rhel: 'dnf install -y dotnet-sdk-10.0 || dnf install -y dotnet-sdk-8.0',
    windows: 'winget install Microsoft.DotNet.SDK.10',
  },
  3: {
    alpine: 'apk add --no-cache bash',
    debian: 'apt update && apt install -y bash',
    rhel: 'dnf install -y bash',
    windows: 'winget install Git.Git',
  },
  4: {
    alpine: 'apk add --no-cache powershell',
    debian: 'apt update && apt install -y powershell',
    rhel: 'dnf install -y powershell',
    windows: 'winget install Microsoft.PowerShell',
  },
  5: {
    alpine: 'apk add --no-cache caddy curl wget',
    debian: 'apt update && apt install -y caddy curl wget',
    rhel: 'dnf install -y caddy curl wget',
    windows: 'winget install CaddyServer.Caddy',
  },
  6: {
    alpine: 'apk update && apk add --no-cache go',
    debian: 'apt update && apt install -y golang-go',
    rhel: 'dnf install -y golang',
    windows: 'winget install GoLang.Go',
  },
};

export const DISTRO_TABS = [
  { key: 'alpine', label: '🐧 Alpine Linux (Docker 推荐)' },
  { key: 'debian', label: '🍥 Debian / Ubuntu' },
  { key: 'rhel', label: '🎩 RHEL / CentOS' },
  { key: 'windows', label: '🪟 Windows' },
];

export function resolveCoreRuntimeCmd(p, distro) {
  if (!p) return '';
  if (p.effectiveInstallGuide && distro === 'alpine') return p.effectiveInstallGuide;
  const guideMap = p.manifest?.installGuide;
  if (guideMap && typeof guideMap === 'object') {
    if (guideMap[distro]) return guideMap[distro];
    if (distro === 'alpine' && guideMap.linux) return guideMap.linux;
  }
  const rt = p.runtime ?? p.manifest?.runtime ?? 0;
  const defaults = RUNTIME_DEFAULT_COMMANDS[rt] || RUNTIME_DEFAULT_COMMANDS[0];
  return defaults[distro] || '';
}

export function buildAggregatedCommand(p, dependencyList, distro, selectedIds, includeCore) {
  const rawCommands = [];
  if (includeCore) {
    const coreCmd = resolveCoreRuntimeCmd(p, distro);
    if (coreCmd) rawCommands.push(coreCmd);
  }
  for (const dep of dependencyList) {
    if (selectedIds.has(dep.id)) {
      const guide = dep.installGuide?.[distro];
      if (guide) rawCommands.push(guide);
    }
  }
  if (rawCommands.length === 0) return '';

  if (distro === 'alpine') {
    const apkPackages = new Set();
    const otherCmds = [];
    for (const cmd of rawCommands) {
      const parts = cmd.split('&&').map((s) => s.trim());
      for (const part of parts) {
        const match = part.match(/^apk\s+add(?:\s+--no-cache)?\s+(.+)$/);
        if (match) {
          match[1].split(/\s+/).forEach((pkg) => {
            if (pkg && !pkg.startsWith('-')) apkPackages.add(pkg);
          });
        } else {
          otherCmds.push(part);
        }
      }
    }
    const combined = [];
    if (apkPackages.size > 0) {
      combined.push(`apk add --no-cache ${Array.from(apkPackages).join(' ')}`);
    }
    for (const extra of otherCmds) {
      if (!combined.includes(extra)) combined.push(extra);
    }
    return combined.join(' && ');
  }

  if (distro === 'debian') {
    const aptPackages = new Set();
    const otherCmds = [];
    for (const cmd of rawCommands) {
      const parts = cmd.split('&&').map((s) => s.trim());
      for (const part of parts) {
        const match = part.match(/^apt(?:-get)?\s+install(?:\s+-y)?\s+(.+)$/);
        if (match) {
          match[1].split(/\s+/).forEach((pkg) => {
            if (pkg && !pkg.startsWith('-')) aptPackages.add(pkg);
          });
        } else if (part === 'apt update' || part === 'apt-get update') {
          // ignore, will prepend
        } else {
          otherCmds.push(part);
        }
      }
    }
    const combined = [];
    if (aptPackages.size > 0) {
      combined.push(`apt update && apt install -y ${Array.from(aptPackages).join(' ')}`);
    }
    for (const extra of otherCmds) {
      if (!combined.includes(extra)) combined.push(extra);
    }
    return combined.join(' && ');
  }

  if (distro === 'rhel') {
    const dnfPackages = new Set();
    const otherCmds = [];
    for (const cmd of rawCommands) {
      const parts = cmd.split('&&').map((s) => s.trim());
      for (const part of parts) {
        const match = part.match(/^dnf\s+install(?:\s+-y)?\s+(.+)$/);
        if (match) {
          match[1].split(/\s+/).forEach((pkg) => {
            if (pkg && !pkg.startsWith('-')) dnfPackages.add(pkg);
          });
        } else {
          otherCmds.push(part);
        }
      }
    }
    const combined = [];
    if (dnfPackages.size > 0) {
      combined.push(`dnf install -y ${Array.from(dnfPackages).join(' ')}`);
    }
    for (const extra of otherCmds) {
      if (!combined.includes(extra)) combined.push(extra);
    }
    return combined.join(' && ');
  }

  if (distro === 'windows') {
    const wingetPkgs = new Set();
    const otherCmds = [];
    for (const cmd of rawCommands) {
      const parts = cmd.split(';').map((s) => s.trim());
      for (const part of parts) {
        const match = part.match(/^winget\s+install\s+(.+)$/);
        if (match) {
          wingetPkgs.add(match[1]);
        } else {
          otherCmds.push(part);
        }
      }
    }
    const combined = [];
    for (const pkg of wingetPkgs) {
      combined.push(`winget install ${pkg}`);
    }
    for (const extra of otherCmds) {
      if (!combined.includes(extra)) combined.push(extra);
    }
    return combined.join(' ; ');
  }

  return rawCommands.join(' && ');
}
