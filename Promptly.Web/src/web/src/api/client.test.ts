import {
  AxiosError,
  AxiosHeaders,
  type AxiosAdapter,
  type AxiosResponse,
  type InternalAxiosRequestConfig,
} from 'axios';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient, resolveApiBaseUrl } from './client';

const originalAdapter = apiClient.defaults.adapter;

const response = <T>(
  config: InternalAxiosRequestConfig,
  data: T,
  status = 200,
): AxiosResponse<T> => ({
  config,
  data,
  headers: new AxiosHeaders(),
  status,
  statusText: status === 200 ? 'OK' : 'Error',
});

const resolvingAdapter = <T>(data: T): ReturnType<typeof vi.fn<AxiosAdapter>> => vi.fn(
  async (config: InternalAxiosRequestConfig) => response(config, data),
);

const rejectingAdapter = (
  status: number,
  message: string,
): { adapter: ReturnType<typeof vi.fn<AxiosAdapter>>; error: AxiosError } => {
  const error = new AxiosError(message, `HTTP_${status}`);
  const adapter = vi.fn<AxiosAdapter>(async (config) => {
    const failedResponse = response(config, { message }, status);
    error.config = config;
    error.response = failedResponse;
    error.status = status;
    throw error;
  });

  return { adapter, error };
};

afterEach(() => {
  apiClient.defaults.adapter = originalAdapter;
  vi.unstubAllGlobals();
});

describe('apiClient', () => {
  it('uses the default API base URL and JSON content type', () => {
    expect(apiClient.defaults.baseURL).toBe('http://localhost:5000/api');
    expect(apiClient.defaults.headers['Content-Type']).toBe('application/json');
  });

  it.each([
    [undefined, false, 'http://localhost:5000/api'],
    [undefined, true, '/api'],
    ['   ', true, '/api'],
    ['https://promptly.example/', true, 'https://promptly.example/api'],
  ])(
    'resolves configured and environment-specific API roots',
    (configured, production, expected) => {
      expect(resolveApiBaseUrl(configured, production)).toBe(expected);
    },
  );

  it('adds the stored bearer token before dispatching a request', async () => {
    localStorage.setItem('auth_token', 'token-1');
    const adapter = resolvingAdapter({ ok: true });
    apiClient.defaults.adapter = adapter;

    await expect(apiClient.get('/secured')).resolves.toMatchObject({ data: { ok: true } });

    const dispatchedConfig = adapter.mock.calls[0][0];
    expect(dispatchedConfig.url).toBe('/secured');
    expect(dispatchedConfig.headers.get('Authorization')).toBe('Bearer token-1');
  });

  it('does not add authorization when no token is stored', async () => {
    const adapter = resolvingAdapter({ ok: true });
    apiClient.defaults.adapter = adapter;

    await apiClient.get('/public');

    expect(adapter.mock.calls[0][0].headers.has('Authorization')).toBe(false);
  });

  it('propagates request-interceptor failures without dispatching', async () => {
    const adapter = resolvingAdapter({ ok: true });
    const requestFailure = new Error('request setup failed');
    apiClient.defaults.adapter = adapter;
    const interceptorId = apiClient.interceptors.request.use(() => Promise.reject(requestFailure));

    try {
      await expect(apiClient.get('/never-dispatched')).rejects.toBe(requestFailure);
      expect(adapter).not.toHaveBeenCalled();
    } finally {
      apiClient.interceptors.request.eject(interceptorId);
    }
  });

  it('clears stored authentication and redirects after a 401 response', async () => {
    localStorage.setItem('auth_token', 'expired-token');
    localStorage.setItem('user', '{"id":"user-1"}');
    const fakeWindow = { location: { href: 'http://localhost/secured' } };
    vi.stubGlobal('window', fakeWindow);
    const { adapter, error } = rejectingAdapter(401, 'Unauthorized');
    apiClient.defaults.adapter = adapter;

    await expect(apiClient.get('/secured')).rejects.toBe(error);

    expect(localStorage.getItem('auth_token')).toBeNull();
    expect(localStorage.getItem('user')).toBeNull();
    expect(fakeWindow.location.href).toBe('/login');
  });

  it('preserves authentication and location for non-401 failures', async () => {
    localStorage.setItem('auth_token', 'valid-token');
    localStorage.setItem('user', '{"id":"user-1"}');
    const fakeWindow = { location: { href: 'http://localhost/secured' } };
    vi.stubGlobal('window', fakeWindow);
    const { adapter, error } = rejectingAdapter(500, 'Server error');
    apiClient.defaults.adapter = adapter;

    await expect(apiClient.get('/secured')).rejects.toBe(error);

    expect(localStorage.getItem('auth_token')).toBe('valid-token');
    expect(localStorage.getItem('user')).toBe('{"id":"user-1"}');
    expect(fakeWindow.location.href).toBe('http://localhost/secured');
  });
});
