import { randomUUID } from 'node:crypto';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';

interface ApiResult {
  status: number;
  body: unknown;
}

const api = async (
  page: Page,
  path: string,
  method: string,
  body?: Record<string, unknown>,
): Promise<ApiResult> => page.evaluate(async ({ path: requestPath, method: requestMethod, body: requestBody }) => {
  const token = localStorage.getItem('auth_token');
  const response = await fetch(requestPath, {
    method: requestMethod,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: requestBody ? JSON.stringify(requestBody) : undefined,
  });
  const text = await response.text();
  let responseBody: unknown = null;
  try {
    responseBody = text ? JSON.parse(text) : null;
  } catch {
    responseBody = text;
  }
  return { status: response.status, body: responseBody };
}, { path, method, body });

const requireCreated = async (
  page: Page,
  path: string,
  body: Record<string, unknown>,
): Promise<Record<string, unknown>> => {
  const response = await api(page, path, 'POST', body);
  expect(response.status, `${path} setup response`).toBeLessThan(300);
  expect(response.body).toBeTruthy();
  return response.body as Record<string, unknown>;
};

test('persisted Run Suite configuration queues a run and loads its result', async ({ page }) => {
  const unique = randomUUID();
  const email = `run-suite-${unique}@promptly.invalid`;
  const password = `Promptly-${unique}-A1`;

  await page.goto('/register');
  await page.getByLabel('Full Name').fill('Run Suite E2E User');
  await page.getByLabel('Email Address').fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByLabel('Confirm Password').fill(password);
  await page.getByRole('button', { name: 'Sign Up' }).click();
  await expect(page).toHaveURL(/\/$/);

  const project = await requireCreated(page, '/api/projects', {
    name: `Run Suite ${unique.slice(0, 8)}`,
    description: 'Safe composed Run Suite fixture',
  });
  const projectId = String(project.id);
  const environment = await requireCreated(page, `/api/projects/${projectId}/environments`, {
    name: 'Deterministic provider fixture',
    baseUrl: `http://${process.env.PROMPTLY_E2E_PROVIDER_HOST ?? 'provider-stub'}:8080`,
    headers: { Authorization: 'Bearer verification-only' },
  });
  const environmentId = String(environment.id);
  const endpoint = await requireCreated(page, `/api/environments/${environmentId}/endpoints`, {
    name: 'Chat completion fixture',
    path: '/v1/chat/completions',
    httpMethod: 'POST',
    timeoutSeconds: 30,
  });
  const endpointId = String(endpoint.id);
  const mapping = await requireCreated(page, `/api/endpoints/${endpointId}/mapping`, {
    name: 'OpenAI-compatible fixture mapping',
    specJson: JSON.stringify({
      version: 1,
      messages: {
        itemsPath: '$.choices[*].message',
        rolePath: '$.role',
        contentPath: '$.content',
      },
    }),
  });
  const suite = await requireCreated(page, `/api/suites?projectId=${projectId}`, {
    name: `Persisted Run Suite ${unique.slice(0, 8)}`,
    description: 'Exercises the real selection and queue flow',
  });
  const suiteId = String(suite.id);
  await requireCreated(page, `/api/suites/${suiteId}/tests`, {
    externalId: `run-suite-${unique.slice(0, 8)}`,
    name: 'Provider fixture response',
    inputSpecJson: JSON.stringify({
      messages: [{
        role: 'user',
        content: JSON.stringify({ integrationCorrelation: unique, prompt: 'Hello' }),
      }],
    }),
    expectationsJson: JSON.stringify([
      { type: 'contains_text', text: 'Deterministically accurate' },
    ]),
  });

  await page.goto(`/suites/${suiteId}`);
  await expect(page.getByRole('heading', { name: suite.name as string })).toBeVisible();
  await page.getByRole('button', { name: 'Run Suite' }).click();
  const dialog = page.getByRole('dialog', { name: 'Run Test Suite' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('combobox', { name: 'Environment' })).toContainText(
    'Deterministic provider fixture',
  );
  await expect(dialog.getByRole('combobox', { name: 'Endpoint' })).toContainText(
    'Chat completion fixture',
  );
  await expect(dialog.getByRole('combobox', { name: 'Mapping Spec' })).toContainText(
    'OpenAI-compatible fixture mapping',
  );
  await expect(dialog.getByRole('button', { name: 'Start Run' })).toBeEnabled();

  const queued = page.waitForResponse((response) => (
    response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/runs'
  ));
  await dialog.getByRole('button', { name: 'Start Run' }).click();
  const queuedResponse = await queued;
  expect(queuedResponse.status()).toBe(201);
  const run = await queuedResponse.json() as { id: string };
  await expect(page).toHaveURL(new RegExp(`/runs/${run.id}$`));
  await expect(page.getByRole('heading', { name: 'Test Run' })).toBeVisible();

  await expect.poll(async () => {
    const current = await api(page, `/api/runs/${run.id}`, 'GET');
    return (current.body as { status?: number | string }).status;
  }, { timeout: 20_000, intervals: [1_000, 2_000] }).toBe(2);
  await page.reload();
  await expect(page.getByText('Completed', { exact: true })).toBeVisible();
  await expect(page.getByText('Test Results (1)', { exact: true })).toBeVisible();
  await expect(page.getByText('Provider fixture response', { exact: true })).toBeVisible();
  await expect(page.getByText('Pass', { exact: true })).toBeVisible();
  await expect(page.getByText('1 / 1', { exact: true })).toBeVisible();
  const results = await api(page, `/api/runs/${run.id}/results`, 'GET');
  expect(results.status).toBe(200);
  const [result] = results.body as Array<{
    status?: number | string;
    traceJson?: string;
    metricsJson?: string;
  }>;
  expect(result.status).toBe(0);
  expect(result.traceJson).toContain('Deterministically accurate');
  expect(JSON.parse(result.metricsJson ?? '{}')).toMatchObject({
    passed: 1,
    failed: 0,
    errors: 0,
    total: 1,
  });

  const deniedEnvironment = await requireCreated(page, `/api/projects/${projectId}/environments`, {
    name: 'Denied private destination fixture',
    baseUrl: 'http://127.0.0.1:8080',
    headers: { Authorization: 'Bearer verification-only' },
  });
  const deniedEnvironmentId = String(deniedEnvironment.id);
  const deniedEndpoint = await requireCreated(
    page,
    `/api/environments/${deniedEnvironmentId}/endpoints`,
    {
      name: 'Denied private endpoint',
      path: '/v1/chat/completions',
      httpMethod: 'POST',
      timeoutSeconds: 30,
    },
  );
  const deniedMapping = await requireCreated(
    page,
    `/api/endpoints/${String(deniedEndpoint.id)}/mapping`,
    {
      name: 'Denied private mapping',
      specJson: JSON.stringify({
        version: 1,
        messages: {
          itemsPath: '$.choices[*].message',
          rolePath: '$.role',
          contentPath: '$.content',
        },
      }),
    },
  );
  const deniedRunResponse = await api(page, '/api/runs', 'POST', {
    suiteId,
    environmentId: deniedEnvironmentId,
    endpointId: String(deniedEndpoint.id),
    mappingSpecId: String(deniedMapping.id),
  });
  expect(deniedRunResponse.status).toBe(201);
  const deniedRunId = String((deniedRunResponse.body as { id: string }).id);
  await expect.poll(async () => {
    const current = await api(page, `/api/runs/${deniedRunId}`, 'GET');
    return (current.body as { status?: number | string }).status;
  }, { timeout: 20_000, intervals: [1_000, 2_000] }).toBe(2);
  const deniedResults = await api(page, `/api/runs/${deniedRunId}/results`, 'GET');
  expect(deniedResults.status).toBe(200);
  const [deniedResult] = deniedResults.body as Array<{
    status?: number | string;
    failureReasonsJson?: string;
  }>;
  expect([2, 'Error']).toContain(deniedResult.status);
  expect(deniedResult.failureReasonsJson).toContain('Unsafe endpoint destination');
  expect(mapping.id).toBeTruthy();
});
