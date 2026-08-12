import type { AuthResponse } from '../api/auth';
import type { User } from './auth-context';

const tokenKey = 'auth_token';
const userKey = 'user';

export interface StoredAuth {
  token: string | null;
  user: User | null;
}

const isUser = (value: unknown): value is User => {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false;
  }

  const candidate = value as Record<string, unknown>;
  return typeof candidate.id === 'string'
    && typeof candidate.name === 'string'
    && typeof candidate.email === 'string';
};

export const clearStoredAuth = (storage: Storage = localStorage): void => {
  storage.removeItem(tokenKey);
  storage.removeItem(userKey);
};

export const readStoredAuth = (storage: Storage = localStorage): StoredAuth => {
  const token = storage.getItem(tokenKey);
  const storedUser = storage.getItem(userKey);

  if (!token || !storedUser) {
    clearStoredAuth(storage);
    return { token: null, user: null };
  }

  try {
    const user: unknown = JSON.parse(storedUser);
    if (!isUser(user)) {
      clearStoredAuth(storage);
      return { token: null, user: null };
    }

    return {
      token,
      user,
    };
  } catch {
    clearStoredAuth(storage);
    return { token: null, user: null };
  }
};

export const writeStoredAuth = (
  response: AuthResponse,
  storage: Storage = localStorage,
): void => {
  storage.setItem(tokenKey, response.token);
  storage.setItem(userKey, JSON.stringify(response.user));
};
