/**
 * 长列表通用分页适配器与状态机 (Pagination Adapter & State Machine)
 * 采用适配器模式统一异构数据源输入，结合状态机保证切页、过滤、条数变更的状态自愈与单向流转。
 */
import { computed, ref, watch } from 'vue';

export const PagingState = {
  IDLE: 'idle',
  FILTERING: 'filtering',
  PAGING: 'paging',
};

/**
 * 通用响应式内存分页适配器
 * @param {import('vue').Ref<Array>} itemsRef 原始列表响应式引用
 * @param {Object} options 配置项
 * @returns 分页状态与控制集合
 */
export function usePagination(itemsRef, options = {}) {
  const {
    defaultPageSize = 20,
    pageSizeOptions = [15, 25, 50, 100],
    initialPage = 1,
  } = options;

  const currentPage = ref(initialPage);
  const pageSize = ref(defaultPageSize);
  const state = ref(PagingState.IDLE);

  const totalItems = computed(() => (itemsRef.value ? itemsRef.value.length : 0));
  const totalPages = computed(() => Math.max(1, Math.ceil(totalItems.value / pageSize.value)));

  // 状态机自校准：当数据量或每页条数变化导致当前页超出总页数时，自愈至末页
  watch([totalItems, pageSize], () => {
    state.value = PagingState.FILTERING;
    if (currentPage.value > totalPages.value) {
      currentPage.value = Math.max(1, totalPages.value);
    }
    state.value = PagingState.IDLE;
  });

  // 内存切片层
  const pagedItems = computed(() => {
    const list = itemsRef.value || [];
    if (list.length === 0) return [];
    const start = (currentPage.value - 1) * pageSize.value;
    return list.slice(start, start + pageSize.value);
  });

  const startIndex = computed(() => {
    if (totalItems.value === 0) return 0;
    return (currentPage.value - 1) * pageSize.value + 1;
  });

  const endIndex = computed(() => {
    if (totalItems.value === 0) return 0;
    return Math.min(currentPage.value * pageSize.value, totalItems.value);
  });

  function setPage(page) {
    state.value = PagingState.PAGING;
    const target = Math.max(1, Math.min(page, totalPages.value));
    currentPage.value = target;
    state.value = PagingState.IDLE;
  }

  function nextPage() {
    if (currentPage.value < totalPages.value) {
      setPage(currentPage.value + 1);
    }
  }

  function prevPage() {
    if (currentPage.value > 1) {
      setPage(currentPage.value - 1);
    }
  }

  function firstPage() {
    setPage(1);
  }

  function lastPage() {
    setPage(totalPages.value);
  }

  function resetPage() {
    currentPage.value = 1;
  }

  return {
    currentPage,
    pageSize,
    pageSizeOptions,
    state,
    pagedItems,
    totalItems,
    totalPages,
    startIndex,
    endIndex,
    setPage,
    nextPage,
    prevPage,
    firstPage,
    lastPage,
    resetPage,
  };
}
