import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useAuth } from '../contexts/useAuth';
import { Layout } from './Layout';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', () => ({ useNavigate: () => navigate }));
vi.mock('../contexts/useAuth', () => ({ useAuth: vi.fn() }));

describe('Layout', () => {
  const logout = vi.fn();

  beforeEach(() => {
    navigate.mockReset();
    logout.mockReset();
  });

  it('renders its shell without account controls for an anonymous user', () => {
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      token: null,
      login: vi.fn(),
      register: vi.fn(),
      logout,
      isAuthenticated: false,
      isLoading: false,
    });

    render(<Layout><div>Page content</div></Layout>);

    expect(screen.getByText('Page content')).toBeInTheDocument();
    expect(screen.getByText('Promptly v1 - Black-box test harness for LLM systems')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'account of current user' })).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('Promptly'));
    fireEvent.click(screen.getByRole('button', { name: 'Projects' }));
    expect(navigate).toHaveBeenNthCalledWith(1, '/');
    expect(navigate).toHaveBeenNthCalledWith(2, '/');
  });

  it('opens the account menu and logs out the current user', () => {
    vi.mocked(useAuth).mockReturnValue({
      user: { id: 'u1', name: 'Ada', email: 'ada@example.test' },
      token: 'token',
      login: vi.fn(),
      register: vi.fn(),
      logout,
      isAuthenticated: true,
      isLoading: false,
    });

    render(<Layout><div>Private content</div></Layout>);

    expect(screen.getByText('Ada')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'account of current user' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Logout' }));

    expect(logout).toHaveBeenCalledOnce();
    expect(navigate).toHaveBeenCalledWith('/login');
    expect(screen.queryByRole('menuitem', { name: 'Logout' })).not.toBeInTheDocument();
  });
});
