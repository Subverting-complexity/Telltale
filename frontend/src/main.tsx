import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { startSessionKeepalive } from './session';
import './App.css';

// Started outside React, and never stopped, because it tracks the window rather
// than anything in the tree. The single-process build serves the API only while
// there is a window open, and this is how it is told there still is one.
startSessionKeepalive();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
