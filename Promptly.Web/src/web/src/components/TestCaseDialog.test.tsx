import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { testsApi, type TestCase } from '../api/tests';
import { TestCaseDialog } from './TestCaseDialog';

vi.mock('../api/tests', () => ({
  testsApi: {
    create: vi.fn(),
    update: vi.fn(),
  },
}));

const persistedTest: TestCase = {
  id: 'test-1',
  suiteId: 'suite-1',
  externalId: 'case-1',
  name: 'Greeting',
  description: 'Original description',
  inputSpecJson: JSON.stringify({
    messages: [{ role: 'user', content: 'Hello' }],
    temperature: 0.5,
    enabled: true,
    metadata: { tags: ['demo'] },
  }),
  expectationsJson: JSON.stringify([
    {
      type: 'llm_judge',
      rubric: 'Helpful',
      min_score: 0.8,
      model: 'stored-model',
      provider: 'stored-provider',
    },
  ]),
  createdAt: '2026-08-12T00:00:00Z',
};

const allTypesYaml = `- id: all-types
  name: All expectation types
  description: Authored through YAML
  input:
    temperature: 0.5
    enabled: true
    metadata:
      nullable: null
      nested:
        count: 2
    messages:
      - role: system
        content: system guidance
      - role: user
        content: user prompt
  expectations:
    - type: contains_text
      text: contains
      case_insensitive: true
    - type: banned_text
      text: banned
      case_insensitive: true
    - type: regex_match
      pattern: ^answer$
      case_insensitive: true
    - type: link_pattern
      pattern: https://example.invalid/*
    - type: tool_called
      tool_name: search
    - type: tool_sequence
      sequence:
        - search
        - summarize
      exact_sequence: false
    - type: llm_judge
      rubric: Helpful
      min_score: 0
    - type: groundedness
      min_score: 1`;

const makeInputAtByteLimit = () => {
  const metadataKeys = Array.from({ length: 16 }, (_, index) => `padding${index}`);
  const emptyMetadata = Object.fromEntries(metadataKeys.map((key) => [key, '']));
  const emptyJson = JSON.stringify({
    ...emptyMetadata,
    messages: [{ role: 'user', content: 'small' }],
  });
  const targetBytes = 262_144 - 1;
  const emptyBytes = new TextEncoder().encode(emptyJson).byteLength;
  const contentBytes = targetBytes - emptyBytes;
  const baseLength = Math.floor(contentBytes / metadataKeys.length);
  const remainder = contentBytes % metadataKeys.length;
  const inputMetadata = Object.fromEntries(metadataKeys.map((key, index) => [
    key,
    'x'.repeat(baseLength + (index < remainder ? 1 : 0)),
  ]));
  const inputSpecJson = JSON.stringify({
    ...inputMetadata,
    messages: [{ role: 'user', content: 'small' }],
  });

  expect(new TextEncoder().encode(inputSpecJson).byteLength).toBe(targetBytes);
  expect(Object.values(inputMetadata).every((value) => value.length <= 16_384)).toBe(true);
  return { inputSpecJson };
};

const renderDialog = (
  testCase: TestCase | null = null,
  onClose = vi.fn(),
  onSaved = vi.fn().mockResolvedValue(undefined),
) => {
  render(
    <TestCaseDialog
      open
      suiteId="suite-1"
      testCase={testCase}
      onClose={onClose}
      onSaved={onSaved}
    />,
  );
  return { onClose, onSaved };
};

const fillMinimumDraft = () => {
  fireEvent.change(screen.getByLabelText(/^External ID/), { target: { value: 'case-new' } });
  fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'New test' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add message' }));
  const message = screen.getByRole('group', { name: 'Message 1' });
  fireEvent.mouseDown(within(message).getByRole('combobox'));
  fireEvent.click(screen.getByRole('option', { name: 'user' }));
  fireEvent.change(screen.getByLabelText('Content for message 1'), {
    target: { value: 'Hello from dialog' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Add expectation' }));
  fireEvent.click(screen.getByRole('menuitem', { name: 'Contains text' }));
  fireEvent.change(screen.getByLabelText('Text for expectation 1'), {
    target: { value: 'Hello from dialog' },
  });
};

describe('TestCaseDialog', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('creates a complete form draft once with ordered messages and expectations', async () => {
    const saved = { ...persistedTest, id: 'created' };
    vi.mocked(testsApi.create).mockResolvedValue(saved);
    const callbacks = renderDialog();
    fillMinimumDraft();

    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(testsApi.create).toHaveBeenCalledOnce());
    expect(testsApi.create).toHaveBeenCalledWith('suite-1', {
      externalId: 'case-new',
      name: 'New test',
      description: '',
      inputSpecJson: '{"messages":[{"role":"user","content":"Hello from dialog"}]}',
      expectationsJson: '[{"type":"contains_text","text":"Hello from dialog","case_insensitive":true}]',
    });
    expect(callbacks.onSaved).toHaveBeenCalledWith(saved);
    expect(callbacks.onClose).toHaveBeenCalledOnce();
  });

  it('rejects a serialized input aggregate that crosses the byte cap on edit', async () => {
    const boundaryInput = makeInputAtByteLimit();
    renderDialog({ ...persistedTest, inputSpecJson: boundaryInput.inputSpecJson });
    const editedContent = 'smallxx';
    const editedInput = JSON.parse(boundaryInput.inputSpecJson);
    editedInput.messages[0].content = editedContent;
    expect(new TextEncoder().encode(JSON.stringify(editedInput)).byteLength).toBe(262_145);
    fireEvent.change(screen.getByLabelText('Content for message 1'), {
      target: { value: editedContent },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(testsApi.update).not.toHaveBeenCalled();
    expect(await screen.findByText('Fix the highlighted input fields before saving this test.'))
      .toBeInTheDocument();
    expect(screen.getByText(
      'input: Input JSON exceeds the 262144-byte limit.',
    )).toBeInTheDocument();
    expect(screen.getByLabelText('Content for message 1')).toHaveValue(editedContent);
  });

  it('authors all eight expectation types through YAML and preserves ordered message edits', async () => {
    const saved = { ...persistedTest, id: 'created-all-types' };
    vi.mocked(testsApi.update).mockResolvedValue(saved);
    renderDialog(persistedTest);
    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));
    fireEvent.change(screen.getByLabelText('Test YAML'), { target: { value: allTypesYaml } });
    fireEvent.click(screen.getByRole('tab', { name: 'Form' }));

    expect(screen.getByLabelText(/^External ID/)).toHaveValue('all-types');
    expect(screen.getByLabelText(/^Name/)).toHaveValue('All expectation types');
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('system guidance');
    fireEvent.change(screen.getByLabelText('Content for message 2'), {
      target: { value: 'user prompt\nwith two lines' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Move message 2 up' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move message 1 down' }));
    fireEvent.click(within(screen.getByRole('group', { name: 'Message 1' }))
      .getByRole('button', { name: 'Delete message 1' }));

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(testsApi.update).toHaveBeenCalledOnce());

    const [, request] = vi.mocked(testsApi.update).mock.calls[0] ?? [];
    expect(JSON.parse(request?.inputSpecJson ?? '{}')).toEqual({
      temperature: 0.5,
      enabled: true,
      metadata: { nullable: null, nested: { count: 2 } },
      messages: [{ role: 'user', content: 'user prompt\nwith two lines' }],
    });
    expect(JSON.parse(request?.expectationsJson ?? '[]')).toEqual([
      { type: 'contains_text', text: 'contains', case_insensitive: true },
      { type: 'banned_text', text: 'banned', case_insensitive: true },
      { type: 'regex_match', pattern: '^answer$', case_insensitive: true },
      { type: 'link_pattern', pattern: 'https://example.invalid/*' },
      { type: 'tool_called', tool_name: 'search' },
      { type: 'tool_sequence', sequence: ['search', 'summarize'], exact_sequence: false },
      { type: 'llm_judge', rubric: 'Helpful', min_score: 0 },
      { type: 'groundedness', min_score: 1 },
    ]);
  });

  it('ignores a duplicate submit while the create request is pending', async () => {
    let resolveCreate: ((testCase: TestCase) => void) | undefined;
    const pendingCreate = new Promise<TestCase>((resolve) => {
      resolveCreate = resolve;
    });
    vi.mocked(testsApi.create).mockReturnValue(pendingCreate);
    const callbacks = renderDialog();
    fillMinimumDraft();

    const createButton = screen.getByRole('button', { name: 'Create' });
    fireEvent.click(createButton);
    fireEvent.click(createButton);
    expect(testsApi.create).toHaveBeenCalledOnce();
    expect(createButton).toBeDisabled();

    resolveCreate?.({ ...persistedTest, id: 'created' });
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce());
  });

  it('loads edit values, preserves input metadata and AI options, and issues one PUT', async () => {
    vi.mocked(testsApi.update).mockResolvedValue({ ...persistedTest, name: 'Edited' });
    const callbacks = renderDialog(persistedTest);

    expect(screen.getByLabelText(/^External ID/)).toHaveValue('case-1');
    expect(screen.getByLabelText(/^Name/)).toHaveValue('Greeting');
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Hello');
    expect(screen.getByLabelText('Rubric for expectation 1')).toHaveValue('Helpful');
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Edited' } });
    fireEvent.change(screen.getByLabelText('Content for message 1'), {
      target: { value: 'Updated hello' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(testsApi.update).toHaveBeenCalledOnce());
    const [, request] = vi.mocked(testsApi.update).mock.calls[0] ?? [];
    expect(request).toMatchObject({
      externalId: 'case-1',
      name: 'Edited',
      inputSpecJson: '{"temperature":0.5,"enabled":true,"metadata":{"tags":["demo"]},"messages":[{"role":"user","content":"Updated hello"}]}',
      expectationsJson: persistedTest.expectationsJson,
    });
    expect(callbacks.onSaved).toHaveBeenCalled();
    expect(callbacks.onClose).toHaveBeenCalledOnce();
  });

  it('preserves the draft and offers retry/cancel after a safe API error', async () => {
    vi.mocked(testsApi.create).mockRejectedValueOnce(new Error('database secret should not render'));
    const callbacks = renderDialog();
    fillMinimumDraft();
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('Unable to save this test. Your draft is still here.'))
      .toBeInTheDocument();
    expect(screen.getByLabelText(/^Name/)).toHaveValue('New test');
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Hello from dialog');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
    expect(within(screen.getByRole('alert')).getByRole('button', { name: 'Cancel' })).toBeEnabled();
    expect(screen.queryByText('database secret should not render')).not.toBeInTheDocument();
    fireEvent.click(within(screen.getByRole('alert')).getByRole('button', { name: 'Cancel' }));
    expect(callbacks.onClose).toHaveBeenCalledOnce();
  });

  it('renders only safe API validation paths alongside the draft-preserving error', async () => {
    const apiError = Object.assign(new Error('internal details'), {
      isAxiosError: true,
      response: {
        data: {
          message: 'The test could not be saved.',
          errors: [
            { path: 'input.messages[0].content', message: 'Content is required.' },
            { path: 'expectations[0].min_score', message: 'Score must be between 0 and 1.' },
            { path: 42, message: 'discard this malformed entry' },
            { path: 'input.metadata.secret', message: '   ' },
          ],
        },
      },
    });
    vi.mocked(testsApi.create).mockRejectedValueOnce(apiError);
    renderDialog();
    fillMinimumDraft();
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('The test could not be saved.')).toBeInTheDocument();
    expect(screen.getByText('input.messages[0].content: Content is required.')).toBeInTheDocument();
    expect(screen.getByText('expectations[0].min_score: Score must be between 0 and 1.'))
      .toBeInTheDocument();
    expect(screen.queryByText(/discard this malformed entry/)).not.toBeInTheDocument();
    expect(screen.queryByText(/input.metadata.secret/)).not.toBeInTheDocument();
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Hello from dialog');
  });

  it('keeps the Form open when malformed input JSON blocks the YAML transition', () => {
    const malformed = {
      ...persistedTest,
      inputSpecJson: '{"messages": [',
    };
    renderDialog(malformed);

    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));

    expect(screen.getByText(/Repair the existing JSON fields before opening YAML/))
      .toBeInTheDocument();
    expect(screen.getByLabelText('Repair input JSON')).toHaveValue('{"messages": [');
    expect(screen.queryByLabelText('Test YAML')).not.toBeInTheDocument();
  });

  it('keeps the Form open when malformed expectations JSON blocks the YAML transition', () => {
    const malformed = {
      ...persistedTest,
      expectationsJson: 'not-json',
    };
    renderDialog(malformed);

    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));

    expect(screen.getByText(/Repair the existing JSON fields before opening YAML/))
      .toBeInTheDocument();
    expect(screen.getByLabelText('Repair expectations JSON')).toHaveValue('not-json');
    expect(screen.queryByLabelText('Test YAML')).not.toBeInTheDocument();
  });

  it('keeps invalid legacy JSON visible and blocks save without an empty fallback', () => {
    const malformed = {
      ...persistedTest,
      inputSpecJson: '{"messages": [',
      expectationsJson: 'not-json',
    };
    const callbacks = renderDialog(malformed);

    expect(screen.getByText(/Existing input JSON needs repair/)).toBeInTheDocument();
    expect(screen.getByText(/Existing expectations JSON needs repair/)).toBeInTheDocument();
    expect(screen.getByLabelText('Repair input JSON')).toHaveValue('{"messages": [');
    expect(screen.getByLabelText('Repair expectations JSON')).toHaveValue('not-json');
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
    expect(callbacks.onClose).not.toHaveBeenCalled();
  });

  it('repairs malformed legacy JSON in place and preserves its metadata', async () => {
    const malformed = {
      ...persistedTest,
      inputSpecJson: '{"temperature":0.25,"messages": [',
      expectationsJson: 'not-json',
    };
    const saved = { ...persistedTest, id: 'repaired' };
    vi.mocked(testsApi.update).mockResolvedValue(saved);
    renderDialog(malformed);

    fireEvent.change(screen.getByLabelText('Repair input JSON'), {
      target: {
        value: '{"temperature":0.25,"messages":[{"role":"user","content":"Repaired"}]}',
      },
    });
    fireEvent.change(screen.getByLabelText('Repair expectations JSON'), {
      target: {
        value: '[{"type":"contains_text","text":"Repaired","case_insensitive":false}]',
      },
    });
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Repaired');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(testsApi.update).toHaveBeenCalledOnce());
    const [, request] = vi.mocked(testsApi.update).mock.calls[0] ?? [];
    expect(request?.inputSpecJson).toBe(
      '{"temperature":0.25,"messages":[{"role":"user","content":"Repaired"}]}',
    );
    expect(request?.expectationsJson).toBe(
      '[{"type":"contains_text","text":"Repaired","case_insensitive":false}]',
    );
  });

  it('keeps the valid form unchanged when leaving malformed YAML', () => {
    renderDialog(persistedTest);
    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));
    const yaml = screen.getByLabelText('Test YAML');
    expect((yaml as HTMLTextAreaElement).value).toContain('- id: case-1');
    fireEvent.change(yaml, { target: { value: '- id: [broken' } });
    fireEvent.click(screen.getByRole('tab', { name: 'Form' }));

    expect(screen.getByText(/Repair the YAML errors before returning to Form/)).toBeInTheDocument();
    expect(screen.getByLabelText('Test YAML')).toHaveValue('- id: [broken');
    expect(screen.queryByLabelText('Content for message 1')).not.toBeInTheDocument();
  });

  it('allows an incomplete new draft to open YAML while keeping save validation active', () => {
    renderDialog();
    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));

    expect((screen.getByLabelText('Test YAML') as HTMLTextAreaElement).value).toContain('- id:');
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(screen.getByText(/Repair the YAML errors before saving this test/)).toBeInTheDocument();
    expect(screen.getByText(/rows\[0\]\.id: Test id is required/)).toBeInTheDocument();
    expect(testsApi.create).not.toHaveBeenCalled();
  });

  it('rejects malformed YAML when saving directly from YAML mode', () => {
    renderDialog(persistedTest);
    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));
    fireEvent.change(screen.getByLabelText('Test YAML'), { target: { value: '- id: [broken' } });

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(screen.getByText(/Repair the YAML errors before saving this test/)).toBeInTheDocument();
    expect(screen.getByLabelText('Test YAML')).toHaveValue('- id: [broken');
    expect(testsApi.update).not.toHaveBeenCalled();
  });

  it('saves a valid YAML draft without requiring a return to Form mode', async () => {
    const saved = { ...persistedTest, name: 'Saved from YAML' };
    vi.mocked(testsApi.update).mockResolvedValue(saved);
    const callbacks = renderDialog(persistedTest);
    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));
    fireEvent.change(screen.getByLabelText('Test YAML'), {
      target: { value: allTypesYaml.replace('All expectation types', 'Saved from YAML') },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(testsApi.update).toHaveBeenCalledOnce());
    const [, request] = vi.mocked(testsApi.update).mock.calls[0] ?? [];
    expect(request?.name).toBe('Saved from YAML');
    expect(callbacks.onSaved).toHaveBeenCalledWith(saved);
    expect(callbacks.onClose).toHaveBeenCalledOnce();
  });

  it('rejects an aggregate expectation payload that crosses the wire limit', () => {
    const storedExpectations = Array.from({ length: 16 }, () => ({
      type: 'llm_judge',
      rubric: 'Helpful',
      min_score: 0.8,
      model: 'm'.repeat(15_000),
      provider: 'p',
    }));
    const storedExpectationsJson = JSON.stringify(storedExpectations);
    expect(new TextEncoder().encode(storedExpectationsJson).byteLength).toBeLessThan(262_144);
    renderDialog({ ...persistedTest, expectationsJson: storedExpectationsJson });

    for (let index = 1; index <= storedExpectations.length; index += 1) {
      fireEvent.change(screen.getByLabelText(`Rubric for expectation ${index}`), {
        target: { value: 'r'.repeat(2_000) },
      });
    }

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(screen.getByText(/Fix the highlighted expectation fields before saving this test/))
      .toBeInTheDocument();
    expect(testsApi.update).not.toHaveBeenCalled();
  });

  it('ignores a completed save after the suite changes', async () => {
    let resolveCreate: ((testCase: TestCase) => void) | undefined;
    vi.mocked(testsApi.create).mockReturnValue(new Promise<TestCase>((resolve) => {
      resolveCreate = resolve;
    }));
    const onClose = vi.fn();
    const onSaved = vi.fn();
    const view = render(
      <TestCaseDialog
        open
        suiteId="suite-1"
        testCase={null}
        onClose={onClose}
        onSaved={onSaved}
      />,
    );
    fillMinimumDraft();
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    view.rerender(
      <TestCaseDialog
        open
        suiteId="suite-2"
        testCase={null}
        onClose={onClose}
        onSaved={onSaved}
      />,
    );
    resolveCreate?.({ ...persistedTest, suiteId: 'suite-1', id: 'stale' });

    await waitFor(() => expect(testsApi.create).toHaveBeenCalledOnce());
    await waitFor(() => {
      expect(onSaved).not.toHaveBeenCalled();
      expect(onClose).not.toHaveBeenCalled();
    });
  });

  it('ignores a completed save after the dialog is closed externally', async () => {
    let resolveCreate: ((testCase: TestCase) => void) | undefined;
    vi.mocked(testsApi.create).mockReturnValue(new Promise<TestCase>((resolve) => {
      resolveCreate = resolve;
    }));
    const onClose = vi.fn();
    const onSaved = vi.fn();
    const view = render(
      <TestCaseDialog
        open
        suiteId="suite-1"
        testCase={null}
        onClose={onClose}
        onSaved={onSaved}
      />,
    );
    fillMinimumDraft();
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    view.rerender(
      <TestCaseDialog
        open={false}
        suiteId="suite-1"
        testCase={null}
        onClose={onClose}
        onSaved={onSaved}
      />,
    );
    resolveCreate?.({ ...persistedTest, id: 'stale' });

    await waitFor(() => {
      expect(onSaved).not.toHaveBeenCalled();
      expect(onClose).not.toHaveBeenCalled();
    });
  });
});
