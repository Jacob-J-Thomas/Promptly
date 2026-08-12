import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AxiosError, AxiosHeaders } from 'axios';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useAuth } from '../contexts/useAuth';
import { deferred } from '../test/render-route';
import { Login } from './Login';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async (importOriginal) => ({
  ...await importOriginal<typeof import('react-router-dom')>(),
  useNavigate: () => navigate,
}));

vi.mock('../contexts/useAuth', () => ({ useAuth: vi.fn() }));

const renderLogin = () => render(
  <MemoryRouter>
    <Login />
  </MemoryRouter>,
);

const createLoginFailure = ({
  status,
  data,
  retryAfter,
}: {
  status: number;
  data: Record<string, unknown>;
  retryAfter?: string;
}) => new AxiosError(
  'request failed',
  'ERR_BAD_REQUEST',
  undefined,
  undefined,
  {
    data,
    status,
    statusText: status === 429 ? 'Too Many Requests' : 'Unauthorized',
    headers: retryAfter === undefined
      ? new AxiosHeaders()
      : new AxiosHeaders({ 'Retry-After': retryAfter }),
    config: { headers: new AxiosHeaders() },
  },
);

const submitLogin = () => {
  fireEvent.change(screen.getByLabelText(/Email Address/), {
    target: { value: 'ada@example.test' },
  });
  fireEvent.change(screen.getByLabelText(/Password/), {
    target: { value: 'wrong' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Sign In' }));
};

describe('Login', () => {
  const login = vi.fn();

  beforeEach(() => {
    navigate.mockReset();
    login.mockReset();
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      token: null,
      login,
      register: vi.fn(),
      logout: vi.fn(),
      isAuthenticated: false,
      isLoading: false,
    });
  });

  it('bounds email while allowing login with legacy long passwords', () => {
    renderLogin();

    expect(screen.getByLabelText(/Email Address/)).toHaveAttribute('maxlength', '256');
    expect(screen.getByLabelText(/Password/)).not.toHaveAttribute('maxlength');
  });

  it('submits entered credentials and navigates after a successful login', async () => {
    login.mockResolvedValue(undefined);
    renderLogin();

    fireEvent.change(screen.getByLabelText(/Email Address/), {
      target: { value: 'ada@example.test' },
    });
    fireEvent.change(screen.getByLabelText(/Password/), {
      target: { value: 'correct horse battery staple' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Sign In' }));

    await waitFor(() => expect(login).toHaveBeenCalledWith(
      'ada@example.test',
      'correct horse battery staple',
    ));
    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('disables the submit button while login is pending', async () => {
    const pending = deferred<void>();
    login.mockReturnValue(pending.promise);
    renderLogin();

    fireEvent.change(screen.getByLabelText(/Email Address/), { target: { value: 'ada@example.test' } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign In' }));

    expect(await screen.findByRole('button', { name: 'Signing in...' })).toBeDisabled();
    pending.resolve();
    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/'));
  });

  it('shows a stable error when authentication fails', async () => {
    login.mockRejectedValue(new Error('offline'));
    renderLogin();

    fireEvent.change(screen.getByLabelText(/Email Address/), { target: { value: 'ada@example.test' } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign In' }));

    expect(await screen.findByText('Login failed. Please check your credentials.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign In' })).toBeEnabled();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('shows bounded retry guidance for the authentication rate-limit contract', async () => {
    login.mockRejectedValue(createLoginFailure({
      status: 429,
      data: {
        code: 'authentication_rate_limited',
        message: 'Rejected login for ada@example.test using wrong',
      },
      retryAfter: '45',
    }));
    renderLogin();

    submitLogin();

    expect(await screen.findByText(
      'Too many sign-in attempts. Please try again in 45 seconds.',
    )).toBeInTheDocument();
    expect(screen.queryByText(/Rejected login for/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign In' })).toBeEnabled();
  });

  it('does not trust a malformed authentication Retry-After header', async () => {
    login.mockRejectedValue(createLoginFailure({
      status: 429,
      data: { code: 'authentication_rate_limited' },
      retryAfter: '1e9',
    }));
    renderLogin();

    submitLogin();

    expect(await screen.findByText(
      'Too many sign-in attempts. Please wait a moment before trying again.',
    )).toBeInTheDocument();
  });

  it('preserves the generic unauthorized login response', async () => {
    login.mockRejectedValue(createLoginFailure({
      status: 401,
      data: { message: 'Invalid email or password' },
    }));
    renderLogin();

    submitLogin();

    expect(await screen.findByText('Invalid email or password')).toBeInTheDocument();
  });

  it('redirects an already-authenticated visitor', () => {
    vi.mocked(useAuth).mockReturnValue({
      user: { id: 'u1', name: 'Ada', email: 'ada@example.test' },
      token: 'token',
      login,
      register: vi.fn(),
      logout: vi.fn(),
      isAuthenticated: true,
      isLoading: false,
    });

    renderLogin();

    expect(navigate).toHaveBeenCalledWith('/');
    expect(screen.getByRole('link', { name: 'Sign up' })).toHaveAttribute('href', '/register');
  });
});
