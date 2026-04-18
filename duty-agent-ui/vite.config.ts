import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { resolve } from 'path'
import { readFileSync, existsSync } from 'fs'

// Read token from .env.local (written by run_dev.bat at startup)
const envPath = resolve(__dirname, '.env.local')
const token = existsSync(envPath)
  ? readFileSync(envPath, 'utf-8').match(/^VITE_BACKEND_TOKEN=(.+)/m)?.[1] ?? ''
  : ''

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue(),
    tailwindcss(),
    {
      name: 'inject-dev-token',
      transformIndexHtml(html) {
        if (!token) return html
        // Inject before any other tags so it is guaranteed available before any module code runs
        return html.replace('<head>', `<head>\n<script>window.__DEV_TOKEN__=${JSON.stringify(token)};</script>`)
      },
    },
  ],
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:8765',
        changeOrigin: true,
        ws: true,
      },
      '/mcp': {
        target: 'http://127.0.0.1:8765',
        changeOrigin: true,
      },
      '/health': {
        target: 'http://127.0.0.1:8765',
        changeOrigin: true,
      },
      '/engine': {
        target: 'http://127.0.0.1:8765',
        changeOrigin: true,
      },
      '/app': {
        target: 'http://127.0.0.1:8765',
        changeOrigin: true,
      },
      '/shutdown': {
        target: 'http://127.0.0.1:8765',
        changeOrigin: true,
      },
    },
  },
})
