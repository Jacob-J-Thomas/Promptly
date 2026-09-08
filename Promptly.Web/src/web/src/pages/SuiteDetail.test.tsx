import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { AxiosError, AxiosHeaders } from 'axios';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { runsApi } from '../api/runs';
import { suitesApi, type TestSuite } from '../api/suites';
import { testsApi, type TestCase } from '../api/tests';
import { SuiteDetail } from './SuiteDetail';

const router = vi.hoisted(() => ({
  navigate: vi.fn(),
  suiteId: 'suite-1' as string | undefined,
}));

const runDialogMock = vi.hoisted(() => ({
  projectId: '',
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return {
    ...actual,
    useNavigate: () => router.navigate,
    useParams: () => ({ suiteId: router.suiteId }),
  };
});

vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: ReactNode }) => <div>{children}</div>,
}));

vi.mock('../components/RunConfigDialog', () => ({
  RunConfigDialog: ({
    open,
    onClose,
    projectId,
  }: {
    open: boolean;
    onClose: () => void;
    projectId: string;
  }) => {
    runDialogMock.projectId = projectId;
    if (!open) return null;
    return (
      <div role="dialog" aria-label="Run Test Suite">
        <input aria-label="Git Commit Hash (optional)" />
        <button type="button" onClick={onClose}>Cancel</button>
        <button type="button" disabled>Start Run</button>
      </div>
    );
  },
}));

vi.mock('../api/suites', () => ({
  suitesApi: {
    getById: vi.fn(),
  },
}));

vi.mock('../api/tests', () => ({
  testsApi: {
    getBySuite: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    importYaml: vi.fn(),
    exportYaml: vi.fn(),
  },
}));

vi.mock('../api/runs', () => ({
  runsApi: {
    queue: vi.fn(),
  },
}));

const suite: TestSuite = {
  id: 'suite-1',
  projectId: 'project-1',
  name: 'Regression Suite',
  description: 'Checks the main flows',
  createdAt: '2026-08-12T00:00:00Z',
  updatedAt: '2026-08-12T00:00:00Z',
  testCaseCount: 1,
};

const testCase: TestCase = {
  id: 'test-1',
  suiteId: 'suite-1',
  externalId: 'chat-001',
  name: 'Returns a greeting',
  description: undefined,
  inputSpecJson: '{"messages":[]}',
  expectationsJson: '[]',
  createdAt: '2026-08-12T00:00:00Z',
};

const createdTest: TestCase = {
  ...testCase,
  id: 'test-2',
  externalId: 'chat-002',
  name: 'Answers a follow-up',
  description: 'Conversation continuity',
};

const editableTest: TestCase = {
  ...testCase,
  inputSpecJson: JSON.stringify({
    messages: [{ role: 'user', content: 'Original greeting' }],
    temperature: 0.5,
    enabled: true,
    metadata: { tags: ['suite'] },
  }),
  expectationsJson: JSON.stringify([
    { type: 'contains_text', text: 'Original', case_insensitive: true },
  ]),
};

const apiError = (message: string) => new AxiosError(
  'request failed',
  'ERR_BAD_REQUEST',
  undefined,
  undefined,
  {
    data: { message },
    status: 400,
    statusText: 'Bad Request',
    headers: {},
    config: { headers: new AxiosHeaders() },
  },
);

const renderLoaded = async (
  suiteResult: TestSuite = suite,
  testResults: TestCase[] = [testCase],
) => {
  vi.mocked(suitesApi.getById).mockResolvedValue(suiteResult);
  vi.mocked(testsApi.getBySuite).mockResolvedValue(testResults);
  const rendered = render(<SuiteDetail />);
  await screen.findByRole('heading', { name: suiteResult.name });
  return rendered;
};

const openTestMenu = async () => {
  const menuButton = screen.getByTestId('MoreVertIcon').closest('button');
  expect(menuButton).not.toBeNull();
  fireEvent.click(menuButton!);
  await screen.findByRole('menuitem', { name: 'Edit' });
};

const fillMinimumTestDraft = () => {
  fireEvent.change(screen.getByLabelText(/External ID/), {
    target: { value: 'chat-002' },
  });
  fireEvent.change(screen.getByLabelText(/^Name/), {
    target: { value: 'Answers a follow-up' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Add message' }));
  const message = screen.getByRole('group', { name: 'Message 1' });
  fireEvent.mouseDown(within(message).getByRole('combobox'));
  fireEvent.click(screen.getByRole('option', { name: 'user' }));
  fireEvent.change(screen.getByLabelText('Content for message 1'), {
    target: { value: 'Hello from the editor' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Add expectation' }));
  fireEvent.click(screen.getByRole('menuitem', { name: 'Contains text' }));
  fireEvent.change(screen.getByLabelText('Text for expectation 1'), {
    target: { value: 'Hello from the editor' },
  });
};

const selectImportFile = () => {
  const fileInput = document.querySelector<HTMLInputElement>('input[type="file"]');
  expect(fileInput).not.toBeNull();
  const file = new File(['tests:\n  - id: chat-002'], 'tests.yaml', {
    type: 'text/yaml',
  });
  fireEvent.change(fileInput!, { target: { files: [file] } });
  return file;
};

describe('SuiteDetail', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    router.suiteId = 'suite-1';
    vi.mocked(suitesApi.getById).mockResolvedValue(suite);
    vi.mocked(testsApi.getBySuite).mockResolvedValue([testCase]);
    vi.mocked(testsApi.create).mockResolvedValue(createdTest);
    vi.mocked(testsApi.update).mockResolvedValue(createdTest);
    vi.mocked(testsApi.delete).mockResolvedValue(undefined);
    vi.mocked(testsApi.importYaml).mockResolvedValue(undefined);
    vi.mocked(testsApi.exportYaml).mockResolvedValue('tests: []');
    vi.mocked(runsApi.queue).mockResolvedValue({
      id: 'run-1',
      suiteId: 'suite-1',
      environmentId: 'environment-1',
      endpointId: 'endpoint-1',
      mappingSpecId: 'mapping-1',
      status: 'queued',
      createdAt: '2026-08-12T00:00:00Z',
    });
  });

  it('loads suite details, navigates, and opens and closes run configuration', async () => {
    let resolveSuite: ((value: TestSuite) => void) | undefined;
    vi.mocked(suitesApi.getById).mockImplementationOnce(() => new Promise((resolve) => {
      resolveSuite = resolve;
    }));
    render(<SuiteDetail />);

    expect(screen.getByRole('progressbar')).toBeInTheDocument();
    await act(async () => resolveSuite?.(suite));

    expect(await screen.findByRole('heading', { name: 'Regression Suite' })).toBeInTheDocument();
    expect(screen.getByText('Checks the main flows')).toBeInTheDocument();
    expect(screen.getByText('chat-001')).toBeInTheDocument();
    expect(screen.getByText('Returns a greeting')).toBeInTheDocument();
    expect(screen.getByText('-')).toBeInTheDocument();

    const backButton = screen.getByTestId('ArrowBackIcon').closest('button');
    expect(backButton).not.toBeNull();
    fireEvent.click(backButton!);
    fireEvent.click(screen.getByRole('button', { name: 'View Run History' }));
    expect(router.navigate).toHaveBeenNthCalledWith(1, -1);
    expect(router.navigate).toHaveBeenNthCalledWith(2, '/suites/suite-1/runs');

    fireEvent.click(screen.getByRole('button', { name: 'Run Suite' }));
    expect(screen.getByRole('dialog', { name: 'Run Test Suite' })).toBeInTheDocument();
    expect(runDialogMock.projectId).toBe('project-1');
    fireEvent.change(screen.getByLabelText('Git Commit Hash (optional)'), {
      target: { value: 'abc123' },
    });
    expect(screen.getByRole('button', { name: 'Start Run' })).toBeDisabled();
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Run Test Suite' }))
      .getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Run Test Suite' }))
      .not.toBeInTheDocument());
  });

  it('reports a missing suite id without requesting data', async () => {
    router.suiteId = undefined;
    render(<SuiteDetail />);

    expect(await screen.findByText('Suite ID is required')).toBeInTheDocument();
    expect(suitesApi.getById).not.toHaveBeenCalled();
    expect(testsApi.getBySuite).not.toHaveBeenCalled();
  });

  it('shows a server message when loading fails', async () => {
    vi.mocked(suitesApi.getById).mockRejectedValueOnce(apiError('Suite no longer exists'));
    render(<SuiteDetail />);

    expect(await screen.findByText('Suite no longer exists')).toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('shows the empty state and creates a test with the entered values', async () => {
    const suiteWithoutMetadata: TestSuite = {
      ...suite,
      description: undefined,
      createdAt: '',
      testCaseCount: 0,
    };
    await renderLoaded(suiteWithoutMetadata, []);

    expect(screen.getByText('No test cases yet')).toBeInTheDocument();
    expect(screen.getByText('-')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run Suite' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Export' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Create First Test' }));
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled();
    fillMinimumTestDraft();
    fireEvent.change(screen.getByLabelText('Description'), {
      target: { value: 'Conversation continuity' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(testsApi.create).toHaveBeenCalledWith('suite-1', {
      externalId: 'chat-002',
      name: 'Answers a follow-up',
      description: 'Conversation continuity',
      inputSpecJson: '{"messages":[{"role":"user","content":"Hello from the editor"}]}',
      expectationsJson: '[{"type":"contains_text","text":"Hello from the editor","case_insensitive":true}]',
    }));
    await waitFor(() => expect(suitesApi.getById).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Create Test Case' }))
      .not.toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Create Test' }));
    expect(screen.getByLabelText(/External ID/)).toHaveValue('');
    expect(screen.getByLabelText(/^Name/)).toHaveValue('');
    expect(screen.getByLabelText('Description')).toHaveValue('');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Create Test Case' }))
      .not.toBeInTheDocument());
  });

  it('shows a create failure, preserves retry input, and resets it on close', async () => {
    vi.mocked(testsApi.create).mockRejectedValueOnce(apiError('Test creation is unavailable'));
    await renderLoaded();

    fireEvent.click(screen.getByRole('button', { name: 'Create Test' }));
    fillMinimumTestDraft();
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('Test creation is unavailable')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Create Test Case' })).toBeInTheDocument();
    expect(screen.getByLabelText(/External ID/)).toHaveValue('chat-002');
    expect(screen.getByRole('button', { name: 'Create' })).toBeEnabled();

    fireEvent.click(within(screen.getByRole('alert'))
      .getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Create Test Case' }))
      .not.toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Create Test' }));
    expect(screen.queryByText('Test creation is unavailable')).not.toBeInTheDocument();
    expect(screen.getByLabelText(/External ID/)).toHaveValue('');
  });

  it('opens a populated edit dialog and persists a changed test through PUT', async () => {
    vi.mocked(testsApi.getBySuite).mockResolvedValue([editableTest]);
    vi.mocked(testsApi.update).mockResolvedValue({ ...editableTest, name: 'Edited test' });
    await renderLoaded(suite, [editableTest]);
    await openTestMenu();

    expect(screen.getByRole('menuitem', { name: 'Edit' })).toBeEnabled();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Edit' }));
    expect(await screen.findByRole('dialog', { name: 'Edit Test Case' })).toBeInTheDocument();
    expect(screen.getByLabelText(/^Name/)).toHaveValue('Returns a greeting');
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('Original greeting');
    expect(screen.getByLabelText('Text for expectation 1')).toHaveValue('Original');

    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Edited test' } });
    fireEvent.change(screen.getByLabelText('Content for message 1'), {
      target: { value: 'Changed greeting' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(testsApi.update).toHaveBeenCalledWith('test-1', expect.objectContaining({
      externalId: 'chat-001',
      name: 'Edited test',
      inputSpecJson: expect.stringContaining('Changed greeting'),
      expectationsJson: editableTest.expectationsJson,
    })));
    await waitFor(() => expect(suitesApi.getById).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole('dialog', { name: 'Edit Test Case' })).not.toBeInTheDocument();
  });

  it('deletes a selected test and refreshes suite data', async () => {
    await renderLoaded();
    await openTestMenu();

    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }));

    await waitFor(() => expect(testsApi.delete).toHaveBeenCalledWith('test-1'));
    await waitFor(() => expect(suitesApi.getById).toHaveBeenCalledTimes(2));
  });

  it('shows an API message when deleting a test fails', async () => {
    vi.mocked(testsApi.delete).mockRejectedValueOnce(apiError('Test is referenced by a run'));
    await renderLoaded();
    await openTestMenu();

    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }));

    expect(await screen.findByText('Test is referenced by a run')).toBeInTheDocument();
  });

  it('exports the suite YAML through a temporary download link', async () => {
    const createObjectURL = vi.fn(() => 'blob:suite-yaml');
    const revokeObjectURL = vi.fn();
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    Object.defineProperty(window.URL, 'createObjectURL', {
      configurable: true,
      value: createObjectURL,
    });
    Object.defineProperty(window.URL, 'revokeObjectURL', {
      configurable: true,
      value: revokeObjectURL,
    });
    await renderLoaded();

    fireEvent.click(screen.getByRole('button', { name: 'Export' }));

    await waitFor(() => expect(testsApi.exportYaml).toHaveBeenCalledWith('suite-1'));
    expect(createObjectURL).toHaveBeenCalledWith(expect.any(Blob));
    expect(anchorClick).toHaveBeenCalledOnce();
    const anchor = anchorClick.mock.instances[0] as HTMLAnchorElement;
    expect(anchor.href).toBe('blob:suite-yaml');
    expect(anchor.download).toBe('Regression Suite.yaml');
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:suite-yaml');
    anchorClick.mockRestore();
  });

  it('shows an API message when export fails', async () => {
    vi.mocked(testsApi.exportYaml).mockRejectedValueOnce(apiError('Export is unavailable'));
    await renderLoaded();

    fireEvent.click(screen.getByRole('button', { name: 'Export' }));

    expect(await screen.findByText('Export is unavailable')).toBeInTheDocument();
  });

  it('imports a selected YAML file, refreshes the suite, and resets the dialog', async () => {
    await renderLoaded();
    fireEvent.click(screen.getByRole('button', { name: 'Import' }));
    expect(screen.getByRole('button', { name: 'Import' })).toBeDisabled();

    const fileInput = document.querySelector<HTMLInputElement>('input[type="file"]');
    expect(fileInput).not.toBeNull();
    fireEvent.change(fileInput!, { target: { files: [] } });
    expect(screen.getByRole('button', { name: 'Import' })).toBeDisabled();
    const file = selectImportFile();

    vi.useFakeTimers();
    try {
      await act(async () => {
        fireEvent.click(screen.getByRole('button', { name: 'Import' }));
        await Promise.resolve();
      });

      expect(testsApi.importYaml).toHaveBeenCalledWith('suite-1', file);
      expect(suitesApi.getById).toHaveBeenCalledTimes(2);

      await act(async () => vi.advanceTimersByTime(1500));
      await act(async () => vi.runOnlyPendingTimers());
      expect(screen.queryByRole('dialog', { name: 'Import Tests from YAML' }))
        .not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }

    fireEvent.click(screen.getByRole('button', { name: 'Import' }));
    expect(screen.getByRole('button', { name: 'Import' })).toBeDisabled();
  });

  it('keeps the import dialog open with an API error so the user can retry', async () => {
    vi.mocked(testsApi.importYaml).mockRejectedValueOnce(apiError('The YAML is invalid'));
    await renderLoaded();
    fireEvent.click(screen.getByRole('button', { name: 'Import' }));
    selectImportFile();

    fireEvent.click(screen.getByRole('button', { name: 'Import' }));

    expect(await screen.findByText('The YAML is invalid')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Import Tests from YAML' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Import' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Import Tests from YAML' }))
      .not.toBeInTheDocument());
  });
});
