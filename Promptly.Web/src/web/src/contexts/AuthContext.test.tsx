import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { authApi } from '../api/auth';
import { AuthProvider } from './AuthContext';
import { useAuth } from './useAuth';

vi.mock('../api/auth', () => ({
  authApi: {
    login: vi.fn(),
    register: vi.fn(),
  },
}));

const wrapper = ({ children }: { children: ReactNode }) => (
  <AuthProvider>{children}</AuthProvider>
);

describe('AuthProvider', () => {
  beforeEach(() => {
    vi.mocked(authApi.login).mockReset();
    vi.mocked(authApi.register).mockReset();
  });

  it('starts with restored authentication and logs out cleanly', () => {
    localStorage.setItem('auth_token', 'stored-token');
    localStorage.setItem('user', JSON.stringify({ id: 'u1', name: 'Ada', email: 'ada@example.test' }));

    const { result } = renderHook(() => useAuth(), { wrapper });

    expect(result.current.isAuthenticated).toBe(true);
    expect(result.current.user?.name).toBe('Ada');
    expect(result.current.isLoading).toBe(false);

    act(() => result.current.logout());
    expect(result.current.isAuthenticated).toBe(false);
    expect(result.current.user).toBeNull();
    expect(localStorage.length).toBe(0);
  });

  it('stores a successful login response', async () => {
    vi.mocked(authApi.login).mockResolvedValue({
      token: 'login-token',
      user: { id: 'u2', name: 'Grace', email: 'grace@example.test' },
    });
    const { result } = renderHook(() => useAuth(), { wrapper });

    await act(() => result.current.login('grace@example.test', 'password'));

    await waitFor(() => expect(result.current.isAuthenticated).toBe(true));
    expect(result.current.user?.name).toBe('Grace');
    expect(localStorage.getItem('auth_token')).toBe('login-token');
  });

  it('stores a successful registration response', async () => {
    vi.mocked(authApi.register).mockResolvedValue({
      token: 'register-token',
      user: { id: 'u3', name: 'Katherine', email: 'katherine@example.test' },
    });
    const { result } = renderHook(() => useAuth(), { wrapper });

    await act(() => result.current.register('Katherine', 'katherine@example.test', 'password'));

    expect(result.current.user?.name).toBe('Katherine');
    expect(localStorage.getItem('auth_token')).toBe('register-token');
  });

  it('requires the hook to be rendered inside its provider', () => {
    expect(() => renderHook(() => useAuth())).toThrow(
      'useAuth must be used within an AuthProvider',
    );
  });
});
