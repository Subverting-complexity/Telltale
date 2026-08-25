// Loaded before every test file. It adds jest-dom's matchers to vitest's
// expect, which is what lets a test say toBeInTheDocument or toHaveTextContent
// instead of reaching into the DOM by hand, and it clears the rendered tree
// between tests so one test cannot see what another left behind.
import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => {
  cleanup();
});
