import React from 'react';
import { ThemeProvider } from '@mui/material/styles';
import CssBaseline from '@mui/material/CssBaseline';
import { AuthProvider } from './contexts/AuthContext';
import { Router } from './Router';
import { theme } from './theme';

console.log('App.tsx: Loading App component');

const App: React.FC = () => {
  console.log('App.tsx: Rendering App component');

  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <AuthProvider>
        <Router />
      </AuthProvider>
    </ThemeProvider>
  );
};

console.log('App.tsx: App component defined');

export default App;
