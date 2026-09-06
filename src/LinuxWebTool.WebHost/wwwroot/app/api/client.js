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
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
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

  return { ok: true, status: response.status, data };
}
