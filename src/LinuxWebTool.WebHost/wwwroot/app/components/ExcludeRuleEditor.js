import { computed, defineComponent, ref } from 'vue';
import { http } from '../api/client.js';
import { API } from '../config.js';
import { toast } from '../store/toast.js';

export default defineComponent({
  name: 'ExcludeRuleEditor',
  props: {
    modelValue: { type: String, default: '' },
    sourcePath: { type: String, default: '' },
    targetPath: { type: String, default: '' },
  },
  emits: ['update:modelValue', 'open-exclude-picker'],
  setup(props, { emit }) {
    const probingExcludes = ref(false);
    const probeResultModal = ref(false);
    const probeResult = ref(null);
    const selectedRecommendationPatterns = ref(new Set());

    const commonPresets = [
      { name: 'Node.js', patterns: ['node_modules/', '.next/', '.nuxt/', 'dist/', '.turbo/', 'npm-debug.log*', 'yarn-error.log*'] },
      { name: '.NET', patterns: ['bin/', 'obj/', '.vs/', '*.user', '*.suo', 'TestResults/'] },
      { name: 'Python', patterns: ['__pycache__/', '*.pyc', '*.pyo', '.venv/', 'venv/', 'env/'] },
      { name: 'VCS', patterns: ['.git/', '.svn/', '.hg/'] },
      { name: '临时/日志', patterns: ['*.log', '*.tmp', '*.temp', 'tmp/', 'temp/', '.DS_Store', 'Thumbs.db'] },
    ];

    const excludeChips = computed(() => {
      if (!props.modelValue) return [];
      return props.modelValue
        .split(/[;,]/)
        .map((s) => s.trim())
        .filter(Boolean);
    });

    function updateModel(chips) {
      emit('update:modelValue', chips.join('; '));
    }

    function removeChip(index) {
      const current = [...excludeChips.value];
      current.splice(index, 1);
      updateModel(current);
    }

    function clearAll() {
      emit('update:modelValue', '');
    }

    function onInputChange(e) {
      emit('update:modelValue', e.target.value);
    }

    function applyPreset(preset) {
      if (!preset?.patterns?.length) return;
      const current = [...excludeChips.value];
      let added = 0;
      for (const pat of preset.patterns) {
        if (!current.includes(pat)) {
          current.push(pat);
          added++;
        }
      }
      updateModel(current);
      toast.success(`已添加「${preset.name}」(${added} 条规则)`);
    }

    async function runProbe() {
      const path = props.sourcePath?.trim();
      if (!path) return toast.error('请先填写源目录路径');
      probingExcludes.value = true;
      try {
        const res = await http(API.backupSync.probeExcludes, {
          method: 'POST',
          body: { path },
        });
        if (res.ok && res.data) {
          probeResult.value = res.data;
          selectedRecommendationPatterns.value = new Set(
            (res.data.recommendations || []).filter((r) => r.isRecommended).map((r) => r.pattern)
          );
          probeResultModal.value = true;
          if (!res.data.recommendations?.length) {
            toast.info('未在源目录探针到典型开发缓存或临时文件');
          }
        } else {
          toast.error(res.message || '智能嗅探执行失败');
        }
      } catch (e) {
        toast.error(e?.message || '智能嗅探执行失败');
      } finally {
        probingExcludes.value = false;
      }
    }

    function toggleRecommendation(pattern) {
      if (selectedRecommendationPatterns.value.has(pattern)) {
        selectedRecommendationPatterns.value.delete(pattern);
      } else {
        selectedRecommendationPatterns.value.add(pattern);
      }
    }

    function isRecommendationSelected(pattern) {
      return selectedRecommendationPatterns.value.has(pattern);
    }

    function toggleSelectAllRecommendations() {
      const recs = probeResult.value?.recommendations || [];
      if (!recs.length) return;
      if (selectedRecommendationPatterns.value.size === recs.length) {
        selectedRecommendationPatterns.value.clear();
      } else {
        selectedRecommendationPatterns.value = new Set(recs.map((r) => r.pattern));
      }
    }

    function applySelectedRecommendations() {
      if (!selectedRecommendationPatterns.value.size) {
        toast.info('未选择任何推荐规则');
        probeResultModal.value = false;
        return;
      }
      const current = [...excludeChips.value];
      let added = 0;
      for (const pat of selectedRecommendationPatterns.value) {
        if (!current.includes(pat)) {
          current.push(pat);
          added++;
        }
      }
      updateModel(current);
      probeResultModal.value = false;
      toast.success(`成功采纳 ${added} 条推荐排除规则`);
    }

    function closeProbeModal() {
      probeResultModal.value = false;
    }

    function openPicker(target) {
      emit('open-exclude-picker', target);
    }

    return {
      probingExcludes,
      probeResultModal,
      probeResult,
      selectedRecommendationPatterns,
      commonPresets,
      excludeChips,
      removeChip,
      clearAll,
      onInputChange,
      applyPreset,
      runProbe,
      toggleRecommendation,
      isRecommendationSelected,
      toggleSelectAllRecommendations,
      applySelectedRecommendations,
      closeProbeModal,
      openPicker,
    };
  },
  template: `
    <div>
      <div class="flex flex-wrap items-center justify-between gap-1 mb-1.5">
        <label class="text-xs text-slate-300 font-medium flex items-center gap-1.5">
          <span>🛡️ 排除规则</span>
          <span class="text-slate-500 font-normal">({{ excludeChips.length }} 条生效)</span>
        </label>
        <div class="flex flex-wrap items-center gap-1">
          <button
            type="button"
            class="btn btn-xs border-cyan-500/40 text-cyan-300 hover:bg-cyan-500/10 flex items-center gap-1"
            :disabled="probingExcludes || !sourcePath"
            @click="runProbe()"
            title="对源目录进行浅层探针嗅探并推荐常见依赖与临时规则"
          >
            <span>{{ probingExcludes ? '⏳ 嗅探中...' : '🤖 智能探测' }}</span>
          </button>
          <button
            type="button"
            class="btn btn-xs border-indigo-500/40 text-indigo-300 hover:bg-indigo-500/10 flex items-center gap-1"
            :disabled="!sourcePath"
            @click="openPicker('source')"
            title="在源目录文件树中点选要排除的目录或文件"
          >
            <span>📂 源端点选</span>
          </button>
          <button
            v-if="targetPath"
            type="button"
            class="btn btn-xs border-purple-500/40 text-purple-300 hover:bg-purple-500/10 flex items-center gap-1"
            @click="openPicker('target')"
            title="在镜像目标目录文件树中点选要排除的目录或文件"
          >
            <span>📂 目标端点选</span>
          </button>
          <button
            v-if="excludeChips.length > 0"
            type="button"
            class="btn btn-xs text-slate-400 hover:text-rose-400"
            @click="clearAll()"
            title="清空所有排除规则（零黑盒：完整同步所有文件）"
          >
            清空
          </button>
        </div>
      </div>

      <!-- 常用预设快捷添加栏 -->
      <div class="flex flex-wrap items-center gap-1.5 mb-2 py-1 px-2 rounded bg-slate-900/60 border border-cyber-line/40 text-[11px]">
        <span class="text-slate-500">常用预设:</span>
        <button
          v-for="p in commonPresets"
          :key="p.name"
          type="button"
          class="badge border-cyber-line hover:border-cyan-400 hover:text-cyan-300 cursor-pointer transition py-0.5 px-1.5"
          @click="applyPreset(p)"
        >
          + {{ p.name }}
        </button>
      </div>

      <!-- 可视化规则胶囊 Chips 展示与快速删除 -->
      <div v-if="excludeChips.length > 0" class="flex flex-wrap gap-1.5 p-2 rounded-lg bg-slate-950/60 border border-cyber-line/60 mb-2 max-h-28 overflow-y-auto">
        <span
          v-for="(chip, idx) in excludeChips"
          :key="chip"
          class="inline-flex items-center gap-1 px-2 py-0.5 rounded text-xs font-mono transition bg-slate-800/80 border text-slate-200"
          :class="chip.endsWith('/') ? 'border-cyan-500/40 text-cyan-300' : 'border-slate-700/80 text-slate-300'"
        >
          <span>{{ chip.endsWith('/') ? '📁' : '📄' }}</span>
          <span class="truncate max-w-[14rem]" :title="chip">{{ chip }}</span>
          <button
            type="button"
            class="ml-0.5 text-slate-400 hover:text-rose-300 transition"
            @click="removeChip(idx)"
            title="移除此规则"
          >
            ✕
          </button>
        </span>
      </div>
      <div v-else class="text-[11px] text-slate-500 italic mb-2 px-1">
        未配置排除规则（零黑盒所见即所行：将 100% 完整备份/同步源目录下的所有文件）。
      </div>

      <!-- 底层纯文本编辑（双向响应式同步，满足高级开发者批量粘贴） -->
      <input
        type="text"
        :value="modelValue"
        @input="onInputChange($event)"
        placeholder="例如：node_modules; *.tmp; .git; *.cache (分号分隔)"
        class="input font-mono text-xs w-full"
      />

      <!-- 智能探测排除规则推荐审核弹窗 (零隐藏逻辑，用户显式勾选采纳) -->
      <div v-if="probeResultModal" class="fixed inset-0 z-[86] flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
        <div class="panel w-full max-w-2xl p-5 max-h-[85vh] flex flex-col border border-cyan-500/40 shadow-2xl" style="background: rgba(13, 21, 38, 0.98)">
          <div class="flex items-center gap-2 mb-3">
            <span class="text-base text-neon-soft font-bold">🤖 智能排除规则嗅探分析</span>
            <button class="btn btn-xs ml-auto" @click="closeProbeModal()">✕</button>
          </div>

          <div class="text-xs text-slate-400 mb-3 bg-slate-900/60 p-2.5 rounded border border-cyber-line/50 flex flex-col gap-1">
            <div class="flex items-center gap-2">
              <span class="text-slate-500">探测目录:</span>
              <span class="font-mono text-cyan-300 truncate" :title="probeResult?.scannedPath">{{ probeResult?.scannedPath }}</span>
            </div>
            <div class="flex items-center gap-2 mt-0.5">
              <span class="text-slate-500">识别技术栈:</span>
              <div class="flex flex-wrap gap-1">
                <span v-for="stack in (probeResult?.detectedTechStacks || [])" :key="stack"
                      class="badge border-cyan-500/50 text-cyan-300 text-[10px] py-0 px-1.5">
                  {{ stack }}
                </span>
                <span v-if="!probeResult?.detectedTechStacks?.length" class="text-slate-500 text-[11px]">未识别到特定开发技术栈</span>
              </div>
            </div>
          </div>

          <div class="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span>推荐排除项 (勾选项将合并至任务配置，零后台黑盒):</span>
            <button type="button" class="btn btn-xs" @click="toggleSelectAllRecommendations()">
              {{ selectedRecommendationPatterns.size === (probeResult?.recommendations?.length || 0) ? '取消全选' : '全部选择' }}
            </button>
          </div>

          <!-- 推荐项列表 -->
          <div class="border border-cyber-line rounded-lg overflow-hidden flex-1 min-h-[12rem] overflow-y-auto mb-3">
            <div v-if="!probeResult?.recommendations?.length" class="text-center py-10 text-slate-500 text-xs">
              源目录未发现典型临时文件、缓存或巨型第三方依赖库
            </div>
            <div v-else class="divide-y divide-cyber-line/40">
              <div
                v-for="item in probeResult.recommendations"
                :key="item.pattern"
                class="p-2.5 flex items-start gap-3 hover:bg-cyan-500/5 cursor-pointer transition"
                :class="{ 'bg-cyan-500/10': isRecommendationSelected(item.pattern) }"
                @click="toggleRecommendation(item.pattern)"
              >
                <input
                  type="checkbox"
                  class="mt-1"
                  :checked="isRecommendationSelected(item.pattern)"
                  @click.stop="toggleRecommendation(item.pattern)"
                />
                <div class="min-w-0 flex-1">
                  <div class="flex items-center gap-2">
                    <span class="font-mono text-xs font-bold text-neon-soft">{{ item.pattern }}</span>
                    <span class="badge border-cyber-line text-[10px] py-0 px-1 text-slate-400">{{ item.category }}</span>
                    <span
                      class="badge text-[10px] py-0 px-1"
                      :class="item.impactLevel === 'High' ? 'border-rose-500/50 text-rose-300' : 'border-amber-500/50 text-amber-300'"
                    >
                      {{ item.impactLevel === 'High' ? '建议排除' : '可选' }}
                    </span>
                  </div>
                  <div class="text-[11px] text-slate-400 mt-0.5">{{ item.reason }}</div>
                  <div v-if="item.matchedPaths?.length" class="text-[10px] text-slate-500 font-mono mt-1 truncate">
                    探针命中: {{ item.matchedPaths.join(', ') }}
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2">
            <button class="btn btn-xs" @click="closeProbeModal()">取消</button>
            <button
              class="btn btn-xs btn-primary bg-cyan-600 hover:bg-cyan-500"
              :disabled="selectedRecommendationPatterns.size === 0"
              @click="applySelectedRecommendations()"
            >
              采纳已选规则 ({{ selectedRecommendationPatterns.size }} 项)
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
});
