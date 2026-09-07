import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AxiosError, AxiosHeaders } from 'axios';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useAuth } from '../contexts/useAuth';
import { deferred } from '../test/render-route';
import { Register } from './Register';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async (importOriginal) => ({
  ...await importOriginal<typeof import('react-router-dom')>(),
  useNavigate: () => navigate,
}));

vi.mock('../contexts/useAuth', () => ({ useAuth: vi.fn() }));

const renderRegister = () => render(
  <MemoryRouter>
    <Register />
  </MemoryRouter>,
);

const fillRegistration = (password: string, confirmation = password) => {
  fireEvent.change(screen.getByLabelText(/Full Name/), { target: { value: 'Ada Lovelace' } });
  fireEvent.change(screen.getByLabelText(/Email Address/), { target: { value: 'ada@example.test' } });
  fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: password } });
  fireEvent.change(screen.getByLabelText(/Confirm Password/), { target: { value: confirmation } });
};

const createRegistrationRateLimit = (retryAfter?: string) => new AxiosError(
  'request failed',
  'ERR_BAD_REQUEST',
  undefined,
  undefined,
  {
    data: {
      code: 'authentication_rate_limited',
      message: 'Rejected registration for Ada Lovelace at ada@example.test',
    },
    status: 429,
    statusText: 'Too Many Requests',
    headers: retryAfter === undefined
      ? new AxiosHeaders()
      : new AxiosHeaders({ 'Retry-After': retryAfter }),
    config: { headers: new AxiosHeaders() },
  },
);

describe('Register', () => {
  const register = vi.fn();

  beforeEach(() => {
    navigate.mockReset();
    register.mockReset();
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      token: null,
      login: vi.fn(),
      register,
      logout: vi.fn(),
      isAuthenticated: false,
      isLoading: false,
    });
  });

  it('publishes the server-compatible authentication input bounds', () => {
    renderRegister();

    expect(screen.getByLabelText(/Full Name/)).toHaveAttribute('maxlength', '256');
    expect(screen.getByLabelText(/Email Address/)).toHaveAttribute('maxlength', '256');
    expect(screen.getByLabelText(/^Password/)).toHaveAttribute('maxlength', '128');
    expect(screen.getByLabelText(/Confirm Password/)).toHaveAttribute('maxlength', '128');
  });

  it('rejects mismatched and short passwords without calling the API', async () => {
    const first = renderRegister();
    fillRegistration('long-enough', 'different');
    fireEvent.click(screen.getByRole('button', { name: 'Sign Up' }));
    expect(await screen.findByText('Passwords do not match')).toBeInTheDocument();
    expect(register).not.toHaveBeenCalled();

    first.unmount();
    renderRegister();
    fillRegistration('short');
    fireEvent.click(screen.getByRole('button', { name: 'Sign Up' }));
    expect(await screen.findByText('Password must be at least 8 characters long')).toBeInTheDocument();
    expect(register).not.toHaveBeenCalled();
  });

  it('submits valid registration data and navigates', async () => {
    register.mockResolvedValue(undefined);
    renderRegister();
    fillRegistration('valid-password');

    fireEvent.click(screen.getByRole('button', { name: 'Sign Up' }));

    await waitFor(() => expect(register).toHaveBeenCalledWith(
      'Ada Lovelace',
      'ada@example.test',
      'valid-password',
    ));
    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('shows pending state and recovers from an API failure', async () => {
    const pending = deferred<void>();
    register.mockReturnValue(pending.promise);
    renderRegister();
    fillRegistration('valid-password');
    fireEvent.click(screen.getByRole('button', { name: 'Sign Up' }));
    expect(await screen.findByRole('button', { name: 'Creating account...' })).toBeDisabled();

    pending.reject(new Error('offline'));
    expect(await screen.findByText('Registration failed. Please try again.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign Up' })).toBeEnabled();
  });

  it('shows bounded retry guidance for the registration rate-limit contract', async () => {
    register.mockRejectedValue(createRegistrationRateLimit('120'));
    renderRegister();
    fillRegistration('valid-password');

    fireEvent.click(screen.getByRole('button', { name: 'Sign Up' }));

    expect(await screen.findByText(
      'Too many registration attempts. Please try again in 120 seconds.',
    )).toBeInTheDocument();
    expect(screen.queryByText(/Rejected registration for/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign Up' })).toBeEnabled();
  });

  it('uses safe retry guidance when Retry-After is missing', async () => {
    register.mockRejectedValue(createRegistrationRateLimit());
    renderRegister();
    fillRegistration('valid-password');

    fireEvent.click(screen.getByRole('button', { name: 'Sign Up' }));

    expect(await screen.findByText(
      'Too many registration attempts. Please wait a moment before trying again.',
    )).toBeInTheDocument();
  });

  it('redirects an authenticated visitor and links back to login', () => {
    vi.mocked(useAuth).mockReturnValue({
      user: { id: 'u1', name: 'Ada', email: 'ada@example.test' },
      token: 'token',
      login: vi.fn(),
      register,
      logout: vi.fn(),
      isAuthenticated: true,
      isLoading: false,
    });
    renderRegister();

    expect(navigate).toHaveBeenCalledWith('/');
    expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login');
  });
});
