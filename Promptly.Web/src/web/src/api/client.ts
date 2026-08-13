import axios from 'axios';

export const resolveApiBaseUrl = (
  configuredBaseUrl: string | undefined,
  production: boolean,
): string => {
  const normalizedBaseUrl = configuredBaseUrl?.trim().replace(/\/+$/, '');
  if (normalizedBaseUrl) {
    return `${normalizedBaseUrl}/api`;
  }

  return production ? '/api' : 'http://localhost:5000/api';
};

export const apiClient = axios.create({
  baseURL: resolveApiBaseUrl(import.meta.env.VITE_API_BASE_URL, import.meta.env.PROD),
  headers: {
    'Content-Type': 'application/json',
  },
});

// Request interceptor to add JWT token
apiClient.interceptors.request.use(
  (config) => {
    const token = localStorage.getItem('auth_token');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => {
    return Promise.reject(error);
  }
);

// Response interceptor to handle auth errors
apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      // Clear auth token and redirect to login
      localStorage.removeItem('auth_token');
      localStorage.removeItem('user');
      window.location.href = '/login';
    }
    return Promise.reject(error);
  }
);
