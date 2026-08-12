import { AxiosError, AxiosHeaders } from 'axios';
import { describe, expect, it } from 'vitest';
import { getApiErrorMessage, getAuthenticationErrorMessage } from './errors';

const createApiError = ({
  status,
  data,
  responseHeaders,
}: {
  status: number;
  data: Record<string, unknown>;
  responseHeaders?: unknown;
}) => new AxiosError(
  'request failed',
  'ERR_BAD_REQUEST',
  undefined,
  undefined,
  {
    data,
    status,
    statusText: status === 429 ? 'Too Many Requests' : 'Unauthorized',
    headers: (responseHeaders === undefined
      ? new AxiosHeaders()
      : responseHeaders) as AxiosHeaders,
    config: { headers: new AxiosHeaders() },
  },
);

const createRateLimitError = (retryAfter?: string) => createApiError({
  status: 429,
  data: {
    code: 'authentication_rate_limited',
    message: 'Do not display ada@example.test or submitted credentials',
  },
  responseHeaders: retryAfter === undefined
    ? new AxiosHeaders()
    : new AxiosHeaders({ 'Retry-After': retryAfter }),
});

const createRateLimitErrorWithHeaders = (responseHeaders: unknown) => createApiError({
  status: 429,
  data: {
    code: 'authentication_rate_limited',
    message: 'Do not display ada@example.test or submitted credentials',
  },
  responseHeaders,
});

describe('getApiErrorMessage', () => {
  it('returns a non-empty API message', () => {
    const error = new AxiosError(
      'request failed',
      'ERR_BAD_REQUEST',
      undefined,
      undefined,
      {
        data: { message: 'Project name already exists' },
        status: 409,
        statusText: 'Conflict',
        headers: {},
        config: { headers: new AxiosHeaders() },
      },
    );

    expect(getApiErrorMessage(error, 'fallback')).toBe('Project name already exists');
  });

  it.each([
    new Error('not an Axios error'),
    new AxiosError('network failure'),
    new AxiosError(
      'request failed',
      'ERR_BAD_REQUEST',
      undefined,
      undefined,
      {
        data: { message: '   ' },
        status: 400,
        statusText: 'Bad Request',
        headers: {},
        config: { headers: new AxiosHeaders() },
      },
    ),
  ])('returns the fallback when no usable API message exists', (error) => {
    expect(getApiErrorMessage(error, 'fallback')).toBe('fallback');
  });
});

describe('getAuthenticationErrorMessage', () => {
  it.each([
    ['login' as const, '60', 'Too many sign-in attempts. Please try again in 60 seconds.'],
    ['registration' as const, '1', 'Too many registration attempts. Please try again in 1 second.'],
  ])('uses a valid integer Retry-After for %s guidance', (operation, retryAfter, expected) => {
    expect(getAuthenticationErrorMessage(
      createRateLimitError(retryAfter),
      operation,
      'fallback',
    )).toBe(expected);
  });

  it('clamps an excessive but safe Retry-After value', () => {
    expect(getAuthenticationErrorMessage(
      createRateLimitError('86400'),
      'login',
      'fallback',
    )).toBe('Too many sign-in attempts. Please try again in 3600 seconds.');
  });

  it('reads Retry-After case-insensitively from a plain header object', () => {
    expect(getAuthenticationErrorMessage(
      createRateLimitErrorWithHeaders({ 'rEtRy-AfTeR': '7' }),
      'login',
      'fallback',
    )).toBe('Too many sign-in attempts. Please try again in 7 seconds.');
  });

  it.each([
    null,
    42,
    { 'retry-after': 7 },
  ])('uses generic guidance for unsupported response headers %s', (responseHeaders) => {
    expect(getAuthenticationErrorMessage(
      createRateLimitErrorWithHeaders(responseHeaders),
      'login',
      'fallback',
    )).toBe('Too many sign-in attempts. Please wait a moment before trying again.');
  });

  it.each([
    undefined,
    '',
    '0',
    '-1',
    '1.5',
    '1e3',
    'Wed, 21 Oct 2015 07:28:00 GMT',
    '9007199254740992',
  ])('uses bounded generic guidance for missing or malformed Retry-After %s', (retryAfter) => {
    const message = getAuthenticationErrorMessage(
      createRateLimitError(retryAfter),
      'registration',
      'fallback',
    );

    expect(message).toBe(
      'Too many registration attempts. Please wait a moment before trying again.',
    );
    expect(message).not.toContain('ada@example.test');
    expect(message).not.toContain('credentials');
  });

  it('preserves the generic unauthorized response outside the rate-limit contract', () => {
    const unauthorized = createApiError({
      status: 401,
      data: { message: 'Invalid email or password' },
    });

    expect(getAuthenticationErrorMessage(
      unauthorized,
      'login',
      'Login failed. Please check your credentials.',
    )).toBe('Invalid email or password');
  });
});
