import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = path.dirname(fileURLToPath(import.meta.url));
const artifactsRoot = path.resolve(webRoot, '../../../artifacts/test-results/e2e');
const baseURL = process.env.PROMPTLY_E2E_WEB_ORIGIN;

if (!baseURL) {
  throw new Error('PROMPTLY_E2E_WEB_ORIGIN is required; run npm run test:e2e');
}

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  outputDir: path.join(artifactsRoot, 'playwright-output'),
  fullyParallel: false,
  forbidOnly: true,
  repeatEach: 1,
  retries: 0,
  workers: 1,
  timeout: 30_000,
  expect: {
    timeout: 10_000,
  },
  reporter: [
    ['list'],
    ['junit', { outputFile: path.join(artifactsRoot, 'junit.xml') }],
    ['json', { outputFile: path.join(artifactsRoot, 'results.json') }],
    ['html', { outputFolder: path.join(artifactsRoot, 'html'), open: 'never' }],
  ],
  use: {
    baseURL,
    actionTimeout: 10_000,
    navigationTimeout: 15_000,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    serviceWorkers: 'block',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        browserName: 'chromium',
      },
    },
  ],
});
