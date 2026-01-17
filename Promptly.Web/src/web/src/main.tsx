import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

console.log('Main.tsx: Starting application...');

const rootElement = document.getElementById('root');
console.log('Main.tsx: Root element found:', rootElement);

if (rootElement) {
  createRoot(rootElement).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
  console.log('Main.tsx: App rendered');
} else {
  console.error('Main.tsx: Root element not found!');
}
