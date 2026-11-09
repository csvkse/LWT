import { defineComponent, ref } from 'vue';
import { http } from '../../api/client.js';
import { API } from '../../config.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'PluginCreateModal',
  props: {
    show: {
      type: Boolean,
      default: false,
    },
  },
  emits: ['close', 'created'],
  setup(props, { emit }) {
    const saving = ref(false);
    const form = ref({
      id: '',
      name: '',
      description: '',
      category: '自定义',
      profile: 0, // 0: Resident, 1: OneShot
      runtime: 1, // 0: Node, 1: Python, 2: DotNet, 3: Bash, 4: PowerShell, 5: Native, 6: Go
      entrypoint: 'main.py',
      supportHttp: false,
      initialPort: 0,
      autoRestart: true,
      enableKv: true,
      enableSqlite: true,
      codeTemplate: '#!/usr/bin/env python3\nimport sys\n\nprint("Hello from custom plugin!")\n',
    });

    function onRuntimeChange() {
      const rt = form.value.runtime;
      if (rt === 0) {
        form.value.entrypoint = 'index.js';
        form.value.codeTemplate = 'console.log("Hello from Node.js plugin!");\n';
      } else if (rt === 1) {
        form.value.entrypoint = 'main.py';
        form.value.codeTemplate = '#!/usr/bin/env python3\nprint("Hello from Python plugin!")\n';
      } else if (rt === 6) {
        form.value.entrypoint = 'main.go';
        form.value.codeTemplate = 'package main\n\nimport "fmt"\n\nfunc main() {\n\tfmt.Println("Hello from Go plugin!")\n}\n';
      } else if (rt === 3) {
        form.value.entrypoint = 'run.sh';
        form.value.codeTemplate = '#!/usr/bin/env bash\necho "Hello from Bash plugin!"\n';
      } else if (rt === 4) {
        form.value.entrypoint = 'run.ps1';
        form.value.codeTemplate = 'Write-Output "Hello from PowerShell plugin!"\n';
      } else if (rt === 7) {
        form.value.entrypoint = 'main.wasm';
        form.value.codeTemplate = ';; WebAssembly module placeholder\n';
      }
    }

    function handleClose() {
      emit('close');
    }

    async function handleCreate() {
      const f = form.value;
      if (!f.id.trim() || !f.name.trim()) {
        toast.warning('请输入插件 ID 与名称');
        return;
      }
      saving.value = true;
      try {
        const manifest = {
          id: f.id.trim(),
          name: f.name.trim(),
          version: '1.0.0',
          description: f.description.trim(),
          category: f.category.trim() || '自定义',
          profile: f.profile,
          runtime: f.runtime,
          entrypoint: f.entrypoint.trim(),
          network: {
            supportHttp: f.supportHttp,
            port: f.initialPort || 0,
            pathPrefix: `/plugins/${f.id.trim()}`,
          },
          supervision: {
            autoRestart: f.autoRestart,
            maxRestarts: 5,
          },
          storage: {
            enableKv: f.enableKv,
            enableSqlite: f.enableSqlite,
          },
          customActions: [],
          files: {},
        };
        if (f.codeTemplate && f.entrypoint) {
          manifest.files[f.entrypoint.trim()] = f.codeTemplate;
        }

        const res = await http(API.plugins.createOrUpdate, {
          method: 'POST',
          body: { manifest },
        });

        if (res.ok) {
          toast.success(`插件 [${f.name}] 创建成功！`);
          emit('created');
          emit('close');
        } else {
          toast.error(res.message || '创建插件失败');
        }
      } catch (err) {
        toast.error('创建插件异常: ' + err.message);
      } finally {
        saving.value = false;
      }
    }

    return {
      saving,
      form,
      onRuntimeChange,
      handleClose,
      handleCreate,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-3 bg-slate-950/80 backdrop-blur-sm">
      <div class="panel w-full max-w-xl p-5 border border-cyan-500/40 flex flex-col max-h-[90vh] shadow-2xl">
        <!-- 头部 -->
        <div class="flex items-center justify-between pb-3 border-b border-cyber-line/60">
          <div class="flex items-center gap-2">
            <span class="text-xl">✨</span>
            <div>
              <h3 class="font-display font-semibold text-slate-100 text-sm">
                创建新插件
              </h3>
              <div class="text-xs text-slate-400">
                定义插件清单并脚手架快速生成代码
              </div>
            </div>
          </div>
          <button type="button" class="btn-ghost p-1 text-slate-400 hover:text-white" @click="handleClose()">
            ✕
          </button>
        </div>

        <!-- 表单 -->
        <div class="py-4 space-y-3 overflow-y-auto flex-1 text-xs">
          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="text-slate-300 font-medium">插件 ID (字母、数字、短横线):</label>
              <input
                v-model="form.id"
                type="text"
                class="input-base text-xs w-full mt-1 font-mono"
                placeholder="例如: custom-monitor"
              />
            </div>
            <div>
              <label class="text-slate-300 font-medium">显示名称:</label>
              <input
                v-model="form.name"
                type="text"
                class="input-base text-xs w-full mt-1"
                placeholder="例如: 自定义系统巡检"
              />
            </div>
          </div>

          <div>
            <label class="text-slate-300 font-medium">描述说明:</label>
            <input
              v-model="form.description"
              type="text"
              class="input-base text-xs w-full mt-1"
              placeholder="简述插件的核心功能..."
            />
          </div>

          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="text-slate-300 font-medium">执行轮廓 (Profile):</label>
              <select v-model.number="form.profile" class="input-base text-xs w-full mt-1">
                <option :value="0">⚡ 常驻微服务与守护 (Resident)</option>
                <option :value="1">🔌 脚本轻量扩展 (OneShot)</option>
                <option :value="2">🚀 动态按需微服务 (Serverless FaaS)</option>
                <option :value="3">⚡ WebAssembly 微函数 (Wasm)</option>
              </select>
            </div>
            <div>
              <label class="text-slate-300 font-medium">运行语言 (Runtime):</label>
              <select v-model.number="form.runtime" class="input-base text-xs w-full mt-1" @change="onRuntimeChange()">
                <option :value="1">🐍 Python 3</option>
                <option :value="0">🟢 Node.js</option>
                <option :value="6">🐹 Go</option>
                <option :value="3">🐚 Bash</option>
                <option :value="4">💻 PowerShell</option>
                <option :value="2">⚡ .NET AOT</option>
                <option :value="7">⚡ WebAssembly (Wasm)</option>
              </select>
            </div>
          </div>

          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="text-slate-300 font-medium">入口文件名 (Entrypoint):</label>
              <input
                v-model="form.entrypoint"
                type="text"
                class="input-base text-xs w-full mt-1 font-mono"
              />
            </div>
            <div>
              <label class="text-slate-300 font-medium">分类 (Category):</label>
              <input
                v-model="form.category"
                type="text"
                class="input-base text-xs w-full mt-1"
                placeholder="例如: 监控 / 运维 / 工具"
              />
            </div>
          </div>

          <!-- 常驻与 Serverless 模式高级选项 -->
          <div v-if="form.profile === 0 || form.profile === 2" class="p-2.5 rounded bg-slate-900 border border-cyber-line/50 space-y-2">
            <div class="font-semibold text-slate-300">{{ form.profile === 2 ? '按需微服务 (Serverless) 选项:' : '常驻守护选项:' }}</div>
            <div class="grid grid-cols-2 gap-2">
              <label class="flex items-center gap-1.5 cursor-pointer">
                <input v-model="form.supportHttp" type="checkbox" class="rounded text-cyan-500" />
                <span>对外暴露 HTTP 接口</span>
              </label>
              <label class="flex items-center gap-1.5 cursor-pointer">
                <input v-model="form.autoRestart" type="checkbox" class="rounded text-cyan-500" />
                <span>崩溃自动重启 (Supervisor)</span>
              </label>
            </div>
          </div>

          <!-- 存储开关 -->
          <div class="p-2.5 rounded bg-slate-900 border border-cyber-line/50 space-y-2">
            <div class="font-semibold text-slate-300">独立沙箱存储:</div>
            <div class="grid grid-cols-2 gap-2">
              <label class="flex items-center gap-1.5 cursor-pointer">
                <input v-model="form.enableKv" type="checkbox" class="rounded text-cyan-500" />
                <span>启用 KV 键值数据库</span>
              </label>
              <label class="flex items-center gap-1.5 cursor-pointer">
                <input v-model="form.enableSqlite" type="checkbox" class="rounded text-cyan-500" />
                <span>启用 SQLite 独立数据库</span>
              </label>
            </div>
          </div>
        </div>

        <!-- 底部 -->
        <div class="pt-3 border-t border-cyber-line/50 flex items-center justify-end gap-2">
          <button type="button" class="btn-ghost text-xs" :disabled="saving" @click="handleClose()">
            取消
          </button>
          <button
            type="button"
            class="btn-primary text-xs flex items-center gap-1.5"
            :disabled="saving"
            @click="handleCreate()"
          >
            <span v-if="saving" class="animate-spin">⏳</span>
            <span>{{ saving ? '创建中...' : '确认创建' }}</span>
          </button>
        </div>
      </div>
    </div>
  `,
});
