import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import App from './App';

vi.mock('./contexts/AuthContext', () => ({
  AuthProvider: ({ children }: { children: React.ReactNode }) => (
    <section data-testid="auth-provider">{children}</section>
  ),
}));

vi.mock('./Router', () => ({ Router: () => <div>Routed application</div> }));

describe('App', () => {
  it('composes the theme, authentication provider, and router', () => {
    render(<App />);

    expect(screen.getByTestId('auth-provider')).toContainElement(
      screen.getByText('Routed application'),
    );
  });
});
