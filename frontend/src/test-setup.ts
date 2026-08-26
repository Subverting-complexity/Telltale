// Loaded before every test file. It adds jest-dom's matchers to vitest's
// expect, which is what lets a test say toBeInTheDocument or toHaveTextContent
// instead of reaching into the DOM by hand, and it clears the rendered tree
// between tests so one test cannot see what another left behind.
import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => {
  cleanup();
  // The window records the theme and the view it was left on, so without this a
  // test that changed either would hand its state to the next one in the file
  // and the next one would open on a view it never asked for.
  //
  // Guarded because a test file can opt out of jsdom into a plain node
  // environment, where there is no store and this would throw after every test
  // in that file rather than only where it had something to clear.
  if (typeof localStorage !== 'undefined') localStorage.clear();
});

// jsdom does not implement matchMedia, and uPlot calls it while its module is
// still being evaluated, to work out the device pixel ratio. That makes it a
// load-time failure rather than a render-time one: any test whose imports reach
// a chart component dies before a single test runs, and no per-test setup can
// get in front of it. Hence here, in the file that runs before the test files.
if (typeof window !== 'undefined' && !window.matchMedia) {
  window.matchMedia = (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }) as MediaQueryList;
}
