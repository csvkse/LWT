export function buildFileBreadcrumbs(path) {
  const nativeRoot = path.match(/^[a-zA-Z]:\//)?.[0] || path.match(/^\/\/[^/]+\/[^/]+\//)?.[0];
  const root = nativeRoot || '/';
  const parts = [{ name: nativeRoot ? '磁盘' : '/', path: '/' }];
  if (nativeRoot) parts.push({ name: root, path: root });
  let current = root.replace(/\/$/, '');
  for (const name of path.slice(root.length).split('/').filter(Boolean)) {
    current += '/' + name;
    parts.push({ name, path: current });
  }
  return parts;
}

export function normalizeSlashes(path) {
  if (!path) return '';
  return path.replace(/\\+/g, '/').replace(/\/+/g, '/');
}

export function getRelativePath(basePath, fullPath) {
  const normBase = normalizeSlashes(basePath || '').replace(/\/+$/, '');
  const normFull = normalizeSlashes(fullPath || '').replace(/\/+$/, '');
  if (!normBase || !normFull) return normFull;
  if (normFull.toLowerCase() === normBase.toLowerCase()) return '';
  if (normFull.toLowerCase().startsWith(normBase.toLowerCase() + '/')) {
    return normFull.slice(normBase.length + 1);
  }
  return normFull;
}

export function inferExcludeRuleOptions(basePath, entry) {
  const rel = getRelativePath(basePath, entry.path);
  const isDir = Boolean(entry.isDir || entry.isDirectory);
  const name = entry.name || (rel ? rel.split('/').pop() : '');
  const options = [];

  if (isDir) {
    options.push({
      label: `全局目录: ${name}/`,
      pattern: `${name}/`,
      description: `排除任意层级名为 "${name}" 的整个子目录`
    });
    if (rel && rel !== name) {
      options.push({
        label: `指定路径: ${rel}/`,
        pattern: `${rel}/`,
        description: `仅排除相对根路径下的 "${rel}/" 目录`
      });
    }
  } else {
    options.push({
      label: `指定文件: ${rel}`,
      pattern: rel,
      description: `仅排除位于 "${rel}" 的特定文件`
    });
    if (rel !== name) {
      options.push({
        label: `全局同名: ${name}`,
        pattern: name,
        description: `排除任意层级下名为 "${name}" 的文件`
      });
    }
    const extIdx = name.lastIndexOf('.');
    if (extIdx > 0 && extIdx < name.length - 1) {
      const ext = name.slice(extIdx);
      options.push({
        label: `同类后缀: *${ext}`,
        pattern: `*${ext}`,
        description: `排除所有后缀为 "${ext}" 的文件`
      });
    }
  }

  return {
    relPath: rel,
    name,
    isDir,
    options
  };
}
