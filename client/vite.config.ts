import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

// Match the Docker web app by default so Vite development uses the persisted
// queue API rather than silently starting against a separate local database.
const apiProxyTarget = process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:6767'
const configuredBasePath = process.env.VITE_BASE_PATH ?? '/'
const basePath = configuredBasePath === '/'
  ? '/'
  : `/${configuredBasePath.replace(/^\/+|\/+$/g, '')}/`

// https://vite.dev/config/
export default defineConfig({
  // Tailscale Serve can mount the app below a path (for example, /codex).
  // Build the public URLs with that prefix so scripts, styles, images, and API
  // calls continue to reach the same mounted service.
  base: basePath,
  plugins: [react(), tailwindcss()],
  build: {
    sourcemap: false,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('/node_modules/react/') || id.includes('/node_modules/react-dom/')) {
            return 'react'
          }
          if (id.includes('/node_modules/lucide-react/')) {
            return 'icons'
          }
          return undefined
        },
      },
    },
  },
  server: {
    proxy: {
      '/api': apiProxyTarget,
    },
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
})
