import axios from 'axios';

interface ApiErrorBody {
  message?: unknown;
  code?: unknown;
}

type AuthenticationOperation = 'login' | 'registration';

const AUTHENTICATION_RATE_LIMIT_CODE = 'authentication_rate_limited';
const MAX_AUTHENTICATION_RETRY_AFTER_SECONDS = 3_600;

const getHeaderValue = (headers: unknown, name: string): unknown => {
  if (typeof headers !== 'object' || headers === null) {
    return undefined;
  }

  const axiosHeaders = headers as { get?: (headerName: string) => unknown };
  if (typeof axiosHeaders.get === 'function') {
    return axiosHeaders.get(name);
  }

  const normalizedName = name.toLowerCase();
  return Object.entries(headers).find(([headerName]) => (
    headerName.toLowerCase() === normalizedName
  ))?.[1];
};

const getRetryAfterSeconds = (headers: unknown): number | null => {
  const retryAfter = getHeaderValue(headers, 'retry-after');
  if (typeof retryAfter !== 'string') {
    return null;
  }

  const normalizedRetryAfter = retryAfter.trim();
  if (!/^[1-9]\d*$/.test(normalizedRetryAfter)) {
    return null;
  }

  const seconds = Number(normalizedRetryAfter);
  if (!Number.isSafeInteger(seconds)) {
    return null;
  }

  return Math.min(seconds, MAX_AUTHENTICATION_RETRY_AFTER_SECONDS);
};

const getRateLimitSubject = (operation: AuthenticationOperation): string => (
  operation === 'login' ? 'sign-in attempts' : 'registration attempts'
);

const formatSeconds = (seconds: number): string => (
  `${seconds} ${seconds === 1 ? 'second' : 'seconds'}`
);

export const getApiErrorMessage = (error: unknown, fallback: string): string => {
  if (axios.isAxiosError<ApiErrorBody>(error)) {
    const message = error.response?.data?.message;
    if (typeof message === 'string' && message.trim().length > 0) {
      return message;
    }
  }

  return fallback;
};

export const getAuthenticationErrorMessage = (
  error: unknown,
  operation: AuthenticationOperation,
  fallback: string,
): string => {
  if (axios.isAxiosError<ApiErrorBody>(error)
    && error.response?.status === 429
    && error.response.data?.code === AUTHENTICATION_RATE_LIMIT_CODE) {
    const subject = getRateLimitSubject(operation);
    const retryAfterSeconds = getRetryAfterSeconds(error.response.headers);

    if (retryAfterSeconds !== null) {
      return `Too many ${subject}. Please try again in ${formatSeconds(retryAfterSeconds)}.`;
    }

    return `Too many ${subject}. Please wait a moment before trying again.`;
  }

  return getApiErrorMessage(error, fallback);
};
