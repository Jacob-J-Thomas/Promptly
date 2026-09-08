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

const replaceYaml = async (page: Page, value: string) => {
  const editor = page.getByRole('textbox', { name: 'Test YAML', exact: true });
  await expect(editor).toBeVisible();
  const editorSurface = page.getByTestId('test-yaml-editor').locator('.monaco-editor .view-line').first();
  await expect(editorSurface).toBeVisible();
  await editorSurface.click();
  await expect(editor).toBeFocused();
  const context = page.context();
  await context.grantPermissions(['clipboard-read', 'clipboard-write'], {
    origin: new URL(page.url()).origin,
  });
  try {
    // Native Monaco treats insertText as typing, including auto-indent and
    // bracket completion. Paste preserves the supplied document verbatim.
    await page.evaluate(async (text) => navigator.clipboard.writeText(text), value);
    await editor.press('ControlOrMeta+A');
    await page.keyboard.press('ControlOrMeta+V');
    await expect.poll(async () => {
      // EditContext exposes only the selected lines; select all before reading.
      await editor.press('ControlOrMeta+A');
      const actualText = await editor.evaluate((element) => {
        if (element instanceof HTMLTextAreaElement) {
          return element.value;
        }
        const editContext = (element as HTMLElement & {
          editContext?: { text?: string };
        }).editContext;
        if (!editContext || typeof editContext.text !== 'string') {
          throw new Error('Monaco editor did not expose its native EditContext text.');
        }
        return editContext.text;
      });
      return actualText.replace(/\r\n/g, '\n');
    }).toBe(value.replace(/\r\n/g, '\n'));
  } finally {
    await context.clearPermissions();
  }
  await editor.press('ArrowRight');
};

const aiEvaluationNotice = 'AI expectations are evaluated by the configured worker when a suite runs. Editing does not call a provider.';

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

const authoringYaml = ({
  externalId,
  name,
  description,
  message,
  temperature,
  enabled,
  caseInsensitive = true,
}: {
  externalId: string;
  name: string;
  description: string;
  message: string;
  temperature: number;
  enabled: boolean;
  caseInsensitive?: boolean;
}) => {
  const expectations = caseInsensitive
    ? allExpectationYaml
    : allExpectationYaml.replace(
      '      case_insensitive: true\n    - type: banned_text',
      '      case_insensitive: false\n    - type: banned_text',
    );
  const messages = [
    message,
    ...Array.from({ length: 11 }, (_, index) => `YAML round-trip message ${index + 2}`),
  ];
  return `- id: ${externalId}
  name: ${name}
  description: ${description}
  input:
    temperature: ${temperature}
    enabled: ${enabled}
    metadata:
      tags:
        - smoke
        - authoring
      nullable: null
      wideInteger: 9007199254740993
      preciseDecimal: 0.123456789012345678901
      exponentValue: 9007199254740993e0
      nested:
        count: 2
    messages:
${messages.map((content) => `      - role: user\n        content: ${JSON.stringify(content)}`).join('\n')}
${expectations}`;
};

const expectLosslessNumericMetadata = (inputSpecJson: string) => {
  expect(inputSpecJson).toMatch(
    /"wideInteger"\s*:\s*9007199254740993(?:[,}])/,
  );
  expect(inputSpecJson).toMatch(
    /"preciseDecimal"\s*:\s*0\.123456789012345678901(?:[,}])/,
  );
  expect(inputSpecJson).toMatch(
    /"exponentValue"\s*:\s*9007199254740993(?:[eE][+]?0)?(?:[,}])/,
  );
};

test('test authoring persists through Form and YAML and runs through the real stack', async ({ page }) => {
  const correlation = process.env.PROMPTLY_E2E_AUTHORING_CORRELATION;
  if (!correlation) {
    throw new Error('PROMPTLY_E2E_AUTHORING_CORRELATION must be supplied by the composed runner');
  }
  const externalMonacoRequests: string[] = [];
  page.on('request', (request) => {
    const hostname = new URL(request.url()).hostname;
    if (hostname === 'cdn.jsdelivr.net' || hostname === 'unpkg.com') {
      externalMonacoRequests.push(request.url());
    }
  });
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
  for (let index = 2; index <= 12; index += 1) {
    await createDialog.getByRole('button', { name: 'Add message' }).click();
    await createDialog.getByLabel(`Content for message ${index}`).fill(`Form message ${index}`);
  }
  await expect(createDialog.getByRole('group', { name: /^Message \d+$/ })).toHaveCount(12);
  await expect(createDialog.getByLabel('Content for message 12')).toHaveValue('Form message 12');
  await createDialog.getByRole('button', { name: 'Add expectation' }).click();
  await page.getByRole('menuitem', { name: 'Contains text', exact: true }).click();
  await createDialog.getByLabel('Text for expectation 1').fill('Deterministically accurate');

  const description = createDialog.getByLabel('Description');
  await description.focus();
  await expect(description).toBeFocused();
  await page.keyboard.press('End');
  await page.keyboard.type(' with keyboard editing');

  await createDialog.getByRole('tab', { name: 'YAML' }).click();
  await expect(createDialog.getByTestId('test-yaml-editor').locator('.monaco-editor')).toBeVisible();
  await expect(createDialog.getByTestId('test-yaml-editor').locator(
    '.monaco-editor .view-line span[class^="mtk"]',
  ).first()).toBeVisible();
  await expect.poll(async () => {
    const classes = await createDialog.getByTestId('test-yaml-editor').locator(
      '.monaco-editor .view-line span[class^="mtk"]',
    ).evaluateAll((spans) => [...new Set(
      spans.flatMap((span) => [...span.classList].filter((name) => /^mtk\d+$/.test(name))),
    )]);
    return classes.length;
  }).toBeGreaterThan(1);
  await expect.poll(async () => (
    (await createDialog.getByTestId('test-yaml-editor').locator('.view-line').allTextContents()).join('\n')
      .replace(/\s+/g, ' ')
  )).toContain('Authored response');
  await expect.poll(async () => (
    (await createDialog.getByTestId('test-yaml-editor').locator('.view-line').allTextContents()).join('\n')
      .replace(/\s+/g, ' ')
  )).toContain('Created through the browser editor with keyboard editing');
  await createDialog.getByRole('tab', { name: 'Form' }).click();
  await expect.poll(() => createDialog.getByRole('alert').allTextContents()).toEqual([]);
  await expect(createDialog.getByRole('tab', { name: 'Form' })).toHaveAttribute('aria-selected', 'true');
  await expect(createDialog.getByRole('group', { name: /^Message \d+$/ })).toHaveCount(12);
  await expect(createDialog.getByLabel('Content for message 12')).toHaveValue('Form message 12');
  await createDialog.getByRole('tab', { name: 'YAML' }).click();
  expect(externalMonacoRequests).toEqual([]);
  await replaceYaml(page, '- id: [broken');
  await createDialog.getByRole('tab', { name: 'Form' }).click();
  await expect(createDialog.getByText(/Repair the YAML errors before returning to Form/)).toBeVisible();
  await expect(createDialog.getByRole('tab', { name: 'YAML' })).toHaveAttribute('aria-selected', 'true');
  await createDialog.getByRole('tab', { name: 'YAML' }).click();
  await replaceYaml(page, authoringYaml({
    externalId: `authoring-${unique.slice(0, 8)}`,
    name: 'Authored response',
    description: 'Created through the browser editor with keyboard editing',
    message: JSON.stringify({
      integrationCorrelation: correlation,
      prompt: 'Hello from authoring',
    }),
    temperature: 0.5,
    enabled: true,
  }));
  await createDialog.getByRole('tab', { name: 'Form' }).click();
  await expect.poll(() => createDialog.getByRole('alert').allTextContents()).toEqual([aiEvaluationNotice]);
  await expect(createDialog.getByRole('tab', { name: 'Form' })).toHaveAttribute('aria-selected', 'true');
  await expect(createDialog.getByRole('group', { name: /^Message \d+$/ })).toHaveCount(12);
  expect(await createDialog.getByLabel('Content for message 1').inputValue()).toContain(correlation);
  await expect(createDialog.getByLabel('Content for message 12')).toHaveValue('YAML round-trip message 12');
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
  const createdInput = JSON.parse(createdBody.inputSpecJson) as {
    messages: Array<{ role: string; content: string }>;
  };
  expect(createdInput.messages).toHaveLength(12);
  expect(createdInput.messages[0]?.content).toContain(correlation);
  expect(createdInput.messages[11]?.content).toBe('YAML round-trip message 12');
  expectLosslessNumericMetadata(createdBody.inputSpecJson);
  await expect(createDialog).toBeHidden();
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
  await expect(editDialog.getByRole('group', { name: /^Message \d+$/ })).toHaveCount(12);
  await expect(editDialog.getByLabel('Content for message 12')).toHaveValue('YAML round-trip message 12');
  const loadedTests = await api(page, `/api/suites/${suiteId}/tests`, 'GET');
  expect(loadedTests.status).toBe(200);
  const loadedTest = (loadedTests.body as Array<{ inputSpecJson: string; expectationsJson: string }>)[0];
  expectLosslessNumericMetadata(loadedTest.inputSpecJson);
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
  await editDialog.getByLabel('Content for message 12').fill('Edited final message from Form');
  await editDialog.getByLabel('Case-insensitive for expectation 1').uncheck();

  const formOnlyUpdateRequest = page.waitForRequest((request) => (
    request.method() === 'PUT' && new URL(request.url()).pathname.startsWith('/api/tests/')
  ));
  await editDialog.getByRole('button', { name: 'Save changes' }).click();
  const formEditedRequest = await formOnlyUpdateRequest;
  const formEditedBody = formEditedRequest.postDataJSON() as {
    name: string;
    inputSpecJson: string;
  };
  expect(formEditedBody.name).toBe('Edited authored response');
  expect(formEditedBody.inputSpecJson).toContain('Edited authoring prompt');
  const formEditedInput = JSON.parse(formEditedBody.inputSpecJson) as {
    messages: Array<{ role: string; content: string }>;
  };
  expect(formEditedInput.messages).toHaveLength(12);
  expect(formEditedInput.messages[0]?.content).toContain(correlation);
  expect(formEditedInput.messages[11]?.content).toBe('Edited final message from Form');
  expectLosslessNumericMetadata(formEditedBody.inputSpecJson);
  await expect(editDialog).toBeHidden();

  await page.reload();
  await expect(page.getByText('Edited authored response', { exact: true })).toBeVisible();
  await openTestMenu(page, 'Edited authored response');
  await page.getByRole('menuitem', { name: 'Edit' }).click();
  const yamlEditDialog = page.getByRole('dialog', { name: 'Edit Test Case' });
  await yamlEditDialog.getByRole('tab', { name: 'YAML' }).click();
  await replaceYaml(page, authoringYaml({
    externalId: `authoring-${unique.slice(0, 8)}`,
    name: 'Edited authored response',
    description: 'Edited and reloaded through the browser',
    message: JSON.stringify({
      integrationCorrelation: correlation,
      prompt: 'Edited authoring prompt',
    }),
    temperature: 0.75,
    enabled: false,
    caseInsensitive: false,
  }));
  await yamlEditDialog.getByRole('tab', { name: 'Form' }).click();
  await expect.poll(() => yamlEditDialog.getByRole('alert').allTextContents()).toEqual([aiEvaluationNotice]);
  await expect(yamlEditDialog.getByRole('tab', { name: 'Form' })).toHaveAttribute('aria-selected', 'true');
  await expect(yamlEditDialog.getByRole('group', { name: /^Message \d+$/ })).toHaveCount(12);
  await expect(yamlEditDialog.getByLabel('Content for message 12')).toHaveValue('YAML round-trip message 12');

  const updateRequest = page.waitForRequest((request) => (
    request.method() === 'PUT' && new URL(request.url()).pathname.startsWith('/api/tests/')
  ));
  await yamlEditDialog.getByRole('button', { name: 'Save changes' }).click();
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
  const updatedInput = JSON.parse(updatedBody.inputSpecJson) as {
    messages: Array<{ role: string; content: string }>;
  };
  expect(updatedInput.messages).toHaveLength(12);
  expect(updatedInput.messages[0]?.content).toContain(correlation);
  expect(updatedInput.messages[11]?.content).toBe('YAML round-trip message 12');
  expectLosslessNumericMetadata(updatedBody.inputSpecJson);
  expect(JSON.parse(updatedBody.expectationsJson)).toEqual([
    { type: 'contains_text', text: 'Deterministically accurate', case_insensitive: false },
    ...authoredExpectations.slice(1),
  ]);
  await expect(yamlEditDialog).toBeHidden();
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
  await expect(reloadedDialog.getByRole('group', { name: /^Message \d+$/ })).toHaveCount(12);
  await expect(reloadedDialog.getByLabel('Content for message 12')).toHaveValue('YAML round-trip message 12');
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
  const reducedBody = reduced.postDataJSON() as { inputSpecJson: string; expectationsJson: string };
  expectLosslessNumericMetadata(reducedBody.inputSpecJson);
  expect(JSON.parse(reducedBody.expectationsJson)).toEqual([
    { type: 'contains_text', text: 'Deterministically accurate', case_insensitive: false },
  ]);
  await expect(reductionDialog).toBeHidden();

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
