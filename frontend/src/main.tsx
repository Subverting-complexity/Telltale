import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { startSessionKeepalive } from './session';
import { installThemeTokens } from './theme';
import './App.css';

// Before React mounts, so the first paint already has the palette. The colour
// tokens live in palette.ts rather than in a stylesheet, which is what keeps
// them to a single definition; CSS still does the light/dark switching.
installThemeTokens();

// Started outside React, and never stopped, because it tracks the window rather
// than anything in the tree. The single-process build serves the API only while
// there is a window open, and this is how it is told there still is one.
startSessionKeepalive();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
