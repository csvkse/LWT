import { reactive } from 'vue';
import { http, onTokenRenewed } from '../api/client.js';
import { API, LS_KEYS } from '../config.js';

export const auth = reactive({
  token: localStorage.getItem(LS_KEYS.token) || '',
  userName: localStorage.getItem(LS_KEYS.user) || '',
});

onTokenRenewed((newToken) => {
  auth.token = newToken;
});

export function isLoggedIn() {
  return Boolean(auth.token);
}

export function getTokenPayload(token = auth.token) {
  if (!token) return null;
  try {
    const parts = token.split('.');
    if (parts.length < 2) return null;
    const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const json = decodeURIComponent(
      atob(base64)
        .split('')
        .map((c) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
        .join('')
    );
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export function getTokenRemainingSeconds(token = auth.token) {
  const payload = getTokenPayload(token);
  if (!payload || !payload.exp) return 0;
  const now = Math.floor(Date.now() / 1000);
  return Math.max(0, payload.exp - now);
}

export async function renewToken() {
  if (!auth.token) return false;
  const result = await http(API.auth.renew, { method: 'POST' });
  if (result.ok && result.data?.token) {
    auth.token = result.data.token;
    auth.userName = result.data.userName || auth.userName;
    localStorage.setItem(LS_KEYS.token, auth.token);
    localStorage.setItem(LS_KEYS.user, auth.userName);
    return true;
  }
  return false;
}

export async function login(userName, password) {
  const result = await http(API.auth.login, { method: 'POST', body: { userName, password }, timeoutMs: 15_000 });
  if (result.ok) {
    auth.token = result.data.token;
    auth.userName = result.data.userName || userName;
    localStorage.setItem(LS_KEYS.token, auth.token);
    localStorage.setItem(LS_KEYS.user, auth.userName);
  }
  return result;
}

export function logout() {
  auth.token = '';
  auth.userName = '';
  localStorage.removeItem(LS_KEYS.token);
  localStorage.removeItem(LS_KEYS.user);
}

export async function check() {
  if (!auth.token) return false;
  const result = await http(API.auth.check);
  if (!result.ok) return false;
  auth.userName = result.data.userName || auth.userName;
  return true;
}

// 默认 72 小时（与服务端 RefreshThresholdHours 一致）触发滑动续约
const RENEW_THRESHOLD_SECONDS = 72 * 3600;

function checkAndAutoRenew() {
  if (!auth.token) return;
  const remaining = getTokenRemainingSeconds();
  if (remaining > 0 && remaining <= RENEW_THRESHOLD_SECONDS) {
    renewToken();
  }
}

if (typeof window !== 'undefined') {
  window.addEventListener('focus', () => {
    checkAndAutoRenew();
  });

  setInterval(() => {
    checkAndAutoRenew();
  }, 30 * 60 * 1000);
}

