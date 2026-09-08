// 唯一请求层：统一 { ok, data, status, message } 返回结构 + Bearer 头 + 自动错误提示。
// localStorage 仅本文件与 store/auth.js 允许访问（前端架构门禁 rule FE-STORAGE）。
import { API_BASE, LS_KEYS } from '../config.js';
import { toast } from '../store/toast.js';

function buildUrl(url, params) {
  if (!params) return API_BASE + url;
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      search.append(key, String(value));
    }
  }
  const qs = search.toString();
  return API_BASE + url + (qs ? `?${qs}` : '');
}

function clearSession() {
  localStorage.removeItem(LS_KEYS.token);
  localStorage.removeItem(LS_KEYS.user);
}

export async function http(url, { method = 'GET', params, body } = {}) {
  const headers = { Accept: 'application/json' };
  const token = localStorage.getItem(LS_KEYS.token);
  if (token) headers.Authorization = `Bearer ${token}`;

  let payload;
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json; charset=utf-8';
    payload = JSON.stringify(body);
  }

  let response;
  try {
    response = await fetch(buildUrl(url, params), { method, headers, body: payload });
  } catch {
    toast.error('网络请求失败，请检查服务是否可达');
    return { ok: false, status: 0, data: null, message: '网络请求失败' };
  }

  const text = await response.text();
  const contentType = response.headers.get('content-type') || '';
  let data = null;
  let parsedJson = true;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    parsedJson = false;
    data = text;
  }

  if (response.status === 401) {
    clearSession();
    if (!window.location.hash.startsWith('#/login')) {
      window.location.hash = '#/login';
    }
    toast.error('登录已失效，请重新登录');
    return { ok: false, status: 401, data, message: '未授权' };
  }

  if (!response.ok) {
    const message = (data && (data.message || data.Message)) || `请求失败（${response.status}）`;
    toast.error(message);
    return { ok: false, status: response.status, data, message };
  }

  // 2xx 也必须是 JSON；未知 API 被旧服务的 SPA fallback 接管时，不能把 index.html 当业务数据。
  if (!parsedJson || !contentType.toLowerCase().includes('application/json')) {
    const message = '服务器返回了非 JSON 响应，请确认服务版本与 API 地址一致';
    toast.error(message);
    return { ok: false, status: response.status, data: null, message };
  }

  return { ok: true, status: response.status, data };
}

/// 上传文件（multipart/form-data）。路径经 params 传入；文件走 form，不在 headers。返回与 http 一致的 { ok, data, status, message }。
export async function httpUpload(url, { params, file } = {}) {
  const headers = { Accept: 'application/json' };
  const token = localStorage.getItem(LS_KEYS.token);
  if (token) headers.Authorization = `Bearer ${token}`;

  const form = new FormData();
  form.append('file', file);

  let response;
  try {
    response = await fetch(buildUrl(url, params), { method: 'POST', headers, body: form });
  } catch {
    toast.error('网络请求失败，请检查服务是否可达');
    return { ok: false, status: 0, data: null, message: '网络请求失败' };
  }

  let data = null;
  try {
    data = await response.json();
  } catch {
    data = null;
  }

  if (response.status === 401) {
    clearSession();
    if (!window.location.hash.startsWith('#/login')) {
      window.location.hash = '#/login';
    }
    toast.error('登录已失效，请重新登录');
    return { ok: false, status: 401, data, message: '未授权' };
  }

  if (!response.ok) {
    const message = (data && (data.message || data.Message)) || `上传失败（${response.status}）`;
    toast.error(message);
    return { ok: false, status: response.status, data, message };
  }
  return { ok: true, status: response.status, data };
}
