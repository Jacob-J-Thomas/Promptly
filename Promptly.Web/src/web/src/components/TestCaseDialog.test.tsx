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
  fireEvent.change(screen.getByLabelText('External ID'), { target: { value: 'case-new' } });
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'New test' } });
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

  it('authors all eight expectation types and preserves ordered message edits', async () => {
    const saved = { ...persistedTest, id: 'created-all-types' };
    vi.mocked(testsApi.create).mockResolvedValue(saved);
    renderDialog();
    fireEvent.change(screen.getByLabelText('External ID'), { target: { value: 'all-types' } });
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'All expectation types' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add message' }));
    fireEvent.change(screen.getByLabelText('Content for message 1'), {
      target: { value: 'system guidance' },
    });
    fireEvent.mouseDown(within(screen.getByRole('group', { name: 'Message 1' })).getByRole('combobox'));
    fireEvent.click(screen.getByRole('option', { name: 'system' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add message' }));
    fireEvent.change(screen.getByLabelText('Content for message 2'), {
      target: { value: 'user prompt\nwith two lines' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Move message 2 up' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move message 1 down' }));
    fireEvent.click(within(screen.getByRole('group', { name: 'Message 1' }))
      .getByRole('button', { name: 'Delete message' }));

    const addExpectation = (label: string) => {
      fireEvent.click(screen.getByRole('button', { name: 'Add expectation' }));
      fireEvent.click(screen.getByRole('menuitem', { name: label }));
    };
    addExpectation('Contains text');
    fireEvent.change(screen.getByLabelText('Text for expectation 1'), { target: { value: 'contains' } });
    addExpectation('Banned text');
    fireEvent.change(screen.getByLabelText('Text for expectation 2'), { target: { value: 'banned' } });
    addExpectation('Regex match');
    fireEvent.change(screen.getByLabelText('Pattern for expectation 3'), { target: { value: '^answer$' } });
    addExpectation('Link pattern');
    fireEvent.change(screen.getByLabelText('Link pattern for expectation 4'), {
      target: { value: 'https://example.invalid/*' },
    });
    addExpectation('Tool called');
    fireEvent.change(screen.getByLabelText('Tool name for expectation 5'), { target: { value: 'search' } });
    addExpectation('Tool sequence');
    fireEvent.change(screen.getByLabelText('Tool 1 for expectation 6'), { target: { value: 'search' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add tool to expectation 6' }));
    fireEvent.change(screen.getByLabelText('Tool 2 for expectation 6'), { target: { value: 'summarize' } });
    fireEvent.click(screen.getByLabelText('Exact sequence for expectation 6'));
    addExpectation('LLM judge');
    fireEvent.change(screen.getByLabelText('Rubric for expectation 7'), { target: { value: 'Helpful' } });
    fireEvent.change(screen.getByLabelText('Minimum score for expectation 7'), { target: { value: '0' } });
    addExpectation('Groundedness');
    fireEvent.change(screen.getByLabelText('Minimum score for expectation 8'), { target: { value: '1' } });

    vi.mocked(testsApi.create).mockResolvedValue(saved);
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    await waitFor(() => expect(testsApi.create).toHaveBeenCalledOnce());

    const [, request] = vi.mocked(testsApi.create).mock.calls[0] ?? [];
    expect(JSON.parse(request?.inputSpecJson ?? '{}')).toEqual({
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

    expect(screen.getByLabelText('External ID')).toHaveValue('case-1');
    expect(screen.getByLabelText('Name')).toHaveValue('Greeting');
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Hello');
    expect(screen.getByLabelText('Rubric for expectation 1')).toHaveValue('Helpful');
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Edited' } });
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
    expect(screen.getByLabelText('Name')).toHaveValue('New test');
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Hello from dialog');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeEnabled();
    expect(screen.queryByText('database secret should not render')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
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
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Hello from dialog');
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
    expect(yaml).toHaveValue(expect.stringContaining('- id: case-1'));
    fireEvent.change(yaml, { target: { value: '- id: [broken' } });
    fireEvent.click(screen.getByRole('tab', { name: 'Form' }));

    expect(screen.getByText(/Repair the YAML errors before returning to Form/)).toBeInTheDocument();
    expect(screen.getByLabelText('Test YAML')).toHaveValue('- id: [broken');
    expect(screen.queryByLabelText('Content for message 1')).not.toBeInTheDocument();
  });

  it('allows an incomplete new draft to open YAML while keeping save validation active', () => {
    renderDialog();
    fireEvent.click(screen.getByRole('tab', { name: 'YAML' }));

    expect(screen.getByLabelText('Test YAML')).toHaveValue(expect.stringContaining('- id:'));
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(screen.getByText(/Repair the YAML errors before saving this test/)).toBeInTheDocument();
    expect(screen.getByText(/rows\[0\]\.id: Test id is required/)).toBeInTheDocument();
    expect(testsApi.create).not.toHaveBeenCalled();
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
