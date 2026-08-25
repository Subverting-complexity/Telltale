/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // The port the shipped build listens on. Development and the shipped
      // build disagreed here for a long time: nothing published ever listened
      // on 5111, so this proxy pointed at a server that did not exist outside a
      // debugger.
      '/api': 'http://127.0.0.1:41821'
    }
  },
  build: {
    outDir: '../viewer/wwwroot',
    emptyOutDir: true
  },
  test: {
    // Components need somewhere to render. Without a DOM, vitest runs in plain
    // Node and anything that touches document throws, which is why the only
    // tests here until now were of pure helper functions. jsdom is the heavier
    // of the two usual choices and the more widely compatible one; happy-dom is
    // the swap if the install or run time ever becomes the problem.
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    include: ['src/**/*.test.{ts,tsx}']
  }
})
