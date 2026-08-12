import { AxiosError, AxiosHeaders } from 'axios';
import { describe, expect, it } from 'vitest';
import { getApiErrorMessage } from './errors';

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
