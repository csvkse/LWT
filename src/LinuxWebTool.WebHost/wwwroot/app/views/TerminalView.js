import { defineComponent, ref, computed, nextTick, onMounted, onBeforeUnmount } from 'vue';
import { Terminal } from 'xterm';
import { FitAddon } from '@xterm/addon-fit';
import { auth } from '../store/auth.js';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';

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
  { label: '清理屏幕', cmd: 'clear\n' },
];

const WINDOWS_COMMANDS = [
  { label: '进程 (Get-Process)', cmd: 'Get-Process | Select-Object -First 15\r' },
  { label: '文件目录 (dir)', cmd: 'dir\r' },
  { label: '驱动磁盘 (Get-PSDrive)', cmd: 'Get-PSDrive -PSProvider FileSystem\r' },
  { label: '网络配置 (ipconfig)', cmd: 'ipconfig\r' },
  { label: '环境变量 (env:)', cmd: 'Get-ChildItem env:\r' },
  { label: '清理屏幕 (cls)', cmd: 'cls\r' },
];

export default defineComponent({
  name: 'TerminalView',
  setup() {
    const isFullscreen = ref(false);
    const terminalHost = ref(null);
    const activeCommandSet = ref(isWindowsClient ? 'windows' : 'linux');
    const tabs = ref([
      { id: 'tab-1', title: '终端 1', sessionId: '', isConnected: false, awaitingInput: null },
    ]);
    const activeTabId = ref('tab-1');

    // 保存终端运行时的非响应式对象
    // tabRuntimes: Map<string, { term, fitAddon, ws, sessionId, containerEl, resizeObserver, awaitingTimer }>
    const tabRuntimes = new Map();

    const activeTab = computed(() => {
      return tabs.value.find((t) => t.id === activeTabId.value) || tabs.value[0];
    });

    const displayedCommands = computed(() => {
      return activeCommandSet.value === 'windows' ? WINDOWS_COMMANDS : LINUX_COMMANDS;
    });

    async function closeSessionApi(id) {
      if (!id) return;
      try {
        await http(API.terminal.session(id), { method: 'DELETE' });
      } catch { }
    }

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
      };

      // 单例绑定用户键盘输入，避免重连时重复注册导致按键重复发送
      term.onData((dataChunk) => {
        if (runtime.ws && runtime.ws.readyState === WebSocket.OPEN) {
          runtime.ws.send(dataChunk);
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
          try { runtime.ws.close(); } catch { }
          runtime.ws = null;
        }
        if (runtime.sessionId) {
          closeSessionApi(runtime.sessionId);
          runtime.sessionId = '';
        }

        tab.isConnected = false;
        tab.awaitingInput = null;
        runtime.term.writeln('\x1b[38;2;56;189;248m[正在创建终端会话 (PTY Engine)...]\x1b[0m');

        const cols = runtime.term.cols > 0 ? runtime.term.cols : (isFullscreen.value ? 120 : 90);
        const rows = runtime.term.rows > 0 ? runtime.term.rows : (isFullscreen.value ? 36 : 28);

        const res = await http(API.terminal.sessions, {
          method: 'POST',
          body: { columns: cols, rows },
        });

        if (!res.ok || !res.data) {
          throw new Error(res.message || '创建终端会话失败');
        }

        const data = res.data;
        const sessionId = data?.sessionId || data?.SessionId;
        if (!sessionId) {
          throw new Error('未获取到有效的终端会话 ID，请确认服务已更新');
        }

        runtime.sessionId = sessionId;
        tab.sessionId = sessionId;

        const token = auth.token;
        const wsUrl = API.terminal.ws(runtime.sessionId, token);

        const ws = new WebSocket(wsUrl);
        runtime.ws = ws;

        ws.onopen = () => {
          if (runtime.ws !== ws) return;
          tab.isConnected = true;
          runtime.term.writeln('\x1b[38;2;16;185;129m[终端已连接成功]\x1b[0m');
          if (data.workingDirectory) {
            runtime.term.writeln(`\x1b[90m[工作目录: ${data.workingDirectory}]\x1b[0m\r\n`);
          }
          nextTick(() => {
            try { runtime.fitAddon.fit(); } catch { }
            runtime.term.focus();
          });
        };

        ws.onmessage = (event) => {
          if (runtime.ws !== ws) return;
          runtime.term.write(event.data);

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
          } catch { }
        });
        runtime.resizeObserver.observe(runtime.containerEl);
      }

      for (const [id, rt] of tabRuntimes.entries()) {
        rt.containerEl.style.display = id === tabId ? 'block' : 'none';
      }

      nextTick(() => {
        try { runtime.fitAddon.fit(); } catch { }
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

    function addTab() {
      const nextNum = tabs.value.length + 1;
      const newId = `tab-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
      tabs.value.push({
        id: newId,
        title: `终端 ${nextNum}`,
        sessionId: '',
        isConnected: false,
        awaitingInput: null,
      });
      switchTab(newId);
    }

    function closeTab(tabId, event) {
      if (event) event.stopPropagation();
      const index = tabs.value.findIndex((t) => t.id === tabId);
      if (index === -1) return;

      const runtime = tabRuntimes.get(tabId);
      if (runtime) {
        if (runtime.ws) {
          try { runtime.ws.close(); } catch { }
        }
        if (runtime.sessionId) {
          closeSessionApi(runtime.sessionId);
        }
        if (runtime.resizeObserver) {
          runtime.resizeObserver.disconnect();
        }
        try { runtime.term.dispose(); } catch { }
        if (runtime.containerEl && runtime.containerEl.parentElement) {
          runtime.containerEl.parentElement.removeChild(runtime.containerEl);
        }
        tabRuntimes.delete(tabId);
      }

      tabs.value.splice(index, 1);

      if (tabs.value.length === 0) {
        addTab();
      } else if (activeTabId.value === tabId) {
        const nextActive = tabs.value[Math.max(0, index - 1)];
        switchTab(nextActive.id);
      }
    }

    function sendCtrlC() {
      const runtime = tabRuntimes.get(activeTabId.value);
      if (!runtime || !runtime.ws || runtime.ws.readyState !== WebSocket.OPEN) return;
      runtime.ws.send('\x03');
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
        runtime.isConnecting = false;
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
          } catch { }
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
      runtime.ws.send(cmd);
      runtime.term.focus();
    }

    function toggleCommandSet() {
      activeCommandSet.value = activeCommandSet.value === 'windows' ? 'linux' : 'windows';
    }

    onMounted(() => {
      mountTab(activeTabId.value);
    });

    onBeforeUnmount(() => {
      for (const [, runtime] of tabRuntimes.entries()) {
        if (runtime.ws) {
          try { runtime.ws.close(); } catch { }
        }
        if (runtime.sessionId) {
          closeSessionApi(runtime.sessionId);
        }
        if (runtime.resizeObserver) {
          runtime.resizeObserver.disconnect();
        }
        try { runtime.term.dispose(); } catch { }
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
          <button type="button" class="btn btn-xs" @click="sendCtrlC()" title="发送 SIGINT 中断信号">^C</button>
          <button type="button" class="btn btn-xs" @click="clearOutput()" title="清屏">清屏</button>
          <button type="button" class="btn btn-xs" @click="reconnectCurrent()" title="重新建立当前终端会话">重连</button>
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
