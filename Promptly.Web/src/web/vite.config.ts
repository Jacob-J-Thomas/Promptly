import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    host: '0.0.0.0',
    port: 3000,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    passWithNoTests: false,
    setupFiles: ['./src/test/setup.ts'],
    reporters: ['default', ['junit', { outputFile: '../../../artifacts/test-results/web/junit.xml' }]],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/test/**',
        'src/vite-env.d.ts',
      ],
      reporter: [
        ['text'],
        ['json', { file: 'coverage-final.json' }],
        ['cobertura', { file: 'cobertura-coverage.xml' }],
      ],
      reportsDirectory: '../../../artifacts/test-results/web/coverage',
      thresholds: {
        lines: 90,
        branches: 90,
      },
    },
  },
})
