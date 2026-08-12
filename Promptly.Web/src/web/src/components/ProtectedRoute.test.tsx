import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AuthContext } from '../contexts/auth-context';
import type { AuthContextValue } from '../contexts/auth-context';
import { ProtectedRoute } from './ProtectedRoute';

const contextValue = (overrides: Partial<AuthContextValue>): AuthContextValue => ({
  user: null,
  token: null,
  login: async () => undefined,
  register: async () => undefined,
  logout: () => undefined,
  isAuthenticated: false,
  isLoading: false,
  ...overrides,
});

const renderRoute = (value: AuthContextValue) => render(
  <AuthContext.Provider value={value}>
    <MemoryRouter initialEntries={['/private']}>
      <Routes>
        <Route path="/login" element={<div>Login page</div>} />
        <Route
          path="/private"
          element={(
            <ProtectedRoute>
              <div>Private content</div>
            </ProtectedRoute>
          )}
        />
      </Routes>
    </MemoryRouter>
  </AuthContext.Provider>,
);

describe('ProtectedRoute', () => {
  it('shows progress while authentication is loading', () => {
    renderRoute(contextValue({ isLoading: true }));

    expect(screen.getByRole('progressbar')).toBeInTheDocument();
  });

  it('redirects unauthenticated visitors to login', () => {
    renderRoute(contextValue({}));

    expect(screen.getByText('Login page')).toBeInTheDocument();
  });

  it('renders protected content for authenticated visitors', () => {
    renderRoute(contextValue({ isAuthenticated: true, token: 'token-1' }));

    expect(screen.getByText('Private content')).toBeInTheDocument();
  });
});
