import { describe, expect, it } from 'vitest';
import { clearStoredAuth, readStoredAuth, writeStoredAuth } from './auth-storage';

describe('auth storage', () => {
  it('restores a paired token and user', () => {
    localStorage.setItem('auth_token', 'token-1');
    localStorage.setItem('user', JSON.stringify({ id: 'u1', name: 'Ada', email: 'ada@example.test' }));

    expect(readStoredAuth()).toEqual({
      token: 'token-1',
      user: { id: 'u1', name: 'Ada', email: 'ada@example.test' },
    });
  });

  it.each([
    ['auth_token', 'token-only'],
    ['user', JSON.stringify({ id: 'u1', name: 'Ada', email: 'ada@example.test' })],
  ])('clears partial state when only %s exists', (key, value) => {
    localStorage.setItem(key, value);

    expect(readStoredAuth()).toEqual({ token: null, user: null });
    expect(localStorage.length).toBe(0);
  });

  it('clears malformed user JSON', () => {
    localStorage.setItem('auth_token', 'token-1');
    localStorage.setItem('user', '{not-json');

    expect(readStoredAuth()).toEqual({ token: null, user: null });
    expect(localStorage.length).toBe(0);
  });

  it.each([
    null,
    [],
    {},
    { id: 'u1', name: 'Ada' },
    { id: 1, name: 'Ada', email: 'ada@example.test' },
    { id: 'u1', name: null, email: 'ada@example.test' },
    { id: 'u1', name: 'Ada', email: false },
  ])('clears a token paired with an invalid parsed user shape: %j', (user) => {
    localStorage.setItem('auth_token', 'token-1');
    localStorage.setItem('user', JSON.stringify(user));

    expect(readStoredAuth()).toEqual({ token: null, user: null });
    expect(localStorage.length).toBe(0);
  });

  it('writes and clears an auth response', () => {
    const response = {
      token: 'token-1',
      user: { id: 'u1', name: 'Ada', email: 'ada@example.test' },
    };

    writeStoredAuth(response);
    expect(readStoredAuth()).toEqual(response);

    clearStoredAuth();
    expect(readStoredAuth()).toEqual({ token: null, user: null });
  });
});
