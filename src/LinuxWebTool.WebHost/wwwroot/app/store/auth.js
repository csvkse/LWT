import { reactive } from 'vue';
import { http } from '../api/client.js';
import { API, LS_KEYS } from '../config.js';

export const auth = reactive({
  token: localStorage.getItem(LS_KEYS.token) || '',
  userName: localStorage.getItem(LS_KEYS.user) || '',
});

export function isLoggedIn() {
  return Boolean(auth.token);
}

export async function login(userName, password) {
  const result = await http(API.auth.login, { method: 'POST', body: { userName, password } });
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
