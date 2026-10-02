import { defineComponent, ref, computed, nextTick, onMounted, onBeforeUnmount, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { Terminal } from 'xterm';
import { FitAddon } from '@xterm/addon-fit';
import { http, readTerminalTabs, saveTerminalTabs } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';
import { openConfirm } from '../store/modal.js';

// 参考 AITool 优化：Windows Terminal Campbell 经典高对比度配色
// 解决 Windows PowerShell / CMD 默认 ANSI 颜色在暗色背景下对比度不足与发虚问题
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

// 交互式输入提示匹配模式（参考 AITool）
const PROMPT_PATTERNS = [
  /press enter/i, /press return/i, /press any key/i, /hit enter/i,
  /\[y\/n\]/i, /\(y\/n\)/i, /\[yes\/no\]/i, /\(yes\/no\)/i, /\by\/n\b/i, /yes\/no/i,
  /password:/i, /passphrase:/i, /token:/i, /continue\?/i, /proceed\?/i,
  /confirm/i, /overwrite/i, /are you sure/i, /select (an option|a number)/i,
  /按(回车|任意键|回车键)/i, /请输入/i, /请选择/i, /是否/i, /确认/i, /继续/i, /密码/i, /口令/i,
];

function detectAwaitingInput(term) {
  if (!term || !term.buffer || !term.buffer.active) return { awaiting: false, hint: '' };
  const buffer = term.buffer.active;
  const length = buffer.length;
  if (length === 0) return { awaiting: false, hint: '' };

  if (buffer.cursorY !== length - 1) return { awaiting: false, hint: '' };

  const line = buffer.getLine(length - 1);
  if (!line) return { awaiting: false, hint: '' };

  const text = line.translateToString(true).trimEnd();
  if (!text || text.length >= term.cols - 1) return { awaiting: false, hint: '' };

  for (const pattern of PROMPT_PATTERNS) {
    if (pattern.test(text)) {
      return { awaiting: true, hint: text };
    }
  }

  return { awaiting: false, hint: '' };
}

const isWindowsClient = typeof navigator !== 'undefined' && (/win/i.test(navigator.platform || '') || /windows/i.test(navigator.userAgent || ''));

const LINUX_COMMANDS = [
  { label: '进程 (top)', cmd: 'top\n' },
  { label: '磁盘 (df -h)', cmd: 'df -h\n' },
  { label: '内存 (free -h)', cmd: 'free -h\n' },
  { label: '容器 (docker ps)', cmd: 'docker ps\n' },
  { label: '网络 (ip a)', cmd: 'ip a\n' },
];

const WINDOWS_COMMANDS = [
  { label: '进程 (Get-Process)', cmd: 'Get-Process | Select-Object -First 15\r' },
  { label: '文件目录 (dir)', cmd: 'dir\r' },
  { label: '驱动磁盘 (Get-PSDrive)', cmd: 'Get-PSDrive -PSProvider FileSystem\r' },
  { label: '网络配置 (ipconfig)', cmd: 'ipconfig\r' },
  { label: '环境变量 (env:)', cmd: 'Get-ChildItem env:\r' },
];

export default defineComponent({
  name: 'TerminalView',
  setup() {
    const route = useRoute();
    const router = useRouter();
    const requestedDirectory = () => typeof route.query.cwd === 'string' ? route.query.cwd : undefined;
    const directoryRequest = () => typeof route.query.open === 'string' ? route.query.open : requestedDirectory();
    const initialTabId = 'tab-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
    const isFullscreen = ref(false);
    const terminalHost = ref(null);
    const activeCommandSet = ref(isWindowsClient ? 'windows' : 'linux');
    const tabs = ref([
      { id: initialTabId, title: '终端 1', sessionId: '', isConnected: false, awaitingInput: null, workingDirectory: requestedDirectory(), directoryRequest: directoryRequest() },
    ]);
    const activeTabId = ref(initialTabId);
    const backgroundSessions = ref([]);
    const showBackground = ref(false);
    const listBusy = ref(false);
    const listError = ref('');
    const capabilities = ref(null);
    const supportBusy = ref(false);
    const supportCheckedAt = ref('');
    const supportError = ref('');
    const sessionBusy = ref(false);
    const renameDrafts = ref({});
    let disposed = false;
    let listTimer;

    async function refreshSessions() {
      if (listBusy.value || disposed) return;
      listBusy.value = true;
      try {
        const result = await http(API.terminal.sessions, { method: 'GET' });
        if (disposed) return;
        if (result.ok && Array.isArray(result.data)) {
          backgroundSessions.value = result.data;
          for (const session of result.data) renameDrafts.value[session.sessionId] ??= session.name;
          for (const tab of tabs.value) {
            const session = result.data.find(s => s.sessionId === tab.sessionId);
            tab.currentWorkingDirectory = session?.workingDirectory;
          }
          listError.value = '';
        } else listError.value = result.message || '会话列表加载失败';
      } finally { listBusy.value = false; }
    }

    function rememberTabs() { saveTerminalTabs(tabs.value); }

    async function refreshSupport(notify = false) {
      if (supportBusy.value || disposed) return;
      supportBusy.value = true;
      supportError.value = '';
      try {
        const support = await http(API.terminal.support, { method: 'GET' });
        if (support.ok) {
          capabilities.value = support.data;
          activeCommandSet.value = support.data.platform === 'Windows' ? 'windows' : 'linux';
          supportCheckedAt.value = new Date().toLocaleTimeString();
          if (notify) {
            const message = `环境检测完成：${support.data.platform} · ${support.data.nativePty ? '支持原生 PTY' : '未检测到原生 PTY，当前使用管道模式'}`;
            if (support.data.nativePty) toast.success(message);
            else toast.info(message);
          }
        } else {
          supportError.value = support.message || '环境检测失败，请重试';
        }
      } catch {
        supportError.value = '环境检测失败，请重试';
        if (notify) toast.error(supportError.value);
      } finally { supportBusy.value = false; }
    }

    async function openCurrentDirectory() {
      const id = activeTab.value?.sessionId;
      if (!id) return;
      const details = await http(API.terminal.session(id), { method: 'GET' });
      const path = details.data?.workingDirectory;
      if (!details.ok) return;
      if (!path) { toast.info('当前目录尚未确认，请等待本地 shell 返回提示符'); return; }
      const result = await http(API.files.list, { method: 'GET', params: { path } });
      if (result.ok) router.push({ path: '/files', query: { path: result.data.path } });
      else toast.error(result.message || '目录无法访问');
    }

    async function renameSession(session) {
      if (sessionBusy.value) return;
      sessionBusy.value = true;
      try {
        const name = renameDrafts.value[session.sessionId]?.trim();
        const result = await http(API.terminal.session(session.sessionId), { method: 'PATCH', body: { name } });
        if (result.ok) {
          const tab = tabs.value.find(t => t.sessionId === session.sessionId);
          if (tab) tab.title = name;
          rememberTabs();
          await refreshSessions();
        }
      } finally { sessionBusy.value = false; }
    }
    function sendInput(runtime, data) {
      if (runtime?.ws?.readyState === WebSocket.OPEN) runtime.ws.send(JSON.stringify({ type: 'input', data }));
    }

    // 保存终端运行时的非响应式对象
    // tabRuntimes: Map<string, { term, fitAddon, ws, sessionId, containerEl, resizeObserver, awaitingTimer }>
    const tabRuntimes = new Map();

    const activeTab = computed(() => {
      return tabs.value.find((t) => t.id === activeTabId.value) || tabs.value[0];
    });

    const displayedCommands = computed(() => {
      return activeCommandSet.value === 'windows' ? WINDOWS_COMMANDS : LINUX_COMMANDS;
    });

    function createTabRuntime(tabId) {
      if (tabRuntimes.has(tabId)) return tabRuntimes.get(tabId);

      const containerEl = document.createElement('div');
      containerEl.className = 'terminal-xterm-host';
      containerEl.style.display = tabId === activeTabId.value ? 'block' : 'none';

      const term = new Terminal({
        fontFamily: "'Cascadia Mono', 'SF Mono', 'Consolas', 'JetBrains Mono', monospace",
        fontSize: 13,
        lineHeight: 1.25,
        cursorBlink: true,
        cursorStyle: 'block',
        theme: CAMPBELL_THEME,
        allowTransparency: false,
        convertEol: true,
      });

      const fitAddon = new FitAddon();
      term.loadAddon(fitAddon);

      // 智能剪贴板 Ctrl+C / Ctrl+V 处理（参考 AITool）
      term.attachCustomKeyEventHandler((event) => {
        const mod = event.ctrlKey || event.metaKey;
        const key = event.key ? event.key.toLowerCase() : '';

        if (mod && key === 'c') {
          if (term.hasSelection()) {
            const selectedText = term.getSelection();
            if (navigator.clipboard && selectedText) {
              navigator.clipboard.writeText(selectedText).catch(() => {});
            }
            term.clearSelection();
            event.preventDefault();
            return false;
          }
          return true; // 无选中时透传为 SIGINT 中断信号
        }

        if (mod && key === 'v') {
          event.preventDefault();
          if (navigator.clipboard) {
            navigator.clipboard.readText().then((text) => {
              if (text) term.paste(text);
            }).catch(() => {});
          }
          return false;
        }

        return true;
      });

      const runtime = {
        term,
        fitAddon,
        ws: null,
        sessionId: '',
        containerEl,
        resizeObserver: null,
        awaitingTimer: null,
        isConnecting: false,
        lastSequence: 0,
        retries: 0,
        reconnectTimer: null,
      };

      // 单例绑定用户键盘输入，避免重连时重复注册导致按键重复发送
      term.onData((dataChunk) => {
        if (runtime.ws && runtime.ws.readyState === WebSocket.OPEN) {
          sendInput(runtime, dataChunk);
        }
      });

      tabRuntimes.set(tabId, runtime);
      return runtime;
    }

    async function initTabSession(tabId) {
      const runtime = createTabRuntime(tabId);
      const tab = tabs.value.find((t) => t.id === tabId);
      if (!tab) return;
      if (runtime.isConnecting) return;
      runtime.isConnecting = true;

      try {
        if (runtime.ws) {
          const previous = runtime.ws;
          runtime.ws = null;
          if (previous.readyState !== WebSocket.CLOSED) await new Promise(resolve => {
            const timeout = setTimeout(resolve, 2000);
            previous.addEventListener('close', () => { clearTimeout(timeout); resolve(); }, { once: true });
            try { previous.close(); } catch { clearTimeout(timeout); resolve(); }
          });
        }
        if (runtime.reconnectTimer) clearTimeout(runtime.reconnectTimer);
        runtime.sessionId ||= tab.sessionId;

        tab.isConnected = false;
        tab.awaitingInput = null;
        runtime.term.writeln('\x1b[38;2;56;189;248m[正在连接后台终端...]\x1b[0m');

        const cols = runtime.term.cols > 0 ? runtime.term.cols : (isFullscreen.value ? 120 : 90);
        const rows = runtime.term.rows > 0 ? runtime.term.rows : (isFullscreen.value ? 36 : 28);

        const res = runtime.sessionId ? await http(API.terminal.session(runtime.sessionId), { method: 'GET' }) : await http(API.terminal.sessions, {
          method: 'POST',
          body: { columns: cols, rows, workingDirectory: tab.workingDirectory },
        });

        if (!res.ok || !res.data) {
          runtime.retries = 3;
          throw new Error(res.message || '会话不存在或服务已重启，请新建终端');
        }
        if (disposed || !tabRuntimes.has(tabId)) return;

        const data = res.data;
        if (data.state === 'Exited') {
          runtime.retries = 3;
          throw new Error('会话已退出，退出码：' + (data.exitCode ?? '未知'));
        }
        const sessionId = data?.sessionId || data?.SessionId;
        if (!sessionId) {
          throw new Error('未获取到有效的终端会话 ID，请确认服务已更新');
        }

        runtime.sessionId = sessionId;
        tab.sessionId = sessionId;
        tab.workingDirectory = data.workingDirectory;

        rememberTabs();
        const ticketResponse = await http(API.terminal.attachment(runtime.sessionId), { method: 'POST' });
        if (!ticketResponse.ok || !ticketResponse.data?.ticket) throw new Error(ticketResponse.message || '无法取得连接凭据');
        if (disposed || !tabRuntimes.has(tabId)) return;
        await new Promise(resolve => runtime.term.write('', resolve));
        const wsUrl = API.terminal.ws(runtime.sessionId, ticketResponse.data.ticket, runtime.lastSequence);

        const ws = new WebSocket(wsUrl);
        runtime.ws = ws;

        ws.onopen = () => {
          if (runtime.ws !== ws) return;
          tab.isConnected = true;
          runtime.retries = 0;
          runtime.term.writeln('\x1b[38;2;16;185;129m[终端已连接成功]\x1b[0m');
          if (data.workingDirectory) {
            runtime.term.writeln(`\x1b[90m[工作目录: ${data.workingDirectory}]\x1b[0m\r\n`);
          }
          nextTick(() => {
            try { runtime.fitAddon.fit(); } catch { /* The terminal can be hidden during navigation. */ }
            ws.send(JSON.stringify({ type: 'resize', cols: runtime.term.cols, rows: runtime.term.rows }));
            runtime.term.focus();
          });
          refreshSessions();
        };

        ws.onmessage = (event) => {
          if (runtime.ws !== ws) return;
          let frame;
          try { frame = JSON.parse(event.data); } catch { return; }
          if (frame.truncated) runtime.term.writeln('\r\n\x1b[33m[较早的输出已截断，全屏程序可能需要重新绘制]\x1b[0m');
          if (typeof frame.data === 'string' && frame.sequence > runtime.lastSequence) {
            runtime.term.write(frame.data, () => { runtime.lastSequence = frame.sequence; });
          }

          // 防抖检测交互式提示输入（如确认、密码、[y/n]等）
          if (runtime.awaitingTimer) clearTimeout(runtime.awaitingTimer);
          runtime.awaitingTimer = setTimeout(() => {
            const { awaiting, hint } = detectAwaitingInput(runtime.term);
            tab.awaitingInput = awaiting ? hint : null;
          }, 200);
        };

        ws.onclose = () => {
          if (runtime.ws !== ws) return;
          tab.isConnected = false;
          tab.awaitingInput = null;
          runtime.term.writeln('\r\n\x1b[38;2;244;63;94m[终端会话已断开]\x1b[0m');
          refreshSessions();
          if (!disposed && runtime.retries < 3 && tabRuntimes.has(tabId)) {
            const delay = 1000 * (2 ** runtime.retries++);
            runtime.reconnectTimer = setTimeout(() => initTabSession(tabId), delay);
          }
        };

        ws.onerror = () => {
          if (runtime.ws !== ws) return;
          tab.isConnected = false;
          tab.awaitingInput = null;
          runtime.term.writeln('\r\n\x1b[38;2;244;63;94m[终端通信异常]\x1b[0m');
        };
      } catch (err) {
        tab.isConnected = false;
        tab.awaitingInput = null;
        runtime.term.writeln(`\x1b[38;2;244;63;94m[会话建立失败: ${err.message}]\x1b[0m`);
        toast.error(`终端初始化失败: ${err.message}`);
      } finally {
        runtime.isConnecting = false;
      }
    }

    function mountTab(tabId) {
      if (!terminalHost.value) return;
      const runtime = createTabRuntime(tabId);

      if (!terminalHost.value.contains(runtime.containerEl)) {
        terminalHost.value.appendChild(runtime.containerEl);
        runtime.term.open(runtime.containerEl);

        runtime.resizeObserver = new ResizeObserver(() => {
          if (runtime.containerEl.clientWidth === 0 || runtime.containerEl.clientHeight === 0) return;
          try {
            runtime.fitAddon.fit();
            if (runtime.ws && runtime.ws.readyState === WebSocket.OPEN) {
              runtime.ws.send(JSON.stringify({
                type: 'resize',
                cols: runtime.term.cols,
                rows: runtime.term.rows,
              }));
            }
          } catch { /* Ignore resize during terminal disposal. */ }
        });
        runtime.resizeObserver.observe(runtime.containerEl);
      }

      for (const [id, rt] of tabRuntimes.entries()) {
        rt.containerEl.style.display = id === tabId ? 'block' : 'none';
      }

      nextTick(() => {
        try { runtime.fitAddon.fit(); } catch { /* A hidden tab has no measurable dimensions. */ }
        runtime.term.focus();
      });

      if (!runtime.ws || runtime.ws.readyState === WebSocket.CLOSED) {
        initTabSession(tabId);
      }
    }

    function switchTab(tabId) {
      activeTabId.value = tabId;
      mountTab(tabId);
    }

    function addTab(workingDirectory) {
      const nextNum = tabs.value.length + 1;
      const newId = `tab-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
      tabs.value.push({
        id: newId,
        title: `终端 ${nextNum}`,
        sessionId: '',
        isConnected: false,
        awaitingInput: null,
        workingDirectory,
        directoryRequest: workingDirectory === undefined ? undefined : directoryRequest(),
      });
      switchTab(newId);
    }

    function closeTab(tabId, event) {
      if (event) event.stopPropagation();
      const index = tabs.value.findIndex((t) => t.id === tabId);
      if (index === -1) return;

      const runtime = tabRuntimes.get(tabId);
      if (runtime) {
        tabRuntimes.delete(tabId);
        if (runtime.reconnectTimer) clearTimeout(runtime.reconnectTimer);
        if (runtime.awaitingTimer) clearTimeout(runtime.awaitingTimer);
        if (runtime.ws) {
          try { runtime.ws.close(); } catch { /* Detach is idempotent. */ }
        }
        if (runtime.resizeObserver) {
          runtime.resizeObserver.disconnect();
        }
        try { runtime.term.dispose(); } catch { /* The terminal may already be disposed. */ }
        if (runtime.containerEl && runtime.containerEl.parentElement) {
          runtime.containerEl.parentElement.removeChild(runtime.containerEl);
        }
      }

      tabs.value.splice(index, 1);
      rememberTabs();

      if (tabs.value.length === 0) {
        activeTabId.value = '';
      } else if (activeTabId.value === tabId) {
        const nextActive = tabs.value[Math.max(0, index - 1)];
        switchTab(nextActive.id);
      }
    }

    function sendCtrlC() {
      const runtime = tabRuntimes.get(activeTabId.value);
      if (!runtime || !runtime.ws || runtime.ws.readyState !== WebSocket.OPEN) return;
      sendInput(runtime, '\x03');
    }

    function clearOutput() {
      const runtime = tabRuntimes.get(activeTabId.value);
      if (!runtime || !runtime.term) return;
      runtime.term.clear();
      runtime.term.focus();
    }

    function reconnectCurrent() {
      const runtime = tabRuntimes.get(activeTabId.value);
      if (runtime) {
        if (runtime.isConnecting) return;
        runtime.retries = 0;
      }
      initTabSession(activeTabId.value);
    }

    function focusCurrentTerm() {
      const runtime = tabRuntimes.get(activeTabId.value);
      if (runtime && runtime.term) {
        runtime.term.focus();
      }
    }

    function toggleFullscreen() {
      isFullscreen.value = !isFullscreen.value;
      nextTick(() => {
        const runtime = tabRuntimes.get(activeTabId.value);
        if (runtime) {
          try {
            runtime.fitAddon.fit();
            if (runtime.ws && runtime.ws.readyState === WebSocket.OPEN) {
              runtime.ws.send(JSON.stringify({
                type: 'resize',
                cols: runtime.term.cols,
                rows: runtime.term.rows,
              }));
            }
          } catch { /* Ignore resize during fullscreen transitions. */ }
          runtime.term.focus();
        }
      });
    }

    function runQuickCommand(cmd) {
      const runtime = tabRuntimes.get(activeTabId.value);
      if (!runtime || !runtime.ws || runtime.ws.readyState !== WebSocket.OPEN) {
        toast.warning('当前终端未连接，无法执行快捷命令');
        return;
      }
      sendInput(runtime, cmd);
      runtime.term.focus();
    }

    function toggleCommandSet() {
      activeCommandSet.value = activeCommandSet.value === 'windows' ? 'linux' : 'windows';
    }

    function attachBackground(session) {
      const existing = tabs.value.find(t => t.sessionId === session.sessionId);
      if (existing) { switchTab(existing.id); return; }
      const id = 'tab-' + session.sessionId;
      tabs.value.push({ id, title: session.name, sessionId: session.sessionId, workingDirectory: session.workingDirectory, isConnected: false, awaitingInput: null });
      rememberTabs();
      switchTab(id);
    }

    function endSession(sessionId, tabId) {
      openConfirm({ title: '结束终端', message: '将终止这个终端及其中的任务，是否继续？', confirmText: '结束会话', danger: true,
        onConfirm: async () => {
          const result = await http(API.terminal.session(sessionId), { method: 'DELETE' });
          if (result.ok || result.status === 404) {
            const tab = tabs.value.find(t => t.sessionId === sessionId);
            if (tab) closeTab(tabId || tab.id);
            await refreshSessions();
          }
        },
      });
    }

    async function retainSession(session) {
      if (sessionBusy.value) return;
      sessionBusy.value = true;
      try {
        const result = await http(API.terminal.session(session.sessionId), { method: 'PATCH', body: { keepAlive: !session.keepAlive } });
        if (result.ok) await refreshSessions();
      } finally { sessionBusy.value = false; }
    }

    function stateText(state) {
      return { RunningAttached: '已连接', RunningDetached: '后台运行', Exited: '已退出' }[state] || state;
    }

    onMounted(async () => {
      const saved = readTerminalTabs().map(t => ({ ...t, isConnected: false, awaitingInput: null }));
      if (saved.length) {
        const initial = tabs.value[0];
        const restored = saved.find(tab => tab.directoryRequest === directoryRequest());
        const restoreOnly = requestedDirectory() === undefined || restored;
        if (!restoreOnly) initial.title = '终端 ' + (saved.length + 1);
        tabs.value = restoreOnly ? saved : [...saved, initial];
        activeTabId.value = restoreOnly ? (restored || saved[0]).id : initial.id;
      }
      mountTab(activeTabId.value);
      await refreshSessions();
      await refreshSupport();
      if (!disposed) listTimer = setInterval(() => refreshSessions(), 5000);
    });

    watch(() => [route.query.cwd, route.query.open], () => {
      if (requestedDirectory() !== undefined) addTab(requestedDirectory());
    });

    onBeforeUnmount(() => {
      disposed = true;
      rememberTabs();
      clearInterval(listTimer);
      for (const [, runtime] of tabRuntimes.entries()) {
        if (runtime.reconnectTimer) clearTimeout(runtime.reconnectTimer);
        if (runtime.awaitingTimer) clearTimeout(runtime.awaitingTimer);
        if (runtime.ws) {
          try { runtime.ws.close(); } catch { /* The socket may already be closed. */ }
        }
        if (runtime.resizeObserver) {
          runtime.resizeObserver.disconnect();
        }
        try { runtime.term.dispose(); } catch { /* Navigation can race terminal disposal. */ }
      }
      tabRuntimes.clear();
    });

    return {
      isFullscreen,
      terminalHost,
      tabs,
      activeTabId,
      activeTab,
      activeCommandSet,
      displayedCommands,
      switchTab,
      addTab,
      closeTab,
      sendCtrlC,
      clearOutput,
      reconnectCurrent,
      focusCurrentTerm,
      toggleFullscreen,
      runQuickCommand,
      toggleCommandSet,
      backgroundSessions, showBackground, listBusy, listError, refreshSessions,
      attachBackground, endSession, retainSession, stateText,
      capabilities, supportBusy, supportCheckedAt, supportError, refreshSupport, openCurrentDirectory, renameSession,
      sessionBusy, renameDrafts,
    };
  },
  template: `
    <div class="space-y-3" :class="{ 'fixed inset-0 z-50 p-3 bg-cyber-bg flex flex-col space-y-2': isFullscreen }">
      <!-- 顶部控制栏与标签页 -->
      <div class="flex flex-wrap items-center justify-between gap-2 bg-slate-900/90 border border-cyber-line p-2.5 rounded-lg">
        <!-- 标签页列表 -->
        <div class="flex items-center gap-1.5 overflow-x-auto no-scrollbar">
          <button
            v-for="tab in tabs"
            :key="tab.id"
            type="button"
            class="flex items-center gap-2 px-3 py-1.5 text-xs font-mono rounded border transition-colors cursor-pointer"
            :class="tab.id === activeTabId ? 'bg-slate-800 text-neon-soft border-neon/50 shadow-sm' : 'bg-slate-950/60 text-slate-400 border-slate-800 hover:border-slate-700'"
            @click="switchTab(tab.id)"
          >
            <span
              class="w-2 h-2 rounded-full inline-block"
              :class="tab.isConnected ? 'bg-emerald-400 animate-pulse' : 'bg-rose-500'"
            ></span>
            <span>{{ tab.title }}</span>
            <span
              v-if="tabs.length > 1"
              class="ml-1 text-slate-500 hover:text-rose-400 text-sm font-bold leading-none"
              @click="closeTab(tab.id, $event)"
              title="关闭标签页"
            >&times;</span>
          </button>
          <button
            type="button"
            class="btn btn-xs font-bold text-slate-400 hover:text-neon-soft px-2.5 py-1"
            @click="addTab()"
            title="新建终端"
          >+ 新建</button>

          <!-- 交互式等待输入提示徽章（参考 AITool） -->
          <span
            v-if="activeTab && activeTab.awaitingInput"
            class="ml-2 px-2 py-0.5 rounded text-[11px] bg-amber-500/20 text-amber-300 border border-amber-500/40 animate-pulse font-mono truncate max-w-[260px]"
            :title="activeTab.awaitingInput"
          >
            ⏳ 等待输入: {{ activeTab.awaitingInput }}
          </span>
        </div>

        <!-- 操作按钮组 -->
        <div class="flex items-center gap-1.5 shrink-0">
          <button type="button" class="btn btn-xs" @click="showBackground = !showBackground; refreshSessions()">后台终端 ({{ backgroundSessions.length }})</button>
          <button v-if="activeTab && activeTab.currentWorkingDirectory" type="button" class="btn btn-xs" @click="openCurrentDirectory()" :title="activeTab.currentWorkingDirectory">打开当前目录</button>
          <button v-if="activeTab && activeTab.sessionId" type="button" class="btn btn-xs btn-danger" @click="endSession(activeTab.sessionId, activeTab.id)">结束会话</button>
          <button v-if="activeTab && activeTab.isConnected" type="button" class="btn btn-xs" @click="closeTab(activeTab.id)">仅断开</button>
          <button type="button" class="btn btn-xs" @click="sendCtrlC()" title="发送 SIGINT 中断信号">^C</button>
          <button type="button" class="btn btn-xs" @click="clearOutput()" title="清屏">清屏</button>
          <button type="button" class="btn btn-xs" :disabled="!activeTab" @click="reconnectCurrent()" title="接回同一个终端会话">重连</button>
          <button
            type="button"
            class="btn btn-xs"
            :class="{ 'btn-primary': isFullscreen }"
            @click="toggleFullscreen()"
          >
            {{ isFullscreen ? '还原窗口' : '全屏' }}
          </button>
        </div>
      </div>

      <div v-if="showBackground" class="panel p-3 space-y-2">
        <div class="flex justify-between items-center"><span>后台终端</span><button class="btn btn-xs" :disabled="listBusy" @click="refreshSessions()">{{ listBusy ? '加载中…' : '刷新' }}</button></div>
        <p class="text-xs text-slate-400">已使用终端断开后继续运行；未使用且空闲的终端会自动清理。服务或容器重启后会话结束。</p>
        <p v-if="listError" class="text-xs text-rose-400">{{ listError }}</p>
        <p v-if="!backgroundSessions.length" class="text-xs text-slate-400">没有后台会话</p>
        <div v-for="session in backgroundSessions" :key="session.sessionId" class="flex flex-wrap items-center gap-2 border-t border-slate-700 py-2 text-xs">
          <input class="input !w-36 !text-xs" v-model="renameDrafts[session.sessionId]" maxlength="80" aria-label="会话名称" @keyup.enter="renameSession(session)" />
          <button class="btn btn-xs" :disabled="sessionBusy" @click="renameSession(session)">保存名称</button>
          <span>{{ stateText(session.state) }} · PID {{ session.processId }}</span>
          <span v-if="!session.nativePty" class="text-amber-300">管道回退 · 全屏与中断受限</span>
          <span class="font-mono break-all text-slate-400">{{ session.workingDirectory || session.initialWorkingDirectory || '目录未知' }}{{ session.workingDirectory ? '' : '（初始目录）' }}</span>
          <span>创建 {{ new Date(session.createdAt).toLocaleString() }} · 最近输入 {{ session.lastInputAt ? new Date(session.lastInputAt).toLocaleString() : '未输入' }}</span>
          <span v-if="session.state === 'Exited'">退出码 {{ session.exitCode ?? '未知' }}</span>
          <span v-if="session.bufferTruncated" class="text-amber-300">历史输出已截断</span>
          <button class="btn btn-xs" :disabled="session.state === 'Exited'" @click="attachBackground(session)">重新连接</button>
          <span v-if="session.hasUserInput && session.state !== 'Exited'" class="text-slate-400">自动保留</span>
          <button v-else-if="!session.hasUserInput" class="btn btn-xs" :disabled="sessionBusy || session.state === 'Exited'" @click="retainSession(session)">{{ session.keepAlive ? '取消保留' : '保留空会话' }}</button>
          <button class="btn btn-xs btn-danger" @click="endSession(session.sessionId)">结束</button>
        </div>
      </div>

      <div class="panel p-3 text-xs space-y-2">
        <div class="flex flex-wrap items-center gap-2"><span>终端环境<span v-if="capabilities"> · {{ capabilities.platform }} · {{ capabilities.nativePty ? '支持原生 PTY' : '管道模式' }}</span></span>
          <button class="btn btn-xs" :disabled="supportBusy" @click="refreshSupport(true)">{{ supportBusy ? '检测中…' : '检测环境' }}</button>
          <span v-if="supportCheckedAt" class="text-slate-400">上次成功检测 {{ supportCheckedAt }}</span>
        </div>
        <p v-if="supportError" role="alert" class="text-red-400">{{ supportError }}</p>
        <p v-if="capabilities" class="text-slate-400">{{ capabilities.message }}</p>
      </div>

      <!-- 快捷常用指令按钮栏（根据宿主环境可切换 Windows/Linux 指令） -->
      <div class="flex items-center gap-1.5 flex-wrap text-xs text-slate-400 px-1">
        <button
          type="button"
          class="text-[11px] px-2 py-0.5 rounded bg-slate-800 text-slate-300 border border-slate-700 hover:border-cyan-500 transition-colors cursor-pointer"
          @click="toggleCommandSet()"
          :title="'点击切换快捷指令集（当前：' + (activeCommandSet === 'windows' ? 'Windows' : 'Linux') + '）'"
        >
          ⚙ {{ activeCommandSet === 'windows' ? 'Windows 指令' : 'Linux 指令' }} ⇄
        </button>
        <button
          v-for="item in displayedCommands"
          :key="item.label"
          type="button"
          class="px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/80 border border-slate-700/60 text-slate-300 font-mono text-[11px] transition-colors cursor-pointer"
          @click="runQuickCommand(item.cmd)"
        >
          {{ item.label }}
        </button>
        <button
          type="button"
          class="px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/80 border border-slate-700/60 text-slate-300 font-mono text-[11px] transition-colors cursor-pointer"
          title="清除当前显示和滚动历史，保留当前光标所在行"
          @click="clearOutput()"
        >清理屏幕</button>
      </div>

      <!-- 终端主显示区域 -->
      <div
        class="border border-cyber-line rounded-lg overflow-hidden p-2 bg-[#0c0c0c] shadow-inner cursor-text"
        :class="isFullscreen ? 'flex-1 w-full min-h-0' : 'h-[650px]'"
        @click="focusCurrentTerm()"
      >
        <div ref="terminalHost" class="w-full h-full"></div>
      </div>
    </div>
  `,
});
