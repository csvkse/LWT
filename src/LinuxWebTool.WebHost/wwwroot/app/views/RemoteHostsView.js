import { defineComponent, ref, reactive, computed, onMounted, onBeforeUnmount, nextTick } from 'vue';
import { Terminal } from 'xterm';
import { FitAddon } from '@xterm/addon-fit';
import { http, httpDownload } from '../api/client.js';
import { API } from '../config.js';
import { auth } from '../store/auth.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import { formatTime, formatBytes } from '../utils/format.js';

const CAMPBELL_THEME = {
  background: '#0c0c0c',
  foreground: '#cccccc',
  cursor: '#38bdf8',
  cursorAccent: '#0c0c0c',
  selectionBackground: 'rgba(56, 189, 248, 0.28)',
  black: '#0c0c0c',
  red: '#c50f1f',
  green: '#13a10e',
  yellow: '#c19c00',
  blue: '#0037da',
  magenta: '#881798',
  cyan: '#3a96dd',
  white: '#cccccc',
  brightBlack: '#767676',
  brightRed: '#e74856',
  brightGreen: '#16c60c',
  brightYellow: '#f9f1a5',
  brightBlue: '#3b78ff',
  brightMagenta: '#b4009e',
  brightCyan: '#61d6d6',
  brightWhite: '#f2f2f2',
};

const PROTOCOL_OPTIONS = [
  { value: 'ssh', label: 'SSH (标准终端 & SFTP)', available: true, badge: 'SSH' },
  { value: 'winrm', label: 'WinRM (Windows 远程管理 - 规划中)', available: false, badge: 'WinRM' },
  { value: 'telnet', label: 'Telnet (网络设备协议 - 规划中)', available: false, badge: 'Telnet' },
  { value: 'agent', label: 'LWT Agent (轻量代理 - 规划中)', available: false, badge: 'Agent' },
];

const QUICK_COMMANDS = [
  { label: '系统概况', cmd: 'uname -a && uptime\n' },
  { label: '磁盘占用', cmd: 'df -h\n' },
  { label: '内存状态', cmd: 'free -h\n' },
  { label: '网络地址', cmd: 'ip a || ifconfig\n' },
  { label: 'Docker 容器', cmd: 'docker ps\n' },
  { label: '清屏', cmd: 'clear\n' },
];

const EXEC_PRESETS = [
  { label: '系统信息', cmd: 'uname -a' },
  { label: '发行版版本', cmd: 'cat /etc/os-release' },
  { label: '磁盘空间', cmd: 'df -h' },
  { label: '内存情况', cmd: 'free -m' },
  { label: '高占用进程', cmd: 'ps aux --sort=-%mem | head -n 15' },
  { label: '系统运行时间', cmd: 'uptime' },
];

function emptyForm() {
  return {
    name: '',
    protocol: 'ssh',
    host: '',
    port: 22,
    authType: 'Password',
    username: 'root',
    credential: '',
    passphrase: '',
    hostKeyFingerprint: '',
    tags: '',
    remark: '',
    sortOrder: 0,
  };
}

export default defineComponent({
  name: 'RemoteHostsView',
  setup() {
    const hosts = ref([]);
    const loading = ref(false);
    const searchQuery = ref('');
    const protocolFilter = ref('all');
    const testingMap = reactive({});
    const latenciesMap = reactive({});

    // 主机编辑弹窗状态
    const showEditModal = ref(false);
    const isEdit = ref(false);
    const editingId = ref(null);
    const form = reactive(emptyForm());
    const probingKey = ref(false);
    const testingForm = ref(false);
    const formTestResult = ref(null);
    const showPassword = ref(false);

    // 交互终端状态
    const showTerminalModal = ref(false);
    const terminalHost = ref(null);
    const terminalSessionId = ref(null);
    const terminalStatus = ref('connecting'); // 'connecting' | 'connected' | 'disconnected' | 'error'
    const terminalError = ref('');
    const terminalContainer = ref(null);
    let termInstance = null;
    let fitAddonInstance = null;
    let wsInstance = null;
    let resizeObserverInstance = null;

    // 快捷命令执行状态
    const showExecModal = ref(false);
    const execHost = ref(null);
    const execCommand = ref('uname -a');
    const execTimeout = ref(30);
    const executing = ref(false);
    const execResult = ref(null);

    // 远程文件管理状态
    const showFilesModal = ref(false);
    const filesHost = ref(null);
    const currentPath = ref('/');
    const pathInput = ref('/');
    const filesLoading = ref(false);
    const filesError = ref('');
    const fileEntries = ref([]);
    const selectedEntry = ref(null);

    // 文件编辑器
    const showEditorModal = ref(false);
    const editorPath = ref('');
    const editorName = ref('');
    const editorContent = ref('');
    const editorLoading = ref(false);
    const editorSaving = ref(false);

    // 新建文件夹弹窗
    const showMkdirModal = ref(false);
    const newFolderName = ref('');

    // 重命名弹窗
    const showRenameModal = ref(false);
    const renameSourcePath = ref('');
    const renameSourceName = ref('');
    const renameDestName = ref('');

    // 文件上传
    const fileUploadInput = ref(null);

    // 复制弹窗
    const showCopyModal = ref(false);
    const copySourcePath = ref('');
    const copySourceName = ref('');
    const copyDestPath = ref('');
    const copySaving = ref(false);

    // 移动弹窗
    const showMoveModal = ref(false);
    const moveSourcePath = ref('');
    const moveSourceName = ref('');
    const moveDestPath = ref('');
    const moveSaving = ref(false);

    // 复制到剪贴板
    function copyText(text) {
      if (!text) return;
      if (navigator?.clipboard?.writeText) {
        navigator.clipboard.writeText(text)
          .then(() => toast.success('已复制到剪贴板'))
          .catch(() => toast.info(text));
      } else {
        toast.info(text);
      }
    }

    // 筛选后主机列表
    const filteredHosts = computed(() => {
      const q = searchQuery.value.trim().toLowerCase();
      const p = protocolFilter.value;
      return hosts.value.filter((h) => {
        if (p !== 'all' && h.protocol !== p) return false;
        if (!q) return true;
        return (
          h.name?.toLowerCase().includes(q) ||
          h.host?.toLowerCase().includes(q) ||
          h.tags?.toLowerCase().includes(q) ||
          h.remark?.toLowerCase().includes(q) ||
          h.username?.toLowerCase().includes(q)
        );
      });
    });

    // 加载主机列表
    async function loadHosts() {
      loading.value = true;
      try {
        const res = await http(API.remoteHosts.list, { method: 'GET' });
        if (res.ok && Array.isArray(res.data)) {
          hosts.value = res.data;
        } else {
          toast.error(res.message || '获取远程主机列表失败');
        }
      } catch (err) {
        toast.error(err?.message || '获取远程主机列表异常');
      } finally {
        loading.value = false;
      }
    }

    // 打开添加主机弹窗
    function openAddModal() {
      isEdit.value = false;
      editingId.value = null;
      Object.assign(form, emptyForm());
      formTestResult.value = null;
      showPassword.value = false;
      showEditModal.value = true;
    }

    // 打开编辑主机弹窗
    function openEditModal(host) {
      isEdit.value = true;
      editingId.value = host.id;
      Object.assign(form, {
        name: host.name,
        protocol: host.protocol || 'ssh',
        host: host.host,
        port: host.port || 22,
        authType: host.authType || 'Password',
        username: host.username || 'root',
        credential: '',
        passphrase: '',
        hostKeyFingerprint: host.hostKeyFingerprint || '',
        tags: host.tags || '',
        remark: host.remark || '',
        sortOrder: host.sortOrder || 0,
      });
      formTestResult.value = null;
      showPassword.value = false;
      showEditModal.value = true;
    }

    // 弹窗内探测主机指纹
    async function probeFingerprint() {
      if (!form.host?.trim()) {
        toast.error('请先输入主机地址');
        return;
      }
      probingKey.value = true;
      try {
        const res = await http(API.remoteHosts.probeKey, {
          method: 'POST',
          body: { host: form.host.trim(), port: Number(form.port) || 22 },
        });
        if (res.ok && res.data?.success) {
          form.hostKeyFingerprint = res.data.fingerprint;
          toast.success(`探测成功 (${res.data.keyType || 'SSH'})`);
        } else {
          toast.error(res.data?.errorMessage || res.message || '探测指纹失败');
        }
      } catch (err) {
        toast.error(err?.message || '探测指纹网络异常');
      } finally {
        probingKey.value = false;
      }
    }

    // 弹窗内测试连通性
    async function testFormConnection() {
      if (!form.host?.trim()) {
        toast.error('请先输入主机地址');
        return;
      }
      testingForm.value = true;
      formTestResult.value = null;
      try {
        const res = await http(API.remoteHosts.test, {
          method: 'POST',
          body: {
            id: isEdit.value ? editingId.value : undefined,
            host: form.host.trim(),
            port: Number(form.port) || 22,
            protocol: form.protocol,
            authType: form.authType,
            username: form.username.trim(),
            credential: form.credential || undefined,
            passphrase: form.passphrase || undefined,
          },
          timeoutMs: 15000,
        });
        if (res.ok && res.data?.success) {
          formTestResult.value = { success: true, latencyMs: res.data.latencyMs };
          toast.success(`连接成功，耗时 ${res.data.latencyMs} ms`);
        } else {
          formTestResult.value = {
            success: false,
            error: res.data?.errorMessage || res.message || '连接失败，请检查配置',
          };
          toast.error(formTestResult.value.error);
        }
      } catch (err) {
        formTestResult.value = { success: false, error: err?.message || '网络请求异常' };
        toast.error(formTestResult.value.error);
      } finally {
        testingForm.value = false;
      }
    }

    // 保存主机信息
    async function saveHost() {
      if (!form.name?.trim() || !form.host?.trim()) {
        toast.error('请填写主机名称与主机地址');
        return;
      }
      if (!isEdit.value && !form.credential?.trim()) {
        toast.error('请填写访问凭据（密码或私钥）');
        return;
      }

      const payload = {
        name: form.name.trim(),
        protocol: form.protocol,
        host: form.host.trim(),
        port: Number(form.port) || 22,
        authType: form.authType,
        username: form.username.trim() || 'root',
        credential: form.credential ? form.credential.trim() : undefined,
        passphrase: form.passphrase ? form.passphrase.trim() : undefined,
        hostKeyFingerprint: form.hostKeyFingerprint?.trim() || undefined,
        tags: form.tags?.trim() || undefined,
        remark: form.remark?.trim() || undefined,
        sortOrder: Number(form.sortOrder) || 0,
      };

      try {
        let res;
        if (isEdit.value && editingId.value) {
          res = await http(API.remoteHosts.item(editingId.value), {
            method: 'PUT',
            body: payload,
          });
        } else {
          res = await http(API.remoteHosts.list, {
            method: 'POST',
            body: payload,
          });
        }

        if (res.ok) {
          toast.success(isEdit.value ? '主机配置已更新' : '已成功添加远程主机');
          showEditModal.value = false;
          await loadHosts();
        } else {
          toast.error(res.message || '保存失败');
        }
      } catch (err) {
        toast.error(err?.message || '保存请求失败');
      }
    }

    // 卡片列表内单独测试连通性
    async function testHostInline(host) {
      testingMap[host.id] = true;
      try {
        const res = await http(API.remoteHosts.testHost ? API.remoteHosts.testHost(host.id) : API.remoteHosts.test, {
          method: 'POST',
          body: {
            id: host.id,
            host: host.host,
            port: host.port,
            protocol: host.protocol,
            authType: host.authType,
            username: host.username,
          },
          timeoutMs: 15000,
        });
        if (res.ok && res.data?.success) {
          latenciesMap[host.id] = { success: true, latencyMs: res.data.latencyMs };
          toast.success(`${host.name} 连接畅通 (${res.data.latencyMs} ms)`);
        } else {
          latenciesMap[host.id] = {
            success: false,
            error: res.data?.errorMessage || res.message || '连接失败',
          };
          toast.error(`${host.name} 连接异常: ${latenciesMap[host.id].error}`);
        }
      } catch (err) {
        latenciesMap[host.id] = { success: false, error: err?.message || '请求失败' };
        toast.error(`${host.name} 网络测试异常`);
      } finally {
        testingMap[host.id] = false;
      }
    }

    // 删除主机
    function deleteHost(host) {
      openConfirm({
        title: '删除远程主机',
        message: `确定要移除远程主机「${host.name}」(${host.host}:${host.port}) 吗？其关联的会话将同步失效。`,
        confirmText: '确认删除',
        danger: true,
        onConfirm: async () => {
          try {
            const res = await http(API.remoteHosts.item(host.id), { method: 'DELETE' });
            if (res.ok) {
              toast.success('远程主机已删除');
              await loadHosts();
            } else {
              toast.error(res.message || '删除失败');
            }
          } catch (err) {
            toast.error(err?.message || '删除请求失败');
          }
        },
      });
    }

    // ----------------- Web 终端模块 -----------------

    async function openTerminal(host) {
      terminalHost.value = host;
      terminalStatus.value = 'connecting';
      terminalError.value = '';
      showTerminalModal.value = true;
      await nextTick();
      await initTerminalSession();
    }

    async function initTerminalSession() {
      cleanupTerminal();
      if (!terminalHost.value || !terminalContainer.value) return;

      try {
        termInstance = new Terminal({
          fontFamily: "'Cascadia Mono', 'SF Mono', 'Consolas', 'JetBrains Mono', monospace",
          fontSize: 13,
          lineHeight: 1.25,
          cursorBlink: true,
          cursorStyle: 'block',
          theme: CAMPBELL_THEME,
          allowTransparency: false,
          convertEol: true,
        });

        fitAddonInstance = new FitAddon();
        termInstance.loadAddon(fitAddonInstance);
        termInstance.open(terminalContainer.value);
        fitAddonInstance.fit();

        termInstance.writeln('\x1b[36m>>> 正在建立安全远程终端会话 (' + terminalHost.value.host + ':' + terminalHost.value.port + ')...\x1b[0m');

        const sessionRes = await http(API.remoteHosts.terminalSession(terminalHost.value.id), {
          method: 'POST',
          body: {
            columns: termInstance.cols || 80,
            rows: termInstance.rows || 24,
          },
        });

        if (!sessionRes.ok || !sessionRes.data?.sessionId) {
          throw new Error(sessionRes.message || '创建终端会话失败');
        }

        terminalSessionId.value = sessionRes.data.sessionId;

        // 连接 WebSocket
        const wsUrl = API.remoteHosts.ws(terminalHost.value.id, terminalSessionId.value, auth.token);
        wsInstance = new WebSocket(wsUrl);

        wsInstance.onopen = () => {
          terminalStatus.value = 'connected';
          termInstance.writeln('\x1b[32m>>> 远程会话已就绪。\x1b[0m\r\n');
          termInstance.focus();
        };

        wsInstance.onmessage = (event) => {
          if (termInstance && event.data) {
            termInstance.write(event.data);
          }
        };

        wsInstance.onerror = () => {
          terminalStatus.value = 'error';
          terminalError.value = 'WebSocket 终端流连接异常';
          if (termInstance) {
            termInstance.writeln('\r\n\x1b[31m>>> 终端网络通道发生异常。\x1b[0m');
          }
        };

        wsInstance.onclose = () => {
          terminalStatus.value = 'disconnected';
          if (termInstance) {
            termInstance.writeln('\r\n\x1b[33m>>> 远程终端会话已断开连接。\x1b[0m');
          }
        };

        termInstance.onData((data) => {
          if (wsInstance && wsInstance.readyState === WebSocket.OPEN) {
            wsInstance.send(data);
          }
        });

        // 剪贴板支持
        termInstance.attachCustomKeyEventHandler((event) => {
          const mod = event.ctrlKey || event.metaKey;
          const key = event.key ? event.key.toLowerCase() : '';

          if (mod && key === 'c') {
            if (termInstance.hasSelection()) {
              const selectedText = termInstance.getSelection();
              if (navigator.clipboard && selectedText) {
                navigator.clipboard.writeText(selectedText).catch(() => {});
              }
              termInstance.clearSelection();
              event.preventDefault();
              return false;
            }
            return true;
          }

          if (mod && key === 'v') {
            event.preventDefault();
            if (navigator.clipboard) {
              navigator.clipboard.readText().then((text) => {
                if (text && wsInstance && wsInstance.readyState === WebSocket.OPEN) {
                  wsInstance.send(text);
                }
              }).catch(() => {});
            }
            return false;
          }
          return true;
        });

        // 监听尺寸自适应
        resizeObserverInstance = new ResizeObserver(() => {
          if (fitAddonInstance && termInstance && terminalContainer.value?.clientWidth > 0) {
            try {
              fitAddonInstance.fit();
            } catch {}
          }
        });
        resizeObserverInstance.observe(terminalContainer.value);

      } catch (err) {
        terminalStatus.value = 'error';
        terminalError.value = err?.message || '初始化终端异常';
        if (termInstance) {
          termInstance.writeln('\r\n\x1b[31m>>> 错误: ' + terminalError.value + '\x1b[0m');
        }
      }
    }

    function sendQuickCmd(cmd) {
      if (wsInstance && wsInstance.readyState === WebSocket.OPEN) {
        wsInstance.send(cmd);
        if (termInstance) termInstance.focus();
      } else {
        toast.info('终端未处于连接状态');
      }
    }

    function cleanupTerminal() {
      if (resizeObserverInstance) {
        resizeObserverInstance.disconnect();
        resizeObserverInstance = null;
      }
      if (wsInstance) {
        try { wsInstance.close(); } catch {}
        wsInstance = null;
      }
      if (termInstance) {
        try { termInstance.dispose(); } catch {}
        termInstance = null;
      }
      fitAddonInstance = null;

      if (terminalHost.value && terminalSessionId.value) {
        const hid = terminalHost.value.id;
        const sid = terminalSessionId.value;
        http(API.remoteHosts.closeTerminalSession(hid, sid), { method: 'DELETE' }).catch(() => {});
      }
      terminalSessionId.value = null;
    }

    function closeTerminalModal() {
      cleanupTerminal();
      showTerminalModal.value = false;
      terminalHost.value = null;
    }

    async function reconnectTerminal() {
      await initTerminalSession();
    }

    // ----------------- 非交互指令执行模块 -----------------

    function openExecModal(host) {
      execHost.value = host;
      execResult.value = null;
      showExecModal.value = true;
    }

    function closeExecModal() {
      showExecModal.value = false;
      execHost.value = null;
    }

    function stripAnsi(text) {
      if (!text) return '';
      return String(text)
        .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\|$)/g, '')
        .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
        .replace(/\]133;[A-Za-z0-9;=._-]*/g, '')
        .replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/g, '')
        .trim();
    }

    function applyExecPreset(cmd) {
      execCommand.value = cmd;
    }

    async function runExecCommand() {
      if (!execHost.value || !execCommand.value?.trim()) {
        toast.error('请输入需要执行的命令');
        return;
      }
      executing.value = true;
      execResult.value = null;
      try {
        const res = await http(API.remoteHosts.terminalExec(execHost.value.id), {
          method: 'POST',
          body: {
            command: execCommand.value.trim(),
            timeoutSeconds: Number(execTimeout.value) || 30,
          },
        });
        if (res.ok && res.data) {
          execResult.value = {
            ...res.data,
            output: stripAnsi(res.data.output),
            errorOutput: stripAnsi(res.data.errorOutput || res.data.error || ''),
          };
          if (res.data.exitCode === 0) {
            toast.success('指令执行完成 (耗时 ' + res.data.durationMs + ' ms)');
          } else {
            toast.info('指令执行退出，退出码: ' + res.data.exitCode);
          }
        } else {
          toast.error(res.message || '指令执行失败');
        }
      } catch (err) {
        toast.error(err?.message || '指令执行请求异常');
      } finally {
        executing.value = false;
      }
    }

    // ----------------- SFTP 远端文件管理模块 -----------------

    function openFilesModal(host) {
      filesHost.value = host;
      currentPath.value = '/';
      pathInput.value = '/';
      fileEntries.value = [];
      filesError.value = '';
      showFilesModal.value = true;
      loadFiles('/');
    }

    function closeFilesModal() {
      showFilesModal.value = false;
      filesHost.value = null;
      fileEntries.value = [];
    }

    async function loadFiles(path) {
      if (!filesHost.value) return;
      const target = path || currentPath.value || '/';
      filesLoading.value = true;
      filesError.value = '';
      try {
        const res = await http(API.remoteHosts.files(filesHost.value.id), {
          method: 'GET',
          params: { path: target },
          timeoutMs: 20000,
        });
        if (res.ok && res.data) {
          currentPath.value = res.data.currentPath || target;
          pathInput.value = currentPath.value;
          fileEntries.value = (res.data.entries || []).slice().sort((a, b) => {
            if (a.isDirectory && !b.isDirectory) return -1;
            if (!a.isDirectory && b.isDirectory) return 1;
            return a.name.localeCompare(b.name);
          });
        } else {
          filesError.value = res.message || '获取远程文件列表失败';
          toast.error(filesError.value);
        }
      } catch (err) {
        filesError.value = err?.message || '加载远程文件发生异常';
        toast.error(filesError.value);
      } finally {
        filesLoading.value = false;
      }
    }

    function navigateToFile(entry) {
      if (entry.isDirectory) {
        loadFiles(entry.path);
      } else {
        openFileEditor(entry);
      }
    }

    function goUpDir() {
      if (currentPath.value === '/' || !currentPath.value) return;
      const parts = currentPath.value.split('/').filter(Boolean);
      parts.pop();
      const parent = '/' + parts.join('/');
      loadFiles(parent);
    }

    function jumpToPath() {
      const target = pathInput.value?.trim() || '/';
      loadFiles(target);
    }

    const breadcrumbSegments = computed(() => {
      const p = currentPath.value || '/';
      if (p === '/') return [{ name: '根目录', path: '/' }];
      const segments = [{ name: '根目录', path: '/' }];
      const parts = p.split('/').filter(Boolean);
      let acc = '';
      for (const part of parts) {
        acc += '/' + part;
        segments.push({ name: part, path: acc });
      }
      return segments;
    });

    // 查看与编辑文本文件
    async function openFileEditor(entry) {
      editorPath.value = entry.path;
      editorName.value = entry.name;
      editorContent.value = '';
      showEditorModal.value = true;
      editorLoading.value = true;
      try {
        const res = await http(API.remoteHosts.fileContent(filesHost.value.id), {
          method: 'GET',
          params: { path: entry.path },
          timeoutMs: 30000,
        });
        if (res.ok && res.data) {
          editorContent.value = res.data.content ?? '';
        } else {
          toast.error(res.message || '读取文件内容失败');
          showEditorModal.value = false;
        }
      } catch (err) {
        toast.error(err?.message || '读取文件内容异常');
        showEditorModal.value = false;
      } finally {
        editorLoading.value = false;
      }
    }

    function closeFileEditor() {
      showEditorModal.value = false;
      editorContent.value = '';
    }

    async function saveFileEditor() {
      if (!filesHost.value || !editorPath.value) return;
      editorSaving.value = true;
      try {
        const res = await http(API.remoteHosts.fileContent(filesHost.value.id), {
          method: 'PUT',
          body: {
            path: editorPath.value,
            content: editorContent.value,
          },
          timeoutMs: 30000,
        });
        if (res.ok) {
          toast.success('文件保存成功');
          showEditorModal.value = false;
          await loadFiles(currentPath.value);
        } else {
          toast.error(res.message || '保存失败');
        }
      } catch (err) {
        toast.error(err?.message || '保存文件请求异常');
      } finally {
        editorSaving.value = false;
      }
    }

    // 新建文件夹
    function openMkdirDialog() {
      newFolderName.value = '';
      showMkdirModal.value = true;
    }

    async function saveMkdir() {
      const name = newFolderName.value.trim();
      if (!name) {
        toast.error('请输入文件夹名称');
        return;
      }
      const dirPath = currentPath.value === '/' ? `/${name}` : `${currentPath.value}/${name}`;
      try {
        const res = await http(API.remoteHosts.fileAction(filesHost.value.id), {
          method: 'POST',
          body: {
            action: 'mkdir',
            path: dirPath,
          },
          timeoutMs: 30000,
        });
        if (res.ok) {
          toast.success('文件夹创建成功');
          showMkdirModal.value = false;
          await loadFiles(currentPath.value);
        } else {
          toast.error(res.message || '创建文件夹失败');
        }
      } catch (err) {
        toast.error(err?.message || '创建文件夹请求异常');
      }
    }

    // 重命名文件或目录
    function openRenameDialog(entry) {
      renameSourcePath.value = entry.path;
      renameSourceName.value = entry.name;
      renameDestName.value = entry.name;
      showRenameModal.value = true;
    }

    async function saveRename() {
      const dest = renameDestName.value.trim();
      if (!dest || dest === renameSourceName.value) {
        showRenameModal.value = false;
        return;
      }
      const parent = renameSourcePath.value.slice(0, renameSourcePath.value.lastIndexOf('/')) || '/';
      const destPath = parent === '/' ? `/${dest}` : `${parent}/${dest}`;

      try {
        const res = await http(API.remoteHosts.fileAction(filesHost.value.id), {
          method: 'POST',
          body: {
            action: 'rename',
            path: renameSourcePath.value,
            destination: destPath,
          },
          timeoutMs: 30000,
        });
        if (res.ok) {
          toast.success('重命名成功');
          showRenameModal.value = false;
          await loadFiles(currentPath.value);
        } else {
          toast.error(res.message || '重命名失败');
        }
      } catch (err) {
        toast.error(err?.message || '重命名操作异常');
      }
    }

    // 删除文件或目录
    function deleteEntry(entry) {
      openConfirm({
        title: entry.isDirectory ? '删除远程目录' : '删除远程文件',
        message: `确定要删除「${entry.name}」吗？${entry.isDirectory ? '目录内部的所有子文件与文件夹将被递归清除，且不可恢复！' : '此操作不可恢复！'}`,
        confirmText: '确认删除',
        danger: true,
        onConfirm: async () => {
          try {
            const res = await http(API.remoteHosts.fileAction(filesHost.value.id), {
              method: 'POST',
              body: {
                action: 'delete',
                path: entry.path,
                recursive: entry.isDirectory,
              },
              timeoutMs: 30000,
            });
            if (res.ok) {
              toast.success('删除成功');
              await loadFiles(currentPath.value);
            } else {
              toast.error(res.message || '删除失败');
            }
          } catch (err) {
            toast.error(err?.message || '删除请求失败');
          }
        },
      });
    }

    // 文件上传
    function triggerFileUpload() {
      fileUploadInput.value?.click();
    }

    async function onFileSelected(e) {
      const file = e.target.files?.[0];
      if (!file || !filesHost.value) return;
      const formData = new FormData();
      formData.append('file', file);
      const uploadUrl = API.remoteHosts.fileUpload(filesHost.value.id, currentPath.value || '/');
      filesLoading.value = true;
      try {
        const res = await http(uploadUrl, {
          method: 'POST',
          body: formData,
        });
        if (res.ok) {
          toast.success(`文件「${file.name}」上传成功`);
          await loadFiles(currentPath.value);
        } else {
          toast.error(res.message || '上传失败');
        }
      } catch (err) {
        toast.error(err?.message || '上传请求异常');
      } finally {
        filesLoading.value = false;
        e.target.value = '';
      }
    }

    // 文件下载
    async function downloadEntry(entry) {
      if (!filesHost.value || !entry?.path) return;
      if (entry.isDirectory) {
        toast.warning('暂不支持直接下载整个远程目录，请在远程主机压缩后再下载');
        return;
      }
      toast.info(`正在下载: ${entry.name}...`);
      const url = API.remoteHosts.fileDownload(filesHost.value.id, entry.path);
      const res = await httpDownload(url);
      if (res && res.ok) {
        toast.success(`下载完成: ${res.filename || entry.name}`);
      }
    }

    // 复制弹窗与操作
    function openCopyDialog(entry) {
      copySourcePath.value = entry.path;
      copySourceName.value = entry.name;
      copyDestPath.value = entry.path.endsWith('/') ? entry.path.slice(0, -1) + '_copy' : entry.path + '_copy';
      showCopyModal.value = true;
    }

    async function saveCopy() {
      const dest = copyDestPath.value.trim();
      if (!dest) {
        toast.error('请输入复制目标路径');
        return;
      }
      copySaving.value = true;
      try {
        const res = await http(API.remoteHosts.fileAction(filesHost.value.id), {
          method: 'POST',
          body: {
            action: 'copy',
            path: copySourcePath.value,
            destination: dest,
          },
          timeoutMs: 30000,
        });
        if (res.ok) {
          toast.success('复制成功');
          showCopyModal.value = false;
          await loadFiles(currentPath.value);
        } else {
          toast.error(res.message || '复制失败');
        }
      } catch (err) {
        toast.error(err?.message || '复制操作请求异常');
      } finally {
        copySaving.value = false;
      }
    }

    // 移动弹窗与操作
    function openMoveDialog(entry) {
      moveSourcePath.value = entry.path;
      moveSourceName.value = entry.name;
      moveDestPath.value = entry.path;
      showMoveModal.value = true;
    }

    async function saveMove() {
      const dest = moveDestPath.value.trim();
      if (!dest || dest === moveSourcePath.value) {
        showMoveModal.value = false;
        return;
      }
      moveSaving.value = true;
      try {
        const res = await http(API.remoteHosts.fileAction(filesHost.value.id), {
          method: 'POST',
          body: {
            action: 'move',
            path: moveSourcePath.value,
            destination: dest,
          },
          timeoutMs: 30000,
        });
        if (res.ok) {
          toast.success('移动成功');
          showMoveModal.value = false;
          await loadFiles(currentPath.value);
        } else {
          toast.error(res.message || '移动失败');
        }
      } catch (err) {
        toast.error(err?.message || '移动操作请求异常');
      } finally {
        moveSaving.value = false;
      }
    }

    // 生命周期
    onMounted(() => {
      loadHosts();
    });

    onBeforeUnmount(() => {
      cleanupTerminal();
    });

    return {
      hosts,
      loading,
      searchQuery,
      protocolFilter,
      testingMap,
      latenciesMap,
      filteredHosts,
      loadHosts,
      copyText,
      formatTime,
      formatBytes,

      // 编辑弹窗
      showEditModal,
      isEdit,
      editingId,
      form,
      probingKey,
      testingForm,
      formTestResult,
      showPassword,
      openAddModal,
      openEditModal,
      probeFingerprint,
      testFormConnection,
      saveHost,
      testHostInline,
      deleteHost,
      PROTOCOL_OPTIONS,

      // 终端
      showTerminalModal,
      terminalHost,
      terminalSessionId,
      terminalStatus,
      terminalError,
      terminalContainer,
      openTerminal,
      closeTerminalModal,
      reconnectTerminal,
      sendQuickCmd,
      QUICK_COMMANDS,

      // 非交互执行
      showExecModal,
      execHost,
      execCommand,
      execTimeout,
      executing,
      execResult,
      openExecModal,
      closeExecModal,
      applyExecPreset,
      runExecCommand,
      EXEC_PRESETS,

      // SFTP
      showFilesModal,
      filesHost,
      currentPath,
      pathInput,
      filesLoading,
      filesError,
      fileEntries,
      selectedEntry,
      breadcrumbSegments,
      openFilesModal,
      closeFilesModal,
      loadFiles,
      navigateToFile,
      goUpDir,
      jumpToPath,

      // 文件编辑
      showEditorModal,
      editorPath,
      editorName,
      editorContent,
      editorLoading,
      editorSaving,
      openFileEditor,
      closeFileEditor,
      saveFileEditor,

      // 新建文件夹 & 重命名
      showMkdirModal,
      newFolderName,
      openMkdirDialog,
      saveMkdir,
      showRenameModal,
      renameSourcePath,
      renameSourceName,
      renameDestName,
      openRenameDialog,
      saveRename,
      deleteEntry,

      // 上传与下载
      fileUploadInput,
      triggerFileUpload,
      onFileSelected,
      downloadEntry,

      // 复制与移动
      showCopyModal,
      copySourcePath,
      copySourceName,
      copyDestPath,
      copySaving,
      openCopyDialog,
      saveCopy,
      showMoveModal,
      moveSourcePath,
      moveSourceName,
      moveDestPath,
      moveSaving,
      openMoveDialog,
      saveMove,
    };
  },
  template: `
    <div class="flex flex-col gap-5 p-6 max-w-7xl mx-auto">
      <!-- 顶部标题栏与操作面板 -->
      <div class="flex flex-col md:flex-row md:items-center justify-between gap-4">
        <div>
          <div class="flex items-center gap-3">
            <h1 class="text-xl font-bold text-slate-100 flex items-center gap-2">
              <span class="text-cyan-400">🖥</span> 远程主机管理
            </h1>
            <span class="badge border-cyan-500/50 text-cyan-300 bg-cyan-950/30">多协议纳管</span>
          </div>
          <p class="text-xs text-slate-400 mt-1">
            支持 SSH 凭据与公钥认证、Web PTY 终端交互、非交互式自动化运维与 SFTP 远端文件管理
          </p>
        </div>
        <div class="flex items-center gap-3">
          <button class="btn btn-secondary text-xs" :disabled="loading" @click="loadHosts()">
            <span v-if="loading" class="animate-spin">↻</span>
            <span v-else>↻</span>
            <span>刷新</span>
          </button>
          <button class="btn btn-primary text-xs" @click="openAddModal()">
            <span>＋</span>
            <span>添加远程主机</span>
          </button>
        </div>
      </div>
      <!-- 搜索与协议筛选栏 -->
      <div class="panel p-4 flex flex-col sm:flex-row items-center justify-between gap-3 text-xs">
        <div class="flex items-center gap-2 w-full sm:w-auto">
          <span class="text-slate-400">协议过滤:</span>
          <select v-model="protocolFilter" class="input w-36 py-1 text-xs">
            <option value="all">全部协议</option>
            <option value="ssh">SSH 协议</option>
            <option value="winrm" disabled>WinRM (规划中)</option>
            <option value="telnet" disabled>Telnet (规划中)</option>
            <option value="agent" disabled>LWT Agent (规划中)</option>
          </select>
        </div>
        <div class="w-full sm:w-80">
          <input
            v-model="searchQuery"
            type="text"
            class="input py-1 text-xs"
            placeholder="搜索名称、IP、端口、标签、备注..."
          />
        </div>
      </div>
      <!-- 主机资产列表为空状态 -->
      <div v-if="!loading && filteredHosts.length === 0" class="panel p-12 text-center text-slate-400 flex flex-col items-center gap-3">
        <div class="text-4xl text-slate-600">🖥</div>
        <p class="text-sm">暂无匹配的远程主机资产</p>
        <p class="text-xs text-slate-500">点击右上角「添加远程主机」，配置您的第一台 Linux 或网络服务器</p>
        <button class="btn btn-primary text-xs mt-2" @click="openAddModal()">立即添加</button>
      </div>
      <!-- 主机资产卡片网格 -->
      <div v-else class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        <div
          v-for="host in filteredHosts"
          :key="host.id"
          class="panel p-4 flex flex-col justify-between gap-4 border-slate-700/80 hover:border-cyan-500/50 transition-colors">
          <!-- 卡片头部: 状态指示灯、名称与协议标签 -->
          <div class="flex flex-col gap-2">
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-2 min-w-0">
                <span
                  class="w-2.5 h-2.5 rounded-full shrink-0"
                  :class="latenciesMap[host.id]?.success ? 'bg-emerald-400 shadow-sm shadow-emerald-500/50' : 'bg-cyan-500'"
                ></span>
                <span class="font-semibold text-slate-100 text-sm truncate" :title="host.name">
                  {{ host.name }}
                </span>
              </div>
              <div class="flex items-center gap-1 shrink-0">
                <span class="badge border-cyan-500/40 text-cyan-300 bg-cyan-950/40 text-[10px] uppercase font-mono">
                  {{ host.protocol }}
                </span>
                <span class="badge border-slate-700 text-slate-400 text-[10px]">
                  :{{ host.port }}
                </span>
              </div>
            </div>
            <!-- 网络地址与登录用户 -->
            <div class="flex items-center justify-between text-xs text-slate-400 bg-slate-900/50 px-2.5 py-1.5 rounded border border-slate-800">
              <div class="flex items-center gap-1 font-mono text-slate-300 truncate">
                <span class="text-cyan-400">@</span>
                <span>{{ host.username }}</span>
                <span class="text-slate-500">·</span>
                <span class="truncate">{{ host.host }}</span>
              </div>
              <button
                class="text-[11px] text-cyan-400 hover:text-cyan-300 ml-2 shrink-0 cursor-pointer"
                title="复制主机地址"
                @click="copyText(host.host)">
                复制
              </button>
            </div>
            <!-- 属性元数据: 认证方式、指纹、标签与延迟 -->
            <div class="flex flex-wrap items-center gap-1.5 text-[11px]">
              <span class="badge border-slate-700 text-slate-300">
                {{ host.authType === 'PrivateKey' ? '🔑 密钥认证' : '🔒 密码认证' }}
              </span>
              <span
                v-if="latenciesMap[host.id]?.success"
                class="badge border-emerald-500/40 text-emerald-300 bg-emerald-950/30">
                ⚡ {{ latenciesMap[host.id].latencyMs }} ms
              </span>
              <span
                v-else-if="latenciesMap[host.id]?.error"
                class="badge border-rose-500/40 text-rose-300 bg-rose-950/30 truncate max-w-[120px]"
                :title="latenciesMap[host.id].error">
                ⚠ 连接异常
              </span>
              <span
                v-if="host.hostKeyFingerprint"
                class="badge border-indigo-500/40 text-indigo-300 bg-indigo-950/30 font-mono text-[10px]"
                :title="host.hostKeyFingerprint">
                ✓ 指纹已存
              </span>
              <span
                v-if="host.tags"
                v-for="tag in host.tags.split(',').filter(Boolean)"
                :key="tag"
                class="badge border-cyan-800/60 text-cyan-400/90 text-[10px]">
                #{{ tag.trim() }}
              </span>
            </div>
            <p v-if="host.remark" class="text-xs text-slate-400/80 line-clamp-2 mt-0.5">
              {{ host.remark }}
            </p>
          </div>
          <!-- 卡片底部操作栏 -->
          <div class="flex flex-col gap-2 pt-2 border-t border-slate-800/80">
            <!-- 核心功能入口: 终端 / 指令 / 文件 -->
            <div class="grid grid-cols-3 gap-1.5">
              <button
                class="btn btn-secondary text-xs py-1.5 px-2 flex items-center justify-center gap-1 border-cyan-500/40 hover:bg-cyan-950/40 text-cyan-300"
                title="开启交互式 Web 终端"
                @click="openTerminal(host)">
                <span>💻</span>
                <span>终端</span>
              </button>
              <button
                class="btn btn-secondary text-xs py-1.5 px-2 flex items-center justify-center gap-1 hover:text-slate-100"
                title="非交互式远程运行 Shell 指令"
                @click="openExecModal(host)">
                <span>⚡</span>
                <span>指令</span>
              </button>
              <button
                class="btn btn-secondary text-xs py-1.5 px-2 flex items-center justify-center gap-1 hover:text-slate-100"
                title="浏览 SFTP 远程文件系统"
                @click="openFilesModal(host)">
                <span>📁</span>
                <span>文件</span>
              </button>
            </div>
            <!-- 次要操作: 测速 / 编辑 / 删除 -->
            <div class="flex items-center justify-between text-xs text-slate-400">
              <button
                class="hover:text-cyan-300 flex items-center gap-1 cursor-pointer"
                :disabled="testingMap[host.id]"
                @click="testHostInline(host)">
                <span v-if="testingMap[host.id]" class="animate-spin text-cyan-400">↻</span>
                <span v-else>⚡</span>
                <span>测试连接</span>
              </button>
              <div class="flex items-center gap-3">
                <button
                  class="hover:text-slate-200 cursor-pointer"
                  @click="openEditModal(host)">
                  编辑
                </button>
                <button
                  class="hover:text-rose-400 cursor-pointer"
                  @click="deleteHost(host)">
                  删除
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 1: 添加 / 编辑远程主机 ==================== -->
      <div v-if="showEditModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-xl p-5 flex flex-col gap-4 border-slate-700 max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <h3 class="font-semibold text-slate-100 text-base flex items-center gap-2">
              <span>{{ isEdit ? '编辑远程主机配置' : '添加新的远程主机' }}</span>
              <span class="badge border-cyan-500/50 text-cyan-300 text-[10px]">AES-256-GCM 保护</span>
            </h3>
            <button class="text-slate-400 hover:text-slate-200 text-lg" @click="showEditModal = false">×</button>
          </div>
          <form class="flex flex-col gap-4 text-xs" @submit.prevent="saveHost()">
            <!-- 主机名与协议 -->
            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label class="block text-slate-300 mb-1 font-medium">主机名称 *</label>
                <input
                  v-model="form.name"
                  type="text"
                  required
                  class="input"
                  placeholder="例如: 生产网关 / 备份机器"
                />
              </div>
              <div>
                <label class="block text-slate-300 mb-1 font-medium">连接协议</label>
                <select v-model="form.protocol" class="input">
                  <option
                    v-for="opt in PROTOCOL_OPTIONS"
                    :key="opt.value"
                    :value="opt.value"
                    :disabled="!opt.available">
                    {{ opt.label }}
                  </option>
                </select>
              </div>
            </div>
            <!-- 主机 IP 地址与端口 -->
            <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
              <div class="sm:col-span-2">
                <label class="block text-slate-300 mb-1 font-medium">主机 IP / 域名 *</label>
                <input
                  v-model="form.host"
                  type="text"
                  required
                  class="input font-mono"
                  placeholder="192.168.1.100 或 server.internal"
                />
              </div>
              <div>
                <label class="block text-slate-300 mb-1 font-medium">端口号</label>
                <input
                  v-model.number="form.port"
                  type="number"
                  min="1"
                  max="65535"
                  class="input font-mono"
                  placeholder="22"
                />
              </div>
            </div>
            <!-- 登录凭据配置: 用户名与认证类型 -->
            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label class="block text-slate-300 mb-1 font-medium">登录用户名 *</label>
                <input
                  v-model="form.username"
                  type="text"
                  required
                  class="input font-mono"
                  placeholder="root"
                />
              </div>
              <div>
                <label class="block text-slate-300 mb-1 font-medium">认证方式</label>
                <select v-model="form.authType" class="input">
                  <option value="Password">密码认证 (Password)</option>
                  <option value="PrivateKey">SSH 私钥认证 (Private Key)</option>
                </select>
              </div>
            </div>
            <!-- 凭据输入区 (根据认证方式切换) -->
            <div v-if="form.authType === 'Password'" class="flex flex-col gap-1">
              <div class="flex items-center justify-between">
                <label class="text-slate-300 font-medium">登录密码 {{ isEdit ? '(留空保留原有密码)' : '*' }}</label>
                <button
                  type="button"
                  class="text-cyan-400 hover:text-cyan-300 text-[11px]"
                  @click="showPassword = !showPassword">
                  {{ showPassword ? '隐藏密码' : '显示密码' }}
                </button>
              </div>
              <input
                v-model="form.credential"
                :type="showPassword ? 'text' : 'password'"
                class="input font-mono"
                :required="!isEdit"
                placeholder="请输入服务器远程密码"
              />
            </div>
            <div v-else class="flex flex-col gap-3">
              <div>
                <label class="block text-slate-300 mb-1 font-medium">SSH 私钥内容 {{ isEdit ? '(留空保留原有私钥)' : '*' }}</label>
                <textarea
                  v-model="form.credential"
                  rows="4"
                  class="input font-mono text-[11px] leading-tight"
                  :required="!isEdit"
                  placeholder="-----BEGIN OPENSSH PRIVATE KEY----- 或 -----BEGIN RSA PRIVATE KEY-----"
                ></textarea>
              </div>
              <div>
                <label class="block text-slate-300 mb-1 font-medium">私钥口令 Passphrase (若私钥加密请输入)</label>
                <input
                  v-model="form.passphrase"
                  type="password"
                  class="input font-mono"
                  placeholder="无口令可留空"
                />
              </div>
            </div>
            <!-- 主机公钥指纹 (可选，带探测按钮) -->
            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="text-slate-300 font-medium">主机公钥指纹 (MITM 防护，可选)</label>
                <button
                  type="button"
                  class="text-cyan-400 hover:text-cyan-300 text-[11px] flex items-center gap-1 cursor-pointer"
                  :disabled="probingKey"
                  @click="probeFingerprint()">
                  <span v-if="probingKey" class="animate-spin">↻</span>
                  <span>探测指纹</span>
                </button>
              </div>
              <input
                v-model="form.hostKeyFingerprint"
                type="text"
                class="input font-mono text-[11px]"
                placeholder="SHA256:4x9j... (支持一键探测自动填充)"
              />
            </div>
            <!-- 标签与备注 -->
            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div>
                <label class="block text-slate-300 mb-1 font-medium">标签 (以逗号分隔)</label>
                <input
                  v-model="form.tags"
                  type="text"
                  class="input"
                  placeholder="例如: prod, web, k8s"
                />
              </div>
              <div>
                <label class="block text-slate-300 mb-1 font-medium">排序权重</label>
                <input
                  v-model.number="form.sortOrder"
                  type="number"
                  class="input"
                  placeholder="0"
                />
              </div>
            </div>
            <div>
              <label class="block text-slate-300 mb-1 font-medium">备注说明</label>
              <input
                v-model="form.remark"
                type="text"
                class="input"
                placeholder="例如: 机房 3 号机架物理机"
              />
            </div>
            <!-- 连通性测试结果提示区 -->
            <div v-if="formTestResult" class="p-2.5 rounded text-xs border" :class="formTestResult.success ? 'bg-emerald-950/30 border-emerald-500/40 text-emerald-300' : 'bg-rose-950/30 border-rose-500/40 text-rose-300'">
              <span v-if="formTestResult.success">✓ 测试连通正常，响应延迟: {{ formTestResult.latencyMs }} ms</span>
              <span v-else>✕ 测试连接失败: {{ formTestResult.error }}</span>
            </div>
            <!-- 操作按钮 -->
            <div class="flex items-center justify-between pt-3 border-t border-slate-700/60 mt-1">
              <button
                type="button"
                class="btn btn-secondary text-xs flex items-center gap-1"
                :disabled="testingForm"
                @click="testFormConnection()">
                <span v-if="testingForm" class="animate-spin">↻</span>
                <span v-else>⚡</span>
                <span>测试连接</span>
              </button>
              <div class="flex items-center gap-2">
                <button
                  type="button"
                  class="btn btn-secondary text-xs"
                  @click="showEditModal = false">
                  取消
                </button>
                <button
                  type="submit"
                  class="btn btn-primary text-xs">
                  保存配置
                </button>
              </div>
            </div>
          </form>
        </div>
      </div>
      <!-- ==================== 弹窗 2: 交互式 Web 终端 ==================== -->
      <div v-if="showTerminalModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 backdrop-blur-sm p-2 sm:p-4">
        <div class="panel w-full max-w-5xl h-[85vh] flex flex-col border-cyan-500/40 shadow-2xl overflow-hidden">
          <!-- 终端标题栏 -->
          <div class="flex items-center justify-between px-4 py-2.5 bg-slate-900 border-b border-slate-800 shrink-0">
            <div class="flex items-center gap-3">
              <span class="text-cyan-400 font-mono text-sm">⌨</span>
              <div class="flex items-center gap-2 text-xs">
                <span class="font-semibold text-slate-100">{{ terminalHost?.name }}</span>
                <span class="font-mono text-slate-400">({{ terminalHost?.username }}@{{ terminalHost?.host }}:{{ terminalHost?.port }})</span>
              </div>
              <span
                class="badge text-[10px]"
                :class="{
                  'border-cyan-500/50 text-cyan-300 bg-cyan-950/30': terminalStatus === 'connecting',
                  'border-emerald-500/50 text-emerald-300 bg-emerald-950/30': terminalStatus === 'connected',
                  'border-amber-500/50 text-amber-300 bg-amber-950/30': terminalStatus === 'disconnected',
                  'border-rose-500/50 text-rose-300 bg-rose-950/30': terminalStatus === 'error',
                }">
                {{ terminalStatus === 'connected' ? '● 会话就绪' : terminalStatus === 'connecting' ? '○ 连接中' : terminalStatus === 'disconnected' ? '已断开' : '错误' }}
              </span>
            </div>
            <div class="flex items-center gap-2">
              <button
                class="btn btn-secondary text-xs py-1 px-2 text-cyan-300 border-cyan-500/30 hover:bg-cyan-950/30"
                @click="reconnectTerminal()">
                重新连接
              </button>
              <button
                class="text-slate-400 hover:text-slate-100 text-lg px-2 cursor-pointer"
                @click="closeTerminalModal()">
                ×
              </button>
            </div>
          </div>
          <!-- 快捷命令工具条 -->
          <div class="flex items-center gap-2 px-3 py-1.5 bg-slate-950/80 border-b border-slate-800/80 overflow-x-auto shrink-0 text-xs">
            <span class="text-slate-500 text-[11px] shrink-0">快捷指令:</span>
            <button
              v-for="cmd in QUICK_COMMANDS"
              :key="cmd.label"
              class="btn btn-secondary text-[11px] py-0.5 px-2 text-slate-300 hover:text-cyan-300 hover:border-cyan-500/50"
              @click="sendQuickCmd(cmd.cmd)">
              {{ cmd.label }}
            </button>
          </div>
          <!-- xterm 挂载容器 -->
          <div class="flex-1 bg-[#0c0c0c] p-2 overflow-hidden relative">
            <div ref="terminalContainer" class="w-full h-full"></div>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 3: 远程指令非交互式执行 ==================== -->
      <div v-if="showExecModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 flex flex-col gap-4 border-slate-700 max-h-[90vh] overflow-y-auto">
          <div class="flex items-center justify-between border-b border-slate-700/60 pb-3">
            <div class="flex items-center gap-2">
              <h3 class="font-semibold text-slate-100 text-base">远程运维脚本执行</h3>
              <span class="badge border-cyan-500/40 text-cyan-300 text-xs">{{ execHost?.name }}</span>
            </div>
            <button class="text-slate-400 hover:text-slate-200 text-lg" @click="closeExecModal()">×</button>
          </div>
          <div class="flex flex-col gap-3 text-xs">
            <!-- 预置快捷指令 -->
            <div class="flex flex-wrap items-center gap-1.5">
              <span class="text-slate-400 text-[11px]">常用预置:</span>
              <button
                v-for="preset in EXEC_PRESETS"
                :key="preset.label"
                class="badge border-slate-700 hover:border-cyan-500/60 text-slate-300 hover:text-cyan-300 cursor-pointer"
                @click="applyExecPreset(preset.cmd)">
                {{ preset.label }}
              </button>
            </div>
            <!-- 指令输入框 -->
            <div>
              <label class="block text-slate-300 mb-1 font-medium">Shell 指令 *</label>
              <textarea
                v-model="execCommand"
                rows="3"
                class="input font-mono text-xs"
                placeholder="例如: docker ps -a 或 systemctl status nginx"
              ></textarea>
            </div>
            <!-- 超时设置与执行按钮 -->
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-2">
                <span class="text-slate-400">超时限制:</span>
                <input
                  v-model.number="execTimeout"
                  type="number"
                  min="1"
                  max="300"
                  class="input w-20 py-1 text-xs"
                />
                <span class="text-slate-500">秒</span>
              </div>
              <button
                class="btn btn-primary text-xs flex items-center gap-1 px-4"
                :disabled="executing"
                @click="runExecCommand()">
                <span v-if="executing" class="animate-spin">↻</span>
                <span v-else>▶</span>
                <span>{{ executing ? '正在远端执行...' : '立即执行' }}</span>
              </button>
            </div>
            <!-- 执行结果展示区 -->
            <div v-if="execResult" class="flex flex-col gap-2 mt-2">
              <div class="flex items-center justify-between text-xs px-2 py-1 bg-slate-900 rounded border border-slate-800">
                <div class="flex items-center gap-2">
                  <span>退出码:</span>
                  <span
                    class="font-mono font-bold"
                    :class="execResult.exitCode === 0 ? 'text-emerald-400' : 'text-rose-400'">
                    {{ execResult.exitCode }}
                  </span>
                </div>
                <div class="text-slate-400">
                  执行耗时: <span class="text-cyan-300 font-mono">{{ execResult.durationMs }} ms</span>
                </div>
              </div>
              <!-- STDOUT 输出 -->
              <div v-if="execResult.output" class="flex flex-col gap-1">
                <span class="text-slate-400 text-[11px]">标准输出 (STDOUT):</span>
                <pre class="bg-black/80 text-slate-200 font-mono text-xs p-3 rounded border border-slate-800 max-h-60 overflow-y-auto whitespace-pre-wrap">{{ execResult.output }}</pre>
              </div>
              <!-- STDERR 输出 -->
              <div v-if="execResult.errorOutput || execResult.error" class="flex flex-col gap-1">
                <span class="text-rose-400 text-[11px]">标准错误 (STDERR):</span>
                <pre class="bg-rose-950/20 text-rose-300 font-mono text-xs p-3 rounded border border-rose-900/40 max-h-40 overflow-y-auto whitespace-pre-wrap">{{ execResult.errorOutput || execResult.error }}</pre>
              </div>
            </div>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 4: SFTP 远端文件浏览器 ==================== -->
      <div v-if="showFilesModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 backdrop-blur-sm p-2 sm:p-4">
        <div class="panel w-full max-w-5xl h-[85vh] flex flex-col border-slate-700 shadow-2xl overflow-hidden">
          <!-- 文件浏览器顶部 -->
          <div class="flex items-center justify-between px-4 py-2.5 bg-slate-900 border-b border-slate-800 shrink-0">
            <div class="flex items-center gap-2 text-xs">
              <span class="text-cyan-400 text-sm">📁</span>
              <span class="font-semibold text-slate-100">远程文件系统 (SFTP)</span>
              <span class="text-slate-500">·</span>
              <span class="text-cyan-300 font-mono">{{ filesHost?.name }} ({{ filesHost?.host }})</span>
            </div>
            <button class="text-slate-400 hover:text-slate-100 text-lg px-2 cursor-pointer" @click="closeFilesModal()">×</button>
          </div>
          <!-- 导航路径栏与快捷操作 -->
          <div class="flex flex-col sm:flex-row items-stretch sm:items-center justify-between gap-2 px-4 py-2 bg-slate-950/60 border-b border-slate-800/80 text-xs shrink-0">
            <!-- 面包屑与快速路径跳转 -->
            <div class="flex items-center gap-2 flex-1 min-w-0">
              <button
                class="btn btn-secondary text-xs py-1 px-2 shrink-0"
                :disabled="currentPath === '/'"
                title="返回上一级"
                @click="goUpDir()">
                ↑ 上一级
              </button>
              <div class="flex items-center gap-1 overflow-x-auto py-0.5">
                <button
                  v-for="(seg, idx) in breadcrumbSegments"
                  :key="seg.path"
                  class="hover:text-cyan-300 text-slate-300 shrink-0 cursor-pointer"
                  @click="loadFiles(seg.path)">
                  <span v-if="idx > 0" class="text-slate-600 mr-1">/</span>
                  <span>{{ seg.name }}</span>
                </button>
              </div>
            </div>
            <!-- 动作按钮: 路径直接前往 / 新建文件夹 / 刷新 -->
            <div class="flex items-center gap-2 shrink-0">
              <div class="flex items-center">
                <input
                  v-model="pathInput"
                  type="text"
                  class="input py-1 text-xs w-44 font-mono rounded-r-none"
                  placeholder="输入绝对路径"
                  @keyup.enter="jumpToPath()"
                />
                <button
                  class="btn btn-secondary text-xs py-1 px-2.5 rounded-l-none border-l-0"
                  @click="jumpToPath()">
                  前往
                </button>
              </div>
              <input
                ref="fileUploadInput"
                type="file"
                class="hidden"
                @change="onFileSelected($event)"
              />
              <button
                class="btn btn-secondary text-xs py-1 px-2.5 flex items-center gap-1"
                :disabled="filesLoading"
                title="上传文件到当前目录"
                @click="triggerFileUpload()">
                <span>↑</span>
                <span>上传</span>
              </button>
              <button class="btn btn-secondary text-xs py-1 px-2.5" @click="openMkdirDialog()">
                ＋ 新建文件夹
              </button>
              <button class="btn btn-secondary text-xs py-1 px-2" :disabled="filesLoading" @click="loadFiles(currentPath)">
                <span v-if="filesLoading" class="animate-spin">↻</span>
                <span v-else>↻</span>
              </button>
            </div>
          </div>
          <!-- 错误提示 -->
          <div v-if="filesError" class="p-3 bg-rose-950/30 border-b border-rose-900/40 text-rose-300 text-xs flex items-center justify-between shrink-0">
            <span>⚠ {{ filesError }}</span>
            <button class="text-rose-400 hover:text-rose-200 underline" @click="loadFiles('/')">返回根目录</button>
          </div>
          <!-- 文件列表区域 -->
          <div class="flex-1 overflow-y-auto p-4 bg-slate-900/30">
            <div v-if="filesLoading && fileEntries.length === 0" class="flex flex-col items-center justify-center h-48 text-slate-400 text-xs gap-3">
              <div class="flex items-center gap-2">
                <span class="animate-spin text-lg text-cyan-400">↻</span>
                <span>正在读取远程目录 ({{ currentPath || '/' }})...</span>
              </div>
              <button
                type="button"
                class="px-2.5 py-1 text-xs text-rose-400 hover:text-rose-300 border border-rose-800/50 rounded bg-rose-950/20 hover:bg-rose-900/30 transition-colors"
                @click="filesLoading = false; filesError = '已手动取消目录读取等待'">
                取消等待
              </button>
            </div>
            <div v-else-if="fileEntries.length === 0" class="flex flex-col items-center justify-center h-48 text-slate-500 text-xs gap-2">
              <span class="text-3xl">📂</span>
              <span>当前目录为空</span>
            </div>
            <table v-else class="data-table">
              <thead>
                <tr>
                  <th class="w-1/2">名称</th>
                  <th>权限</th>
                  <th>大小</th>
                  <th>修改时间</th>
                  <th class="text-right">操作</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="entry in fileEntries"
                  :key="entry.path"
                  class="hover:bg-cyan-950/20 cursor-pointer"
                  @click="navigateToFile(entry)">
                  <td class="font-medium text-slate-200">
                    <div class="flex items-center gap-2">
                      <span class="text-sm shrink-0">{{ entry.isDirectory ? '📁' : '📄' }}</span>
                      <span class="truncate hover:text-cyan-300" :title="entry.name">{{ entry.name }}</span>
                    </div>
                  </td>
                  <td class="font-mono text-slate-400 text-xs">
                    {{ entry.permissions || '—' }}
                  </td>
                  <td class="font-mono text-slate-400 text-xs">
                    {{ entry.isDirectory ? '—' : formatBytes(entry.size) }}
                  </td>
                  <td class="text-slate-400 text-xs">
                    {{ formatTime(entry.lastModified) }}
                  </td>
                  <td class="text-right" @click.stop="">
                    <div class="flex items-center justify-end gap-2 text-xs">
                      <button
                        v-if="!entry.isDirectory"
                        class="text-cyan-400 hover:text-cyan-300 cursor-pointer"
                        title="查看与编辑文件内容"
                        @click="openFileEditor(entry)">
                        编辑
                      </button>
                      <button
                        class="text-emerald-400 hover:text-emerald-300 cursor-pointer"
                        :title="entry.isDirectory ? '暂不支持直接下载目录，请先压缩' : '下载文件'"
                        @click="downloadEntry(entry)">
                        下载
                      </button>
                      <button
                        class="text-amber-400 hover:text-amber-300 cursor-pointer"
                        title="复制副本或复制到指定路径"
                        @click="openCopyDialog(entry)">
                        复制
                      </button>
                      <button
                        class="text-indigo-400 hover:text-indigo-300 cursor-pointer"
                        title="移动到新路径或新目录"
                        @click="openMoveDialog(entry)">
                        移动
                      </button>
                      <button
                        class="text-slate-400 hover:text-slate-200 cursor-pointer"
                        title="重命名"
                        @click="openRenameDialog(entry)">
                        重命名
                      </button>
                      <button
                        class="text-rose-400 hover:text-rose-300 cursor-pointer"
                        title="删除"
                        @click="deleteEntry(entry)">
                        删除
                      </button>
                    </div>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 5: 远端文本文件在线编辑 ==================== -->
      <div v-if="showEditorModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/75 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-4xl h-[85vh] flex flex-col border-cyan-500/40 shadow-2xl overflow-hidden">
          <div class="flex items-center justify-between px-4 py-2.5 bg-slate-900 border-b border-slate-800 shrink-0">
            <div class="flex items-center gap-2 text-xs min-w-0">
              <span class="text-cyan-400">📄</span>
              <span class="font-semibold text-slate-100 truncate">{{ editorName }}</span>
              <span class="text-slate-500 font-mono truncate text-[11px]">{{ editorPath }}</span>
            </div>
            <button class="text-slate-400 hover:text-slate-100 text-lg px-2 cursor-pointer" @click="closeFileEditor()">×</button>
          </div>
          <div class="flex-1 bg-black/90 p-3 overflow-hidden flex flex-col">
            <div v-if="editorLoading" class="flex items-center justify-center h-full text-slate-400 text-xs">
              <span class="animate-spin text-lg mr-2">↻</span>
              <span>正在拉取远程文件内容...</span>
            </div>
            <textarea
              v-else
              v-model="editorContent"
              class="w-full h-full bg-transparent text-slate-200 font-mono text-xs outline-none resize-none leading-relaxed p-1"
              placeholder="文件为空"
            ></textarea>
          </div>
          <div class="flex items-center justify-between px-4 py-2.5 bg-slate-900 border-t border-slate-800 shrink-0">
            <span class="text-xs text-slate-400">
              字符数: {{ editorContent.length }} · UTF-8
            </span>
            <div class="flex items-center gap-2">
              <button class="btn btn-secondary text-xs" @click="closeFileEditor()">取消</button>
              <button
                class="btn btn-primary text-xs flex items-center gap-1"
                :disabled="editorSaving"
                @click="saveFileEditor()">
                <span v-if="editorSaving" class="animate-spin">↻</span>
                <span v-else>💾</span>
                <span>{{ editorSaving ? '保存中...' : '保存更改' }}</span>
              </button>
            </div>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 6: 新建文件夹 ==================== -->
      <div v-if="showMkdirModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5 flex flex-col gap-4 border-slate-700">
          <h3 class="font-semibold text-slate-100 text-sm">新建远程目录</h3>
          <div>
            <label class="block text-slate-300 text-xs mb-1 font-medium">目录名称</label>
            <input
              v-model="newFolderName"
              type="text"
              class="input text-xs"
              placeholder="请输入文件夹名称"
              @keyup.enter="saveMkdir()"
            />
          </div>
          <div class="flex items-center justify-end gap-2">
            <button class="btn btn-secondary text-xs" @click="showMkdirModal = false">取消</button>
            <button class="btn btn-primary text-xs" @click="saveMkdir()">创建</button>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 7: 重命名 ==================== -->
      <div v-if="showRenameModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5 flex flex-col gap-4 border-slate-700">
          <h3 class="font-semibold text-slate-100 text-sm">重命名</h3>
          <div>
            <label class="block text-slate-300 text-xs mb-1 font-medium">新名称</label>
            <input
              v-model="renameDestName"
              type="text"
              class="input text-xs"
              placeholder="请输入新名称"
              @keyup.enter="saveRename()"
            />
          </div>
          <div class="flex items-center justify-end gap-2">
            <button class="btn btn-secondary text-xs" @click="showRenameModal = false">取消</button>
            <button class="btn btn-primary text-xs" @click="saveRename()">确认</button>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 8: 复制文件或目录 ==================== -->
      <div v-if="showCopyModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5 flex flex-col gap-4 border-slate-700">
          <h3 class="font-semibold text-slate-100 text-sm">复制文件 / 目录</h3>
          <div>
            <label class="block text-slate-400 text-xs mb-1">源文件: <span class="font-mono text-slate-200">{{ copySourceName }}</span></label>
            <label class="block text-slate-300 text-xs mb-1 font-medium mt-2">目标路径 / 副本名称</label>
            <input
              v-model="copyDestPath"
              type="text"
              class="input text-xs font-mono"
              placeholder="请输入完整目标路径"
              @keyup.enter="saveCopy()"
            />
          </div>
          <div class="flex items-center justify-end gap-2">
            <button class="btn btn-secondary text-xs" @click="showCopyModal = false">取消</button>
            <button class="btn btn-primary text-xs" :disabled="copySaving" @click="saveCopy()">
              <span v-if="copySaving" class="animate-spin mr-1">↻</span>
              <span>确认复制</span>
            </button>
          </div>
        </div>
      </div>
      <!-- ==================== 弹窗 9: 移动文件或目录 ==================== -->
      <div v-if="showMoveModal" class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-sm p-5 flex flex-col gap-4 border-slate-700">
          <h3 class="font-semibold text-slate-100 text-sm">移动文件 / 目录</h3>
          <div>
            <label class="block text-slate-400 text-xs mb-1">源文件: <span class="font-mono text-slate-200">{{ moveSourceName }}</span></label>
            <label class="block text-slate-300 text-xs mb-1 font-medium mt-2">目标目录 / 新完整路径</label>
            <input
              v-model="moveDestPath"
              type="text"
              class="input text-xs font-mono"
              placeholder="请输入目标完整路径"
              @keyup.enter="saveMove()"
            />
          </div>
          <div class="flex items-center justify-end gap-2">
            <button class="btn btn-secondary text-xs" @click="showMoveModal = false">取消</button>
            <button class="btn btn-primary text-xs" :disabled="moveSaving" @click="saveMove()">
              <span v-if="moveSaving" class="animate-spin mr-1">↻</span>
              <span>确认移动</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
