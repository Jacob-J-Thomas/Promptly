import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = path.dirname(fileURLToPath(import.meta.url));
const artifactsRoot = path.resolve(webRoot, '../../../artifacts/test-results/e2e');
const phase = process.env.PROMPTLY_E2E_PHASE;
if (phase !== 'proxy' && phase !== 'direct') {
  throw new Error('PROMPTLY_E2E_PHASE must be proxy or direct');
}
const baseURL = process.env.PROMPTLY_E2E_WEB_ORIGIN;

if (!baseURL) {
  throw new Error('PROMPTLY_E2E_WEB_ORIGIN is required; run npm run test:e2e');
}

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  grep: phase === 'proxy'
    ? /anonymous protected routes redirect to login|registration logout and login traverse the real stack|an expired signed session receives 401 and clears stored authentication/
    : /persisted Run Suite configuration queues a run and loads its result/,
  outputDir: path.join(artifactsRoot, phase, 'playwright-output'),
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
    ['junit', { outputFile: path.join(artifactsRoot, phase, 'junit.xml') }],
    ['json', { outputFile: path.join(artifactsRoot, phase, 'results.json') }],
    ['html', { outputFolder: path.join(artifactsRoot, phase, 'html'), open: 'never' }],
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
