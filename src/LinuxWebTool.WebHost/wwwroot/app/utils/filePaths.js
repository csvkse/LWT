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
