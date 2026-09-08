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

const openTestMenu = async (page: Page, testName: string) => {
  const menuButton = page.getByRole('button', { name: `Actions for ${testName}`, exact: true });
  await menuButton.click();
  await expect(page.getByRole('menuitem', { name: 'Edit' })).toBeVisible();
};

const allExpectationYaml = `  expectations:
    - type: contains_text
      text: Deterministically accurate
      case_insensitive: true
    - type: banned_text
      text: forbidden
      case_insensitive: false
    - type: regex_match
      pattern: Deterministically
      case_insensitive: true
    - type: link_pattern
      pattern: https://example.com
    - type: tool_called
      tool_name: search
    - type: tool_sequence
      sequence:
        - search
        - summarize
      exact_sequence: false
    - type: llm_judge
      rubric: Helpful human judge
      min_score: 0.8
      model: null
      provider: null
    - type: groundedness
      min_score: 0.8
      model: null
      provider: null`;

const authoredExpectations = [
  { type: 'contains_text', text: 'Deterministically accurate', case_insensitive: true },
  { type: 'banned_text', text: 'forbidden', case_insensitive: false },
  { type: 'regex_match', pattern: 'Deterministically', case_insensitive: true },
  { type: 'link_pattern', pattern: 'https://example.com' },
  { type: 'tool_called', tool_name: 'search' },
  { type: 'tool_sequence', sequence: ['search', 'summarize'], exact_sequence: false },
  {
    type: 'llm_judge',
    rubric: 'Helpful human judge',
    min_score: 0.8,
    model: null,
    provider: null,
  },
  { type: 'groundedness', min_score: 0.8, model: null, provider: null },
] as const;

test('test authoring persists through Form and YAML and runs through the real stack', async ({ page }) => {
  const correlation = process.env.PROMPTLY_E2E_AUTHORING_CORRELATION;
  if (!correlation) {
    throw new Error('PROMPTLY_E2E_AUTHORING_CORRELATION must be supplied by the composed runner');
  }
  const unique = randomUUID();
  const email = `authoring-${unique}@promptly.invalid`;
  const password = `Promptly-${unique}-A1`;

  await page.goto('/register');
  await page.getByLabel('Full Name').fill('Test Authoring E2E User');
  await page.getByLabel('Email Address').fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByLabel('Confirm Password').fill(password);
  await page.getByRole('button', { name: 'Sign Up' }).click();
  await expect(page).toHaveURL(/\/$/);

  const project = await requireCreated(page, '/api/projects', {
    name: `Authoring ${unique.slice(0, 8)}`,
    description: 'Safe composed test-authoring fixture',
  });
  const projectId = String(project.id);
  const environment = await requireCreated(page, `/api/projects/${projectId}/environments`, {
    name: 'Authoring provider fixture',
    baseUrl: `http://${process.env.PROMPTLY_E2E_PROVIDER_HOST ?? 'provider-stub'}:8080`,
    headers: { Authorization: 'Bearer verification-only' },
  });
  const environmentId = String(environment.id);
  const endpoint = await requireCreated(page, `/api/environments/${environmentId}/endpoints`, {
    name: 'Authoring chat endpoint',
    path: '/v1/chat/completions',
    httpMethod: 'POST',
    timeoutSeconds: 30,
  });
  const endpointId = String(endpoint.id);
  const mapping = await requireCreated(page, `/api/endpoints/${endpointId}/mapping`, {
    name: 'Authoring response mapping',
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
    name: `Test Authoring ${unique.slice(0, 8)}`,
    description: 'Exercises the real Form and YAML authoring flow',
  });
  const suiteId = String(suite.id);

  await page.goto(`/suites/${suiteId}`);
  await expect(page.getByRole('heading', { name: suite.name as string })).toBeVisible();
  await page.getByRole('button', { name: 'Create Test' }).click();
  const createDialog = page.getByRole('dialog', { name: 'Create Test Case' });
  await createDialog.getByLabel('External ID').fill(`authoring-${unique.slice(0, 8)}`);
  await createDialog.getByRole('textbox', { name: 'Name', exact: true }).fill('Authored response');
  await createDialog.getByLabel('Description').fill('Created through the browser editor');
  await createDialog.getByRole('button', { name: 'Add message' }).click();
  await createDialog.getByLabel('Content for message 1').fill(JSON.stringify({
    integrationCorrelation: correlation,
    prompt: 'Hello from authoring',
  }));
  await createDialog.getByRole('button', { name: 'Add expectation' }).click();
  await page.getByRole('menuitem', { name: 'Contains text', exact: true }).click();
  await createDialog.getByLabel('Text for expectation 1').fill('Deterministically accurate');

  const description = createDialog.getByLabel('Description');
  await description.focus();
  await expect(description).toBeFocused();
  await page.keyboard.press('End');
  await page.keyboard.type(' with keyboard editing');

  await createDialog.getByRole('tab', { name: 'YAML' }).click();
  const yamlEditor = createDialog.getByLabel('Test YAML');
  const initialYaml = await yamlEditor.inputValue();
  await yamlEditor.fill('- id: [broken');
  await createDialog.getByRole('tab', { name: 'Form' }).click();
  await expect(createDialog.getByText(/Repair the YAML errors before returning to Form/)).toBeVisible();
  const authoredYaml = initialYaml.slice(0, initialYaml.indexOf('  expectations:'))
    .replace('  input:\n', `  input:
    temperature: 0.5
    enabled: true
    metadata:
      tags:
        - smoke
        - authoring
      nullable: null
      nested:
        count: 2
`)
    + allExpectationYaml;
  await yamlEditor.fill(authoredYaml);
  await createDialog.getByRole('tab', { name: 'Form' }).click();
  expect(await createDialog.getByLabel('Content for message 1').inputValue()).toContain(correlation);
  await expect(createDialog.getByLabel('Text for expectation 1')).toHaveValue('Deterministically accurate');
  await expect(createDialog.getByLabel('Text for expectation 2')).toHaveValue('forbidden');
  await expect(createDialog.getByLabel('Pattern for expectation 3')).toHaveValue('Deterministically');
  await expect(createDialog.getByLabel('Link pattern for expectation 4')).toHaveValue('https://example.com');
  await expect(createDialog.getByLabel('Tool name for expectation 5')).toHaveValue('search');
  await expect(createDialog.getByLabel('Tool 1 for expectation 6')).toHaveValue('search');
  await expect(createDialog.getByLabel('Tool 2 for expectation 6')).toHaveValue('summarize');
  await expect(createDialog.getByLabel('Rubric for expectation 7')).toHaveValue('Helpful human judge');
  await expect(createDialog.getByLabel('Minimum score for expectation 7')).toHaveValue('0.8');
  await expect(createDialog.getByLabel('Minimum score for expectation 8')).toHaveValue('0.8');

  const createRequest = page.waitForRequest((request) => (
    request.method() === 'POST'
      && new URL(request.url()).pathname === `/api/suites/${suiteId}/tests`
  ));
  await createDialog.getByRole('button', { name: 'Create' }).click();
  const createdRequest = await createRequest;
  const createdBody = createdRequest.postDataJSON() as {
    inputSpecJson: string;
    expectationsJson: string;
  };
  expect(JSON.parse(createdBody.inputSpecJson)).toMatchObject({
    temperature: 0.5,
    enabled: true,
    metadata: {
      tags: ['smoke', 'authoring'],
      nullable: null,
      nested: { count: 2 },
    },
  });
  expect(createdBody.inputSpecJson).toContain(correlation);
  expect(JSON.parse(createdBody.expectationsJson)).toEqual(authoredExpectations);
  await expect(page.getByText('Authored response', { exact: true })).toBeVisible();

  await page.setViewportSize({ width: 375, height: 900 });
  await expect.poll(() => page.evaluate(() => (
    document.documentElement.scrollWidth <= window.innerWidth
  ))).toBe(true);

  await page.reload();
  await expect(page.getByText('Authored response', { exact: true })).toBeVisible();
  await openTestMenu(page, 'Authored response');
  await page.getByRole('menuitem', { name: 'Edit' }).click();
  const editDialog = page.getByRole('dialog', { name: 'Edit Test Case' });
  await expect.poll(() => editDialog.evaluate((dialog) => (
    dialog.scrollWidth <= dialog.clientWidth
  ))).toBe(true);
  await expect(editDialog.getByLabel('Text for expectation 1')).toHaveValue('Deterministically accurate');
  await expect(editDialog.getByLabel('Text for expectation 2')).toHaveValue('forbidden');
  await expect(editDialog.getByLabel('Pattern for expectation 3')).toHaveValue('Deterministically');
  await expect(editDialog.getByLabel('Link pattern for expectation 4')).toHaveValue('https://example.com');
  await expect(editDialog.getByLabel('Tool name for expectation 5')).toHaveValue('search');
  await expect(editDialog.getByLabel('Tool 1 for expectation 6')).toHaveValue('search');
  await expect(editDialog.getByLabel('Tool 2 for expectation 6')).toHaveValue('summarize');
  await expect(editDialog.getByLabel('Rubric for expectation 7')).toHaveValue('Helpful human judge');
  await expect(editDialog.getByLabel('Minimum score for expectation 7')).toHaveValue('0.8');
  await expect(editDialog.getByLabel('Minimum score for expectation 8')).toHaveValue('0.8');
  const loadedTests = await api(page, `/api/suites/${suiteId}/tests`, 'GET');
  expect(loadedTests.status).toBe(200);
  const loadedTest = (loadedTests.body as Array<{ expectationsJson: string }>)[0];
  expect(JSON.parse(loadedTest.expectationsJson)).toEqual(authoredExpectations);

  const editName = editDialog.getByRole('textbox', { name: 'Name', exact: true });
  await editName.focus();
  await expect(editName).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(editDialog.getByLabel('Description')).toBeFocused();
  await editName.fill('Edited authored response');
  await editDialog.getByLabel('Description').fill('Edited and reloaded through the browser');
  await editDialog.getByLabel('Content for message 1').fill(JSON.stringify({
    integrationCorrelation: correlation,
    prompt: 'Edited authoring prompt',
  }));
  await editDialog.getByLabel('Case-insensitive for expectation 1').uncheck();
  await editDialog.getByRole('tab', { name: 'YAML' }).click();
  const editedYaml = editDialog.getByLabel('Test YAML');
  await editedYaml.fill((await editedYaml.inputValue())
    .replace('temperature: 0.5', 'temperature: 0.75')
    .replace('enabled: true', 'enabled: false'));
  await editDialog.getByRole('tab', { name: 'Form' }).click();

  const updateRequest = page.waitForRequest((request) => (
    request.method() === 'PUT' && new URL(request.url()).pathname.startsWith('/api/tests/')
  ));
  await editDialog.getByRole('button', { name: 'Save changes' }).click();
  const updatedRequest = await updateRequest;
  const updatedBody = updatedRequest.postDataJSON() as {
    name: string;
    inputSpecJson: string;
    expectationsJson: string;
  };
  expect(updatedBody.name).toBe('Edited authored response');
  expect(JSON.parse(updatedBody.inputSpecJson)).toMatchObject({
    temperature: 0.75,
    enabled: false,
    metadata: {
      tags: ['smoke', 'authoring'],
      nullable: null,
      nested: { count: 2 },
    },
  });
  expect(updatedBody.inputSpecJson).toContain('Edited authoring prompt');
  expect(JSON.parse(updatedBody.expectationsJson)).toEqual([
    { type: 'contains_text', text: 'Deterministically accurate', case_insensitive: false },
    ...authoredExpectations.slice(1),
  ]);
  await expect(page.getByText('Edited authored response', { exact: true })).toBeVisible();

  await page.goto(`/suites/${suiteId}`);
  await expect(page.getByText('Edited authored response', { exact: true })).toBeVisible();
  await openTestMenu(page, 'Edited authored response');
  await page.getByRole('menuitem', { name: 'Edit' }).click();
  const reloadedDialog = page.getByRole('dialog', { name: 'Edit Test Case' });
  await expect(reloadedDialog.getByRole('textbox', { name: 'Name', exact: true }))
    .toHaveValue('Edited authored response');
  await expect(reloadedDialog.getByLabel('Content for message 1')).toHaveValue(
    JSON.stringify({ integrationCorrelation: correlation, prompt: 'Edited authoring prompt' }),
  );
  await expect(reloadedDialog.getByLabel('Case-insensitive for expectation 1')).not.toBeChecked();
  await reloadedDialog.getByRole('button', { name: 'Cancel' }).click();

  await openTestMenu(page, 'Edited authored response');
  await page.getByRole('menuitem', { name: 'Edit' }).click();
  const reductionDialog = page.getByRole('dialog', { name: 'Edit Test Case' });
  for (let index = authoredExpectations.length; index >= 2; index -= 1) {
    await reductionDialog.getByRole('button', { name: `Delete expectation ${index}` }).click();
  }
  await expect(reductionDialog.getByLabel('Text for expectation 1')).toHaveValue('Deterministically accurate');
  await expect(reductionDialog.getByRole('group', { name: 'Expectation 2' })).toHaveCount(0);
  const reductionRequest = page.waitForRequest((request) => (
    request.method() === 'PUT' && new URL(request.url()).pathname.startsWith('/api/tests/')
  ));
  await reductionDialog.getByRole('button', { name: 'Save changes' }).click();
  const reduced = await reductionRequest;
  const reducedBody = reduced.postDataJSON() as { expectationsJson: string };
  expect(JSON.parse(reducedBody.expectationsJson)).toEqual([
    { type: 'contains_text', text: 'Deterministically accurate', case_insensitive: false },
  ]);

  await page.getByRole('button', { name: 'Run Suite' }).click();
  const runDialog = page.getByRole('dialog', { name: 'Run Test Suite' });
  await expect(runDialog.getByRole('button', { name: 'Start Run' })).toBeEnabled();
  const queued = page.waitForResponse((response) => (
    response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/runs'
  ));
  await runDialog.getByRole('button', { name: 'Start Run' }).click();
  const queuedResponse = await queued;
  expect(queuedResponse.status()).toBe(201);
  const run = await queuedResponse.json() as { id: string };
  await expect(page).toHaveURL(new RegExp(`/runs/${run.id}$`));

  await expect.poll(async () => {
    const current = await api(page, `/api/runs/${run.id}`, 'GET');
    return (current.body as { status?: number | string }).status;
  }, { timeout: 20_000, intervals: [1_000, 2_000] }).toBe(2);
  await page.reload();
  const runStatus = page.getByRole('heading', { name: 'Run Status' }).locator('xpath=..');
  await expect(runStatus.getByText('Completed', { exact: true })).toBeVisible();
  await expect(page.getByText('Test Results (1)', { exact: true })).toBeVisible();
  await expect(page.getByText('Edited authored response', { exact: true })).toBeVisible();
  await expect(page.getByText('Pass', { exact: true })).toBeVisible();
  await expect(page.getByText('1 / 1', { exact: true })).toBeVisible();
  const results = await api(page, `/api/runs/${run.id}/results`, 'GET');
  expect(results.status).toBe(200);
  const [result] = results.body as Array<{ status?: number | string; traceJson?: string }>;
  expect(result.status).toBe(0);
  expect(result.traceJson).toContain('Deterministically accurate');
  expect(mapping.id).toBeTruthy();
});
