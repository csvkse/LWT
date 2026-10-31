import { defineComponent, ref } from 'vue';
import { API } from '../../config.js';
import { httpUpload } from '../../api/client.js';
import { toast } from '../../store/toast.js';

export default defineComponent({
  name: 'ExtensionZipImportModal',
  props: {
    show: { type: Boolean, default: false },
  },
  emits: ['close', 'imported'],
  setup(props, { emit }) {
    const selectedFile = ref(null);
    const fileInputRef = ref(null);
    const uploading = ref(false);

    function closeModal() {
      if (uploading.value) return;
      selectedFile.value = null;
      if (fileInputRef.value) fileInputRef.value.value = '';
      emit('close');
    }

    function onFileChange(e) {
      const file = e.target?.files?.[0];
      if (file) {
        if (!file.name.toLowerCase().endsWith('.zip')) {
          toast.error('仅支持上传 .zip 格式的压缩文件');
          return;
        }
        selectedFile.value = file;
      }
    }

    function formatSize(bytes) {
      if (!bytes) return '0 B';
      const k = 1024;
      const sizes = ['B', 'KB', 'MB', 'GB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return (bytes / Math.pow(k, i)).toFixed(1) + ' ' + sizes[i];
    }

    async function submitZip() {
      if (!selectedFile.value) {
        toast.error('请先选择要上传的扩展 Zip 压缩包');
        return;
      }

      uploading.value = true;
      try {
        toast.info('正在上传并校验扩展服务压缩包...');
        const res = await httpUpload(API.services.extensionImportZip, { file: selectedFile.value });
        if (res.ok && res.data?.success) {
          const count = res.data?.importedExtensionIds?.length || 0;
          toast.success(`成功导入 ${count} 个运维扩展服务！`);
          emit('imported');
          closeModal();
        } else {
          toast.error(res.message || res.data?.message || '导入扩展失败');
        }
      } catch (err) {
        toast.error('上传解压失败: ' + err.message);
      } finally {
        uploading.value = false;
      }
    }

    return {
      selectedFile,
      fileInputRef,
      uploading,
      closeModal,
      onFileChange,
      formatSize,
      submitZip,
    };
  },
  template: `
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-sm">
      <div class="panel max-w-lg w-full p-5 flex flex-col gap-4 border-cyber-line/50 animate-fade-in shadow-2xl">
        <div class="flex items-center justify-between border-b border-cyber-line/40 pb-3">
          <div class="flex items-center gap-2">
            <span class="text-xl">📦</span>
            <div>
              <div class="font-display font-semibold text-slate-100 text-sm">导入运维扩展 ZIP 压缩包</div>
              <div class="text-[11px] text-cyber-muted mt-0.5">支持单扩展包或 Mono-repo 多扩展压缩归档</div>
            </div>
          </div>
          <button @click="closeModal()" class="text-slate-400 hover:text-slate-200 transition-colors">✕</button>
        </div>

        <div class="flex flex-col gap-3 py-2">
          <div class="border-2 border-dashed border-cyber-line/60 hover:border-cyber-primary/70 rounded-lg p-6 flex flex-col items-center justify-center gap-3 bg-cyber-bg/40 transition-colors">
            <span class="text-3xl text-cyber-muted">📁</span>
            <div class="text-xs text-slate-300 text-center">
              点击下方按钮选择本地导出的 <span class="font-mono text-cyber-primary">.zip</span> 扩展包
            </div>
            <input
              ref="fileInputRef"
              type="file"
              accept=".zip,application/zip"
              class="hidden"
              @change="onFileChange($event)"
            />
            <button
              type="button"
              class="btn btn-secondary text-xs py-1.5 px-4"
              @click="$refs.fileInputRef?.click()"
            >
              浏览本地文件...
            </button>
            <div v-if="selectedFile" class="text-xs font-mono text-cyber-neon bg-cyber-primary/10 px-3 py-1.5 rounded border border-cyber-primary/30 mt-1">
              已选: {{ selectedFile.name }} ({{ formatSize(selectedFile.size) }})
            </div>
          </div>

          <div class="text-[11px] text-slate-400 bg-cyber-line/10 p-2.5 rounded border border-cyber-line/30 flex flex-col gap-1">
            <div class="font-semibold text-slate-300">💡 规范与安全说明：</div>
            <div>• 压缩包内需包含 <span class="font-mono text-slate-200">manifest.json</span> 元数据清单与钩子脚本（hooks 目录）。</div>
            <div>• 系统全程启用 Zip Slip 路径穿越防御，若已存在同名扩展将安全保留用户的现有 config.json 配置。</div>
          </div>
        </div>

        <div class="flex items-center justify-end gap-2 border-t border-cyber-line/40 pt-3">
          <button type="button" class="btn btn-ghost text-xs" :disabled="uploading" @click="closeModal()">
            取消
          </button>
          <button
            type="button"
            class="btn btn-primary text-xs flex items-center gap-1.5"
            :disabled="!selectedFile || uploading"
            @click="submitZip()"
          >
            <span v-if="uploading" class="animate-spin text-xs">⏳</span>
            <span>{{ uploading ? '正在导入中...' : '确认导入' }}</span>
          </button>
        </div>
      </div>
    </div>
  `,
});
