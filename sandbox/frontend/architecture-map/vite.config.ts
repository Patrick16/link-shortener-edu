import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Fixed (not auto-picked) so it matches ControlApi's default CORS allow-list.
    port: 5174,
    strictPort: true,
  },
})
