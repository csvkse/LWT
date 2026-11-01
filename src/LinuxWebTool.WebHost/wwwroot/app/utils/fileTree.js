/**
 * 插件中心代码编辑器文件树适配器与折叠状态机 (File Tree Adapter & Folding State Machine)
 * 采用适配器模式将平铺的相对路径字符串数组转化为多叉层次树，
 * 结合状态机管理目录折叠/展开与当前活动文件的智能祖先聚焦。
 */
import { computed, ref, watch } from 'vue';

/**
 * 根据文件名与目录状态获取语义化图标
 */
export function getFileIcon(name, isDir, isExpanded) {
  if (isDir) {
    return isExpanded ? '📂' : '📁';
  }
  const ext = (name.split('.').pop() || '').toLowerCase();
  switch (ext) {
    case 'js':
    case 'mjs':
    case 'cjs':
    case 'ts':
      return '🟨';
    case 'json':
      return '🟩';
    case 'html':
      return '🟧';
    case 'css':
    case 'scss':
      return '🟦';
    case 'md':
    case 'txt':
      return '📝';
    case 'sh':
    case 'bash':
    case 'py':
      return '⚡';
    case 'bak':
      return '📦';
    default:
      return '📄';
  }
}

/**
 * 适配器：将平铺文件路径列表构造为层次多叉树
 * @param {string[]} paths 平铺路径数组 (如 ['src/index.js', 'manifest.json'])
 * @returns 根节点
 */
export function buildFileTree(paths) {
  const root = { name: '', fullPath: '', isDir: true, children: new Map() };

  for (const rawPath of paths || []) {
    if (!rawPath) continue;
    const norm = String(rawPath).replace(/\\+/g, '/').replace(/^\/+/, '');
    const parts = norm.split('/').filter(Boolean);
    if (parts.length === 0) continue;

    let current = root;
    let accumulatedPath = '';

    for (let i = 0; i < parts.length; i++) {
      const part = parts[i];
      const isLast = i === parts.length - 1;
      accumulatedPath = accumulatedPath ? `${accumulatedPath}/${part}` : part;

      if (!current.children.has(part)) {
        current.children.set(part, {
          name: part,
          fullPath: accumulatedPath,
          isDir: !isLast,
          children: isLast ? null : new Map(),
        });
      } else if (!isLast && !current.children.get(part).isDir) {
        // 兼容同名冲突容错
        current.children.get(part).isDir = true;
        current.children.get(part).children = current.children.get(part).children || new Map();
      }
      current = current.children.get(part);
    }
  }

  // 排序：目录优先 + 字母升序 (Folders First Strategy)
  function sortNodeChildren(node) {
    if (!node.children || node.children.size === 0) return [];
    const list = Array.from(node.children.values());
    list.sort((a, b) => {
      if (a.isDir && !b.isDir) return -1;
      if (!a.isDir && b.isDir) return 1;
      return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
    });
    for (const item of list) {
      if (item.isDir) {
        item.sortedChildren = sortNodeChildren(item);
      }
    }
    return list;
  }

  root.sortedChildren = sortNodeChildren(root);
  return root;
}

/**
 * 扁平深度优先遍历：根据展开目录集合提取可视节点
 */
export function flattenFileTree(rootNode, expandedSet, depth = 0) {
  const result = [];
  const children = rootNode.sortedChildren || [];

  for (const child of children) {
    const isExpanded = child.isDir && expandedSet.has(child.fullPath);
    result.push({
      name: child.name,
      fullPath: child.fullPath,
      isDir: child.isDir,
      depth,
      isExpanded,
      icon: getFileIcon(child.name, child.isDir, isExpanded),
      childCount: child.isDir && child.sortedChildren ? child.sortedChildren.length : 0,
    });

    if (child.isDir && isExpanded) {
      const subNodes = flattenFileTree(child, expandedSet, depth + 1);
      result.push(...subNodes);
    }
  }
  return result;
}

/**
 * 获取指定文件路径的所有祖先目录全路径
 */
export function getAncestorDirectories(filePath) {
  if (!filePath) return [];
  const norm = String(filePath).replace(/\\+/g, '/').replace(/^\/+/, '');
  const parts = norm.split('/').filter(Boolean);
  if (parts.length <= 1) return [];

  const ancestors = [];
  let current = '';
  for (let i = 0; i < parts.length - 1; i++) {
    current = current ? `${current}/${parts[i]}` : parts[i];
    ancestors.push(current);
  }
  return ancestors;
}

/**
 * 响应式文件树组合式钩子 (Composable)
 * @param {import('vue').Ref<string[]>} fileListRef 平铺文件列表响应式引用
 * @param {import('vue').Ref<string>} activeFileRef 当前活动文件响应式引用
 */
export function useFileTree(fileListRef, activeFileRef) {
  const expandedFolders = ref(new Set());

  const tree = computed(() => buildFileTree(fileListRef.value || []));

  // 获取整棵树的所有目录路径
  function collectAllDirectories(node, collected = []) {
    for (const child of node.sortedChildren || []) {
      if (child.isDir) {
        collected.push(child.fullPath);
        collectAllDirectories(child, collected);
      }
    }
    return collected;
  }

  // 状态机自愈聚焦：当当前文件或文件列表变化时，自动展开其所有祖先目录
  watch(
    [activeFileRef, fileListRef],
    ([active]) => {
      if (active) {
        const ancestors = getAncestorDirectories(active);
        if (ancestors.length > 0) {
          const next = new Set(expandedFolders.value);
          let changed = false;
          for (const a of ancestors) {
            if (!next.has(a)) {
              next.add(a);
              changed = true;
            }
          }
          if (changed) {
            expandedFolders.value = next;
          }
        }
      }
    },
    { immediate: true }
  );

  function toggleFolder(dirPath) {
    const next = new Set(expandedFolders.value);
    if (next.has(dirPath)) {
      next.delete(dirPath);
    } else {
      next.add(dirPath);
    }
    expandedFolders.value = next;
  }

  function expandAll() {
    expandedFolders.value = new Set(collectAllDirectories(tree.value));
  }

  function collapseAll() {
    // 若有当前选中文件，仍保留当前文件的直接链路，否则完全折叠
    if (activeFileRef.value) {
      expandedFolders.value = new Set(getAncestorDirectories(activeFileRef.value));
    } else {
      expandedFolders.value = new Set();
    }
  }

  const visibleNodes = computed(() => {
    return flattenFileTree(tree.value, expandedFolders.value);
  });

  return {
    tree,
    visibleNodes,
    expandedFolders,
    toggleFolder,
    expandAll,
    collapseAll,
    getFileIcon,
  };
}
