import { computed, defineComponent, onMounted, onUnmounted, reactive, ref } from 'vue';
import { http, httpDownload } from '../api/client.js';
import { API } from '../config.js';
import { openConfirm } from '../store/modal.js';
import { toast } from '../store/toast.js';
import FilePicker from '../components/FilePicker.js';
import {
  formatBytes, formatDuration, formatTime, transcodeModeLabel, transcodeStatusMeta, transcodeTriggerLabel,
} from '../utils/format.js';

const TABS = [
  { key: 'submit', label: '一次性转码' },
  { key: 'queue', label: '任务队列' },
  { key: 'presets', label: '转码预设' },
  { key: 'watch', label: '监听规则' },
];

// 源为文件夹时的常用媒体扩展
const SUGGESTED_PATTERNS = '.mkv,.avi,.mov,.wmv,.flv,.ts,.m2ts,.mp4';

export default defineComponent({
  name: 'TranscodeView',
  components: { FilePicker },
  setup() {
    const tab = ref('submit');
    const ffmpeg = reactive({ available: false, version: '', message: '' });
    const presets = ref([]);
    const importInput = ref(null);

    // ---- 一次性提交表单 ----
    const submitForm = reactive({
      sourcePath: '', presetId: '', customArgs: '', outputContainer: 'mp4',
      outputMode: 1, filePatterns: SUGGESTED_PATTERNS, recursive: true, outputDir: '',
      useHardwareAccel: true,
    });
    const submitting = ref(false);

    // ---- 队列 ----
    const jobs = ref([]);
    const jobTotal = ref(0);
    const jobQuery = reactive({ page: 1, pageSize: 20, status: '' });
    const jobLoading = ref(false);
    const actJobId = ref(null);
    const commandView = reactive({ show: false, command: '', label: '' });

    function showCommand(job) {
      commandView.command = job.commandLine || '';
      commandView.label = `${job.sourcePath} → ${job.outputPath || ''}`;
      commandView.show = true;
    }

    // ---- 预设编辑 ----
    const showPresetEditor = ref(false);
    const editingPresetId = ref(null);
    const presetSaving = ref(false);
    const presetForm = reactive({
      name: '', container: 'mp4', videoCodec: 'libx264', videoQuality: 23,
      audioCodec: 'aac', audioBitrate: '128k', extraArgs: '', description: '',
    });
    const presetCodecOptions = ['libx264', 'libx265', 'copy', ''];
    const presetAudioOptions = ['aac', 'libmp3lame', 'copy', ''];

    // ---- 监听规则 ----
    const watchRules = ref([]);
    const showWatchEditor = ref(false);
    const editingWatchId = ref(null);
    const watchSaving = ref(false);
    const watchForm = reactive({
      name: '', watchPath: '', filePatterns: SUGGESTED_PATTERNS, presetId: '',
      outputMode: 1, recursive: true, mode: 0, pollSeconds: 300, enabled: true,
      useHardwareAccel: true,
    });

    const loading = ref(false);
    const loadError = ref('');
    let timer = null;

    // ---- 可视化选择器 ----
    const picker = reactive({ show: false, mode: 'folder', target: '', startPath: '/' });
    function openPicker(mode, target, startPath) {
      picker.mode = mode;
      picker.target = target;
      picker.startPath = startPath || '/';
      picker.show = true;
    }
    function onPickerSelect(path) {
      picker.show = false;
      if (picker.target === 'source') {
        submitForm.sourcePath = path;
      } else if (picker.target === 'watch') {
        watchForm.watchPath = path;
      }
    }
    function onPickerClose() {
      picker.show = false;
    }

    async function loadFfmpeg() {
      const result = await http(API.transcode.detectFfmpeg);
      if (result.ok && result.data && typeof result.data.available === 'boolean') {
        Object.assign(ffmpeg, result.data);
      } else if (!result.ok) {
        throw new Error(result.message || 'ffmpeg 状态检测失败');
      } else {
        throw new Error('ffmpeg 检测响应格式异常');
      }
    }

    async function loadPresets() {
      const result = await http(API.transcode.presets);
      if (result.ok && Array.isArray(result.data)) {
        presets.value = result.data;
      } else if (!result.ok) {
        throw new Error(result.message || '转码预设加载失败');
      } else {
        throw new Error('转码预设响应格式异常');
      }
    }

    async function exportPresets() {
      const result = await httpDownload(API.transcode.presetExport);
      if (result.ok) {
        toast.success(`已导出 ${presets.value.length} 个预设（${result.filename}）`);
      }
    }

    function triggerImport() {
      const input = importInput.value;
      if (input) input.click();
    }

    async function onImportFile(event) {
      const file = event.target.files && event.target.files[0];
      event.target.value = ''; // 允许重复选择同一文件
      if (!file) return;
      if (!file.name.toLowerCase().endsWith('.json')) {
        toast.error('请选择 .json 预设文件');
        return;
      }
      let items;
      try {
        const text = await file.text();
        const parsed = JSON.parse(text);
        if (!Array.isArray(parsed)) throw new Error('文件内容不是预设数组');
        items = parsed;
      } catch (e) {
        toast.error(`预设文件解析失败：${e.message}`);
        return;
      }
      const result = await http(API.transcode.presetImport, { method: 'POST', body: items });
      if (!result.ok) return;
      if (result.data.imported > 0) {
        toast.success(`导入 ${result.data.imported} 个预设，跳过 ${result.data.skipped} 个`);
      } else {
        toast.info(result.data.skipped > 0 ? '无新预设可导入（已存在同名）' : '文件里没有可导入的预设');
      }
      await loadPresets();
    }

    async function loadJobs() {
      jobLoading.value = true;
      try {
        const result = await http(API.transcode.jobs, {
          params: {
            page: jobQuery.page, pageSize: jobQuery.pageSize,
            status: jobQuery.status, watchRuleId: undefined,
          },
        });
        if (result.ok && result.data && Array.isArray(result.data.items) && Number.isFinite(Number(result.data.total))) {
          jobs.value = result.data.items;
          jobTotal.value = Number(result.data.total);
        } else if (!result.ok) {
          throw new Error(result.message || '转码任务加载失败');
        } else {
          throw new Error('转码任务响应格式异常');
        }
      } catch (error) {
        loadError.value = error?.message || '转码任务加载失败';
      } finally {
        jobLoading.value = false;
      }
    }

    async function loadWatch() {
      const result = await http(API.transcode.watchRules);
      if (result.ok && Array.isArray(result.data)) {
        watchRules.value = result.data;
      } else if (!result.ok) {
        throw new Error(result.message || '监听规则加载失败');
      } else {
        throw new Error('监听规则响应格式异常');
      }
    }

    async function loadAll() {
      loading.value = true;
      loadError.value = '';
      try {
        const results = await Promise.allSettled([loadFfmpeg(), loadPresets(), loadJobs(), loadWatch()]);
        const failed = results.find((item) => item.status === 'rejected');
        if (failed) {
          loadError.value = failed.reason?.message || '转码页面初始化失败';
        }
      } catch (error) {
        loadError.value = error?.message || '转码页面初始化失败';
      } finally {
        loading.value = false;
      }
    }

    // ---- 一次性提交 ----
    function resetSubmit() {
      Object.assign(submitForm, {
        sourcePath: '', presetId: presets.value[0]?.id || '', customArgs: '', outputContainer: 'mp4',
        outputMode: 1, filePatterns: SUGGESTED_PATTERNS, recursive: true, outputDir: '',
      });
    }

    async function submit() {
      if (!submitForm.sourcePath.trim()) return toast.error('请填写源文件 / 源文件夹路径');
      if (!submitForm.customArgs && !submitForm.presetId) return toast.error('请选择预设或填写自定义参数');
      submitting.value = true;
      try {
        const result = await http(API.transcode.submit, {
          method: 'POST',
          body: {
            sourcePath: submitForm.sourcePath,
            presetId: submitForm.presetId || null,
            customArgs: submitForm.customArgs || null,
            outputContainer: submitForm.customArgs ? (submitForm.outputContainer || null) : null,
            outputMode: Number(submitForm.outputMode),
            filePatterns: submitForm.filePatterns || null,
            recursive: submitForm.recursive,
            outputDir: submitForm.outputDir || null,
            useHardwareAccel: submitForm.useHardwareAccel,
          },
        });
        if (result.ok) {
          toast.success(result.data.message);
          tab.value = 'queue';
          await loadJobs();
        }
      } finally {
        submitting.value = false;
      }
    }

    async function cancelJob(job) {
      actJobId.value = job.id;
      try {
        const result = await http(API.transcode.jobCancel(job.id), { method: 'POST' });
        if (result.ok) toast.success(result.data.message);
        await loadJobs();
      } finally {
        actJobId.value = null;
      }
    }

    async function retryJob(job) {
      actJobId.value = job.id;
      try {
        const result = await http(API.transcode.jobRetry(job.id), { method: 'POST' });
        if (result.ok) toast.success(result.data.message);
        await loadJobs();
      } finally {
        actJobId.value = null;
      }
    }

    function clearFinished() {
      openConfirm({
        title: '清理已完成的任务记录',
        message: '将删除所有已结束（成功/失败/取消/中断）的任务记录，日志文件保留。确定？',
        confirmText: '清理',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.transcode.jobClearFinished, { method: 'POST' });
          if (result.ok) {
            toast.success(result.data.message);
            await loadJobs();
          }
        },
      });
    }

    // ---- 预设 ----
    function openPresetCreate() {
      editingPresetId.value = null;
      Object.assign(presetForm, {
        name: '', container: 'mp4', videoCodec: 'libx264', videoQuality: 23,
        audioCodec: 'aac', audioBitrate: '128k', extraArgs: '', description: '',
      });
      showPresetEditor.value = true;
    }

    function openPresetEdit(preset) {
      editingPresetId.value = preset.id;
      Object.assign(presetForm, {
        name: preset.name, container: preset.container || 'mp4',
        videoCodec: preset.videoCodec || '', videoQuality: preset.videoQuality ?? 23,
        audioCodec: preset.audioCodec || '', audioBitrate: preset.audioBitrate || '',
        extraArgs: preset.extraArgs || '', description: preset.description || '',
      });
      showPresetEditor.value = true;
    }

    async function savePreset() {
      if (!presetForm.name.trim()) return toast.error('请输入预设名称');
      if (!presetForm.container.trim()) return toast.error('请输入输出格式');
      if (presetForm.videoCodec === '' && presetForm.audioCodec === '') return toast.error('视频 / 音频至少保留一项');
      presetSaving.value = true;
      try {
        const payload = {
          name: presetForm.name,
          container: presetForm.container,
          videoCodec: presetForm.videoCodec || null,
          videoQuality: presetForm.videoQuality === '' ? null : Number(presetForm.videoQuality),
          audioCodec: presetForm.audioCodec || null,
          audioBitrate: presetForm.audioBitrate || null,
          extraArgs: presetForm.extraArgs || null,
          description: presetForm.description || null,
        };
        const result = editingPresetId.value
          ? await http(API.transcode.presetItem(editingPresetId.value), { method: 'PUT', body: payload })
          : await http(API.transcode.presets, { method: 'POST', body: payload });
        if (result.ok) {
          toast.success(editingPresetId.value ? '预设已更新' : '预设已创建');
          showPresetEditor.value = false;
          await loadPresets();
        }
      } finally {
        presetSaving.value = false;
      }
    }

    function removePreset(preset) {
      openConfirm({
        title: '删除转码预设',
        message: `确定删除预设「${preset.name}」？若被监听规则或运行中任务引用将无法删除。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.transcode.presetItem(preset.id), { method: 'DELETE' });
          if (result.ok) {
            toast.success('预设已删除');
            await loadPresets();
          }
        },
      });
    }

    // ---- 监听规则 ----
    function presetById(id) {
      return presets.value.find((p) => p.id === id);
    }

    function openWatchCreate() {
      editingWatchId.value = null;
      Object.assign(watchForm, {
        name: '', watchPath: '', filePatterns: SUGGESTED_PATTERNS, presetId: presets.value[0]?.id || '',
        outputMode: 1, recursive: true, mode: 0, pollSeconds: 300, enabled: true,
      });
      showWatchEditor.value = true;
    }

    function openWatchEdit(rule) {
      editingWatchId.value = rule.id;
      Object.assign(watchForm, {
        name: rule.name, watchPath: rule.watchPath, filePatterns: rule.filePatterns || SUGGESTED_PATTERNS,
        presetId: rule.presetId, outputMode: rule.outputMode,
        recursive: rule.recursive, mode: rule.mode, pollSeconds: rule.pollSeconds, enabled: rule.enabled,
      });
      showWatchEditor.value = true;
    }

    async function saveWatch() {
      if (!watchForm.name.trim()) return toast.error('请输入规则名称');
      if (!watchForm.watchPath.trim()) return toast.error('请输入监听目录');
      if (!watchForm.presetId) return toast.error('请选择转码预设');
      watchSaving.value = true;
      try {
        const payload = {
          name: watchForm.name,
          watchPath: watchForm.watchPath,
          filePatterns: watchForm.filePatterns || null,
          presetId: watchForm.presetId,
          outputMode: Number(watchForm.outputMode),
          recursive: watchForm.recursive,
          mode: Number(watchForm.mode),
          pollSeconds: Number(watchForm.pollSeconds),
          enabled: watchForm.enabled,
          useHardwareAccel: watchForm.useHardwareAccel,
        };
        const result = editingWatchId.value
          ? await http(API.transcode.watchRuleItem(editingWatchId.value), { method: 'PUT', body: payload })
          : await http(API.transcode.watchRules, { method: 'POST', body: payload });
        if (result.ok) {
          toast.success(editingWatchId.value ? '监听规则已更新' : '监听规则已创建');
          showWatchEditor.value = false;
          await loadWatch();
        }
      } finally {
        watchSaving.value = false;
      }
    }

    async function toggleWatch(rule) {
      const result = await http(API.transcode.watchRuleToggle(rule.id), { method: 'POST' });
      if (result.ok) {
        toast.success(result.data.enabled ? '监听已启用' : '监听已停用');
        await loadWatch();
      }
    }

    function removeWatch(rule) {
      openConfirm({
        title: '删除监听规则',
        message: `确定删除规则「${rule.name}」？已入队的任务不受影响，正在监听的文件将停止自动转码。`,
        confirmText: '删除',
        danger: true,
        onConfirm: async () => {
          const result = await http(API.transcode.watchRuleItem(rule.id), { method: 'DELETE' });
          if (result.ok) {
            toast.success('规则已删除');
            await loadWatch();
          }
        },
      });
    }

    function isPathActive(p) {
      return jobs.value.some((j) =>
        (j.status === 0 || j.status === 1) && (j.sourcePath === p || (j.outputPath && j.outputPath === p)));
    }

    function watchStatus(rule) {
      if (!rule.enabled) return { label: '已停用', dot: 'bg-slate-500', class: 'border-slate-600/60 text-slate-500' };
      return { label: rule.mode === 1 ? '监听中(FSE)' : '监听中(轮询)', dot: 'bg-emerald-400', class: 'border-emerald-500/50 text-emerald-300' };
    }

    const totalPages = computed(() => Math.max(1, Math.ceil(jobTotal.value / jobQuery.pageSize)));
    function goPage(delta) {
      const next = jobQuery.page + delta;
      if (next < 1 || next > totalPages.value) return;
      jobQuery.page = next;
      loadJobs();
    }

    onMounted(() => {
      resetSubmit();
      loadAll();
      timer = setInterval(() => {
        loadJobs();
      }, 5000);
    });
    onUnmounted(() => {
      if (timer) clearInterval(timer);
    });

    return {
      TABS, tab, ffmpeg, presets,
      picker, openPicker, onPickerSelect, onPickerClose,
      submitForm, submitting, submit, resetSubmit,
      jobs, jobTotal, jobQuery, jobLoading, actJobId, cancelJob, retryJob, clearFinished, goPage, totalPages,
      commandView, showCommand,
      showPresetEditor, editingPresetId, presetSaving, presetForm, openPresetCreate, openPresetEdit, savePreset, removePreset,
      presetCodecOptions, presetAudioOptions,
      exportPresets, triggerImport, onImportFile, importInput,
      watchRules, showWatchEditor, editingWatchId, watchSaving, watchForm, openWatchCreate, openWatchEdit, saveWatch, toggleWatch, removeWatch,
      presetById, isPathActive, watchStatus,
      formatBytes, formatDuration, formatTime, transcodeStatusMeta, transcodeModeLabel, transcodeTriggerLabel, loading, loadError,
    };
  },
  template: `
    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="text-sm text-slate-400">FFmpeg 媒体转码</h2>
        <span v-if="ffmpeg.available" class="badge border-emerald-500/50 text-emerald-300">● ffmpeg 可用</span>
        <span v-else class="badge border-rose-500/50 text-rose-300">○ ffmpeg 不可用</span>
        <span v-if="ffmpeg.available && ffmpeg.hwEncoders && ffmpeg.hwEncoders.length"
              class="badge border-cyan-500/50 text-cyan-300" title="已检测到硬件视频编码器">⚡ 硬件加速可用</span>
        <span v-if="ffmpeg.available && (!ffmpeg.hwEncoders || !ffmpeg.hwEncoders.length)"
              class="badge border-slate-500/40 text-slate-400" title="未检测到可用的硬件视频编码器（需 GPU 直通，见文档）">○ 未检测到硬件加速</span>
        <div class="ml-auto flex gap-1">
          <button v-for="t in TABS" :key="t.key" class="btn btn-xs"
                  :class="tab === t.key ? 'btn-primary' : ''" @click="tab = t.key">{{ t.label }}</button>
        </div>
      </div>

      <div v-if="!ffmpeg.available" class="panel !border-rose-500/40 bg-rose-500/5 text-rose-200/90 text-xs leading-relaxed px-4 py-3">
        未检测到 ffmpeg，转码任务将无法执行。<br />
        Docker 镜像已内置；桌面部署请安装 ffmpeg（<code class="font-mono">apt install ffmpeg</code>/<code class="font-mono">brew install ffmpeg</code>/<code class="font-mono">winget install ffmpeg</code>）
        或通过环境变量 <code class="font-mono">Media__FfmpegPath</code> 指定绝对路径。<br />
        <span v-if="ffmpeg.message" class="text-rose-300/70">{{ ffmpeg.message }}</span>
      </div>

      <div v-if="ffmpeg.available" class="panel p-3 text-xs text-slate-400">
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-slate-500">硬件加速：</span>
          <template v-if="ffmpeg.hwEncoders && ffmpeg.hwEncoders.length">
            <span class="text-cyan-300 font-mono">{{ ffmpeg.hwEncoders.join(' · ') }}</span>
            <span class="text-slate-600">（需 GPU 直通，转码预设可选对应编码器）</span>
          </template>
          <span v-else class="text-slate-600">当前环境未检测到硬件编码器，将使用软件编码（libx264/libx265）。GPU 直通见 README「GPU（NVIDIA/Intel/AMD）」节。</span>
        </div>
      </div>
      <div v-if="loadError" class="panel !border-amber-500/40 bg-amber-500/5 text-amber-200/90 text-xs px-4 py-3 flex items-center gap-3">
        <span>{{ loadError }}</span>
        <button class="btn btn-xs ml-auto" @click="loadAll()">重试</button>
      </div>

      <!-- ============ 一次性转码 ============ -->
      <div v-show="tab === 'submit'" class="panel p-4 flex flex-col gap-3 max-w-3xl">
        <label class="block">
          <span class="text-xs text-slate-500 mb-1 block">源文件 / 源文件夹（服务器本地可访问路径）*</span>
          <div class="flex gap-2">
            <input class="input font-mono flex-1" v-model="submitForm.sourcePath" placeholder="/mnt/media/movies 或 /mnt/media/file.mkv" />
            <button class="btn btn-xs" title="可视化选择文件或文件夹" @click="openPicker('any', 'source', submitForm.sourcePath || '/')">📂 选择</button>
          </div>
          <p class="text-[11px] text-slate-600 mt-1">文件 → 单任务；文件夹 → 按扩展名过滤批量入队。路径直接在运行该服务的机器上解析。</p>
        </label>
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <label class="block">
            <span class="text-xs text-slate-500 mb-1 block">转码预设（或下方自定义参数）</span>
            <select class="input" v-model="submitForm.presetId">
              <option value="">使用自定义参数</option>
              <option v-for="p in presets" :key="p.id" :value="p.id">{{ p.name }}（{{ p.container }}）</option>
            </select>
          </label>
          <label class="block">
            <span class="text-xs text-slate-500 mb-1 block">输出模式</span>
            <select class="input" v-model="submitForm.outputMode">
              <option :value="0">替换源文件（先转临时文件，成功后删除源）</option>
              <option :value="1">并存（保留源文件，重名自动加序号）</option>
            </select>
          </label>
        </div>
        <label class="block">
          <span class="text-xs text-slate-500 mb-1 block">自定义 ffmpeg 参数（使用自定义参数时生效，{input}/{output} 之外的中间段）</span>
          <input class="input font-mono" v-model="submitForm.customArgs" placeholder="-c:v libx264 -crf 20 -c:a aac -b:a 128k" />
        </label>
        <div class="grid grid-cols-2 sm:grid-cols-3 gap-3">
          <label v-if="submitForm.customArgs" class="block">
            <span class="text-xs text-slate-500 mb-1 block">输出扩展名 *</span>
            <input class="input font-mono" v-model="submitForm.outputContainer" placeholder="mp4 / mkv / mp3" />
          </label>
          <label class="block">
            <span class="text-xs text-slate-500 mb-1 block">源为文件夹时的扩展名过滤</span>
            <input class="input font-mono" v-model="submitForm.filePatterns" placeholder=".mkv,.avi,.mov" />
          </label>
          <label class="block">
            <span class="text-xs text-slate-500 mb-1 block">输出目录（留空=源目录）</span>
            <input class="input font-mono" v-model="submitForm.outputDir" placeholder="/mnt/media/out" />
          </label>
        </div>
        <label class="flex items-center gap-2 text-sm text-slate-400">
          <input type="checkbox" v-model="submitForm.useHardwareAccel" class="accent-cyan-400" /> ⚡ 使用硬件加速
          <span class="text-[10px] text-slate-600 truncate" :title="ffmpeg.hwEncoders && ffmpeg.hwEncoders.length ? '当前环境支持：' + ffmpeg.hwEncoders.join(' · ') : '当前环境未检测到硬件编码器，将使用软件编码'">
            （{{
              ffmpeg.hwEncoders && ffmpeg.hwEncoders.length
                ? '检测到 ' + ffmpeg.hwEncoders.length + ' 个硬件编码器'
                : '当前环境未检测到，将自动回退软件编码'
            }}）
          </span>
        </label>
        <label class="flex items-center gap-2 text-sm text-slate-400">
          <input type="checkbox" v-model="submitForm.recursive" class="accent-cyan-400" /> 包含子文件夹（仅文件夹模式）
        </label>
        <div class="flex justify-end gap-2 mt-1">
          <button class="btn" @click="resetSubmit()">重置</button>
          <button class="btn btn-primary" :disabled="submitting" @click="submit()">{{ submitting ? '加入队列中…' : '⤓ 提交转码' }}</button>
        </div>
      </div>

      <!-- ============ 任务队列 ============ -->
      <div v-show="tab === 'queue'" class="flex flex-col gap-3">
        <div class="panel p-3 flex items-center flex-wrap gap-2">
          <select class="input !w-36" v-model="jobQuery.status">
            <option value="">全部状态</option>
            <option value="0">排队</option>
            <option value="1">转码中</option>
            <option value="2">成功</option>
            <option value="3">失败</option>
            <option value="4">已取消</option>
            <option value="5">中断</option>
          </select>
          <span class="text-xs text-slate-500">共 {{ jobTotal }} 个任务</span>
          <div class="ml-auto flex gap-2">
            <button class="btn btn-xs" @click="loadJobs()">刷新</button>
            <button class="btn btn-xs btn-danger" @click="clearFinished()">清理已结束</button>
          </div>
        </div>
        <div class="panel overflow-x-auto">
          <table class="data-table min-w-[62rem]">
            <thead>
              <tr><th>状态</th><th>源文件</th><th>输出</th><th>预设</th><th>进度</th><th>触发</th><th>时间</th><th class="text-right">操作</th></tr>
            </thead>
            <tbody>
              <tr v-if="!jobs.length && !jobLoading"><td colspan="8" class="text-slate-600 py-8 text-center">暂无转码任务，切换到「一次性转码」提交</td></tr>
              <tr v-for="job in jobs" :key="job.id">
                <td><span class="badge" :class="transcodeStatusMeta(job.status).class">{{ transcodeStatusMeta(job.status).label }}</span></td>
                <td class="max-w-[14rem]">
                  <div class="truncate font-mono text-xs text-cyan-300/80" :title="job.sourcePath">{{ job.sourcePath }}</div>
                  <div v-if="job.sourceSizeBytes" class="text-[10px] text-slate-600">{{ formatBytes(job.sourceSizeBytes) }}</div>
                </td>
                <td class="max-w-[10rem] truncate font-mono text-xs text-slate-400" :title="job.outputPath">{{ job.outputPath || '—' }}</td>
                <td class="text-xs text-slate-400">
                  {{ job.presetName || (job.customArgs ? '自定义' : '—') }}
                  <span class="ml-1 badge align-middle" :class="job.usedHardwareAccel ? 'border-cyan-500/50 text-cyan-300' : 'border-slate-500/40 text-slate-400'"
                        :title="job.usedHardwareAccel ? '本次实际使用硬件编码器' : (job.useHardwareAccel ? '请求了加速但回退为软件编码' : '未启用硬件加速')">
                    {{ job.usedHardwareAccel ? '⚡硬件' : '软件' }}
                  </span>
                </td>
                <td class="min-w-[6rem]">
                  <div class="flex items-center gap-2">
                    <div class="h-1.5 flex-1 rounded bg-slate-800 overflow-hidden">
                      <div class="h-full" :class="job.status === 1 ? 'bg-cyan-400' : 'bg-slate-600'"
                           :style="{ width: (job.progress || 0) + '%' }"></div>
                    </div>
                    <span class="text-[10px] text-slate-500 whitespace-nowrap">{{ (job.progress || 0).toFixed(1) }}%<template v-if="job.speedText"> · {{ job.speedText }}</template></span>
                  </div>
                  <div v-if="job.errorOutput" class="text-[10px] text-rose-300/80 truncate max-w-[12rem] mt-0.5" :title="job.errorOutput">{{ job.errorOutput }}</div>
                </td>
                <td class="text-xs text-slate-500 whitespace-nowrap">{{ transcodeTriggerLabel(job.trigger) }}</td>
                <td class="text-[10px] text-slate-500 whitespace-nowrap">{{ formatTime(job.startTime || job.queueTime) }}</td>
                <td class="text-right whitespace-nowrap">
                  <button v-if="job.commandLine" class="btn btn-xs" title="查看实际执行的 ffmpeg 命令" @click="showCommand(job)">命令</button>
                  <template v-if="job.status === 0 || job.status === 1">
                    <button class="btn btn-xs btn-danger" :disabled="actJobId === job.id" @click="cancelJob(job)">取消</button>
                  </template>
                  <template v-else-if="job.status === 3 || job.status === 4 || job.status === 5">
                    <button class="btn btn-xs" :disabled="actJobId === job.id" @click="retryJob(job)">重试</button>
                  </template>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-between text-sm text-slate-500">
          <span></span>
          <div class="flex items-center gap-2">
            <button class="btn btn-xs" :disabled="jobQuery.page <= 1" @click="goPage(-1)">上一页</button>
            <span>{{ jobQuery.page }} / {{ totalPages }}</span>
            <button class="btn btn-xs" :disabled="jobQuery.page >= totalPages" @click="goPage(1)">下一页</button>
          </div>
        </div>
      </div>

      <!-- ============ 转码预设 ============ -->
      <div v-show="tab === 'presets'" class="flex flex-col gap-3">
        <div class="flex items-center gap-2 flex-wrap">
          <span class="text-xs text-slate-500">预设用于一键配置 ffmpeg 参数；内置预设可编辑、可删除。</span>
          <div class="ml-auto flex gap-1.5">
            <button class="btn btn-xs" title="导出全部预设为 JSON 文件" @click="exportPresets()">⬇ 导出</button>
            <button class="btn btn-xs" title="从 JSON 文件导入预设" @click="triggerImport()">⬆ 导入</button>
            <button class="btn btn-primary" @click="openPresetCreate()">＋ 新建预设</button>
            <input ref="importInput" type="file" accept=".json" class="hidden" @change="onImportFile($event)" />
          </div>
        </div>
        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div v-for="p in presets" :key="p.id" class="panel p-4 flex flex-col gap-2">
            <div class="flex items-center gap-2">
              <h3 class="text-sm font-medium text-slate-200">{{ p.name }}
                <span v-if="p.isBuiltin" class="badge border-violet-500/50 text-violet-300 !text-[0.625rem]">内置</span>
              </h3>
              <div class="ml-auto flex gap-1">
                <button class="btn btn-xs" @click="openPresetEdit(p)">编辑</button>
                <button class="btn btn-xs btn-danger" @click="removePreset(p)">删除</button>
              </div>
            </div>
            <div class="font-mono text-xs text-cyan-300/90 break-all">[{{ p.container }}]
              {{ p.videoCodec ? '-c:v ' + p.videoCodec + (p.videoQuality != null ? ' -crf ' + p.videoQuality : '') : '-vn' }}
              {{ p.audioCodec ? ' | ' + (p.audioCodec === 'copy' ? '-c:a copy' : '-c:a ' + p.audioCodec + (p.audioBitrate ? ' -b:a ' + p.audioBitrate : '')) : ' | -an' }}
            </div>
            <p v-if="p.description" class="text-xs text-slate-500">{{ p.description }}</p>
          </div>
          <div v-if="!presets.length" class="text-slate-600 text-sm py-8 text-center">暂无预设，点右上角「新建预设」</div>
        </div>
      </div>

      <!-- ============ 监听规则 ============ -->
      <div v-show="tab === 'watch'" class="flex flex-col gap-3">
        <div class="flex items-center flex-wrap gap-2">
          <span class="text-xs text-slate-500">监听文件夹，新增匹配文件后自动入队转码。网络挂载盘（SMB）请用轮询模式。</span>
          <button class="btn btn-primary ml-auto" @click="openWatchCreate()">＋ 新建监听规则</button>
        </div>
        <div class="panel overflow-x-auto">
          <table class="data-table min-w-[56rem]">
            <thead>
              <tr><th>状态</th><th>名称</th><th>监听目录</th><th>扩展名</th><th>预设</th><th>输出/递归</th><th>扫描方式</th><th>上次扫描</th><th class="text-right">操作</th></tr>
            </thead>
            <tbody>
              <tr v-if="!watchRules.length"><td colspan="9" class="text-slate-600 py-8 text-center">暂无监听规则</td></tr>
              <tr v-for="rule in watchRules" :key="rule.id">
                <td><span class="badge" :class="watchStatus(rule).class"><span class="inline-block w-1.5 h-1.5 rounded-full mr-1.5 align-middle" :class="watchStatus(rule).dot"></span>{{ watchStatus(rule).label }}</span></td>
                <td class="text-slate-200">{{ rule.name }}</td>
                <td class="max-w-[14rem] truncate font-mono text-xs text-cyan-300/80" :title="rule.watchPath">{{ rule.watchPath }}</td>
                <td class="max-w-[10rem] truncate text-xs text-slate-500" :title="rule.filePatterns">{{ rule.filePatterns || '默认全集' }}</td>
                <td class="text-slate-400 text-xs">{{ presetById(rule.presetId)?.name || '—' }}</td>
                <td class="text-xs text-slate-500 whitespace-nowrap">{{ transcodeModeLabel(rule.outputMode) }}<template v-if="rule.recursive"> · 递归</template></td>
                <td class="text-xs text-slate-500 whitespace-nowrap">{{ rule.mode === 1 ? '文件事件' : '轮询 ' + rule.pollSeconds + 's' }}</td>
                <td class="text-[10px] text-slate-500 whitespace-nowrap">{{ formatTime(rule.lastScanTime) }}</td>
                <td class="text-right whitespace-nowrap">
                  <button class="badge" :class="rule.enabled ? 'border-emerald-500/50 text-emerald-300' : 'border-slate-600/60 text-slate-500'"
                          @click="toggleWatch(rule)">{{ rule.enabled ? '● 启用' : '○ 停用' }}</button>
                  <button class="btn btn-xs" @click="openWatchEdit(rule)">编辑</button>
                  <button class="btn btn-xs btn-danger" @click="removeWatch(rule)">删除</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- ============ 预设编辑弹窗 ============ -->
      <div v-if="showPresetEditor" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 max-h-[90vh] overflow-auto" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">{{ editingPresetId ? '编辑预设' : '新建预设' }}</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">预设名称 *</span>
              <input class="input" v-model="presetForm.name" placeholder="例如：MP4 H.264 高清" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">输出格式 / 扩展名 *</span>
              <input class="input font-mono" v-model="presetForm.container" placeholder="mp4 / mkv / mp3" />
            </label>
            <div class="grid grid-cols-2 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">视频编解码（空=去视频提取音频；可填硬件编码器如 h264_nvenc）</span>
                <input class="input font-mono" list="preset-codec-list" v-model="presetForm.videoCodec"
                       placeholder="libx264 / h264_nvenc / copy / 空" />
                <datalist id="preset-codec-list">
                  <option v-for="c in presetCodecOptions" :key="c" :value="c">{{ c || '（去视频）' }}</option>
                </datalist>
              </label>
              <label v-if="presetForm.videoCodec && presetForm.videoCodec !== 'copy'" class="block">
                <span class="text-xs text-slate-500 mb-1 block">CRF 质量（0~51）</span>
                <input class="input" type="number" min="0" max="51" v-model="presetForm.videoQuality" />
              </label>
            </div>
            <div class="grid grid-cols-2 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">音频编解码（空=去音频）</span>
                <select class="input" v-model="presetForm.audioCodec">
                  <option v-for="c in presetAudioOptions" :key="c" :value="c">{{ c || '（去音频）' }}</option>
                </select>
              </label>
              <label v-if="presetForm.audioCodec && presetForm.audioCodec !== 'copy'" class="block">
                <span class="text-xs text-slate-500 mb-1 block">音频码率</span>
                <input class="input font-mono" v-model="presetForm.audioBitrate" placeholder="128k / 192k" />
              </label>
            </div>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">额外 ffmpeg 参数（附加在输出前）</span>
              <input class="input font-mono" v-model="presetForm.extraArgs" placeholder="-movflags +faststart -preset medium" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">备注</span>
              <input class="input" v-model="presetForm.description" />
            </label>
          </div>
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showPresetEditor = false">取消</button>
            <button class="btn btn-primary" :disabled="presetSaving" @click="savePreset()">{{ presetSaving ? '保存中…' : '保存' }}</button>
          </div>
        </div>
      </div>

      <!-- ============ 监听规则编辑弹窗 ============ -->
      <div v-if="showWatchEditor" class="fixed inset-0 z-[80] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-lg p-5 max-h-[90vh] overflow-auto" style="background: rgba(13, 21, 38, 0.97)">
          <h3 class="font-display text-base text-neon-soft mb-4">{{ editingWatchId ? '编辑监听规则' : '新建监听规则' }}</h3>
          <div class="flex flex-col gap-3">
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">规则名称 *</span>
              <input class="input" v-model="watchForm.name" placeholder="例如：下载目录自动转码" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">监听目录 *</span>
              <div class="flex gap-2">
                <input class="input font-mono flex-1" v-model="watchForm.watchPath" placeholder="/mnt/media/movies" />
                <button class="btn btn-xs" title="可视化选择" @click="openPicker('folder', 'watch', watchForm.watchPath || '/')">📂 选择</button>
              </div>
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">扩展名过滤</span>
              <input class="input font-mono" v-model="watchForm.filePatterns" placeholder=".mkv,.avi,.mov" />
            </label>
            <label class="block">
              <span class="text-xs text-slate-500 mb-1 block">转码预设 *</span>
              <select class="input" v-model="watchForm.presetId">
                <option v-for="p in presets" :key="p.id" :value="p.id">{{ p.name }}（{{ p.container }}）</option>
              </select>
            </label>
            <div class="grid grid-cols-2 gap-3">
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">输出模式</span>
                <select class="input" v-model="watchForm.outputMode">
                  <option :value="0">替换源文件</option>
                  <option :value="1">并存</option>
                </select>
              </label>
              <label class="block">
                <span class="text-xs text-slate-500 mb-1 block">扫描方式</span>
                <select class="input" v-model="watchForm.mode">
                  <option :value="0">轮询（网络盘 / SMB，推荐）</option>
                  <option :value="1">文件事件（仅本地盘实时）</option>
                </select>
              </label>
            </div>
            <div class="grid grid-cols-2 gap-3">
              <label v-if="watchForm.mode === 0" class="block">
                <span class="text-xs text-slate-500 mb-1 block">轮询间隔（秒，最小 30）</span>
                <input class="input" type="number" min="30" max="86400" v-model="watchForm.pollSeconds" />
              </label>
              <label class="flex items-center gap-2 text-sm text-slate-400 mt-5">
                <input type="checkbox" v-model="watchForm.recursive" class="accent-cyan-400" /> 包含子目录
              </label>
            </div>
            <label class="flex items-center gap-2 text-sm text-slate-400">
              <input type="checkbox" v-model="watchForm.enabled" class="accent-cyan-400" /> 创建后启用
            </label>
            <label class="flex items-center gap-2 text-sm text-slate-400">
              <input type="checkbox" v-model="watchForm.useHardwareAccel" class="accent-cyan-400" /> ⚡ 使用硬件加速
              <span class="text-[10px] text-slate-600">（{{ ffmpeg.hwEncoders && ffmpeg.hwEncoders.length ? '检测到 ' + ffmpeg.hwEncoders.length + ' 个硬件编码器' : '未检测到，将回退软件编码' }}）</span>
            </label>
          </div>
          <div class="flex justify-end gap-2 mt-5">
            <button class="btn" @click="showWatchEditor = false">取消</button>
            <button class="btn btn-primary" :disabled="watchSaving" @click="saveWatch()">{{ watchSaving ? '保存中…' : '保存' }}</button>
          </div>
        </div>
      </div>

      <FilePicker :show="picker.show" :mode="picker.mode" :start-path="picker.startPath"
                  @select="onPickerSelect" @close="onPickerClose" />

      <!-- 实际转码命令查看弹窗 -->
      <div v-if="commandView.show" class="fixed inset-0 z-[85] flex items-center justify-center bg-black/60 backdrop-blur-sm p-4"
           @click.self="commandView.show = false">
        <div class="panel w-full max-w-3xl p-5 max-h-[88vh] flex flex-col" style="background: rgba(13, 21, 38, 0.97)">
          <div class="flex items-center gap-2 mb-3">
            <h3 class="font-display text-base text-neon-soft">实际转码命令</h3>
            <span class="text-xs text-slate-500 truncate flex-1" :title="commandView.label">{{ commandView.label }}</span>
            <button class="btn btn-xs ml-auto" @click="commandView.show = false">✕</button>
          </div>
          <pre class="flex-1 overflow-auto text-xs font-mono leading-relaxed p-3 bg-black/40 rounded-lg border border-cyber-line/40 whitespace-pre-wrap break-all">{{ commandView.command }}</pre>
          <div class="flex justify-end gap-2 mt-3">
            <button class="btn" @click="commandView.show = false">关闭</button>
          </div>
        </div>
      </div>
    </div>
  `,
});
