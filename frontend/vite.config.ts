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
  }
})
