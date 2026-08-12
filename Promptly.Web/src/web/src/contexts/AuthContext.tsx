import { useState } from 'react';
import type { ReactNode } from 'react';
import { authApi } from '../api/auth';
import type { AuthResponse } from '../api/auth';
import { AuthContext } from './auth-context';
import { clearStoredAuth, readStoredAuth, writeStoredAuth } from './auth-storage';

export const AuthProvider = ({ children }: { children: ReactNode }) => {
  const [auth, setAuth] = useState(readStoredAuth);

  const handleAuthResponse = (response: AuthResponse) => {
    setAuth({ token: response.token, user: response.user });
    writeStoredAuth(response);
  };

  const login = async (email: string, password: string) => {
    const response = await authApi.login({ email, password });
    handleAuthResponse(response);
  };

  const register = async (name: string, email: string, password: string) => {
    const response = await authApi.register({ name, email, password });
    handleAuthResponse(response);
  };

  const logout = () => {
    setAuth({ token: null, user: null });
    clearStoredAuth();
  };

  return (
    <AuthContext.Provider
      value={{
        user: auth.user,
        token: auth.token,
        login,
        register,
        logout,
        isAuthenticated: auth.token !== null,
        isLoading: false,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
