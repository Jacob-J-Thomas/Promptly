import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { runsApi } from '../api/runs';
import { renderAtRoute } from '../test/render-route';
import { RunDetail } from './RunDetail';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async (importOriginal) => ({
  ...await importOriginal<typeof import('react-router-dom')>(),
  useNavigate: () => navigate,
}));
vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../api/runs', () => ({
  runsApi: {
    getById: vi.fn(),
    getResults: vi.fn(),
  },
}));

const completedRun = {
  id: 'run-1',
  suiteId: 'suite-1',
  environmentId: 'environment-1',
  endpointId: 'endpoint-1',
  mappingSpecId: 'mapping-1',
  status: 'Completed',
  summaryJson: JSON.stringify({
    passRate: 0.75,
    passed: 3,
    failed: 1,
    errors: 1,
    avgLatencyMs: 120,
    totalTokens: 1234,
    totalCost: 0.125,
  }),
  createdByUserId: 'Ada',
  errorMessage: 'Run-level warning',
  createdAt: '2026-08-12T00:00:00Z',
  startedAt: '2026-08-12T00:00:01Z',
  completedAt: '2026-08-12T00:01:00Z',
};

const result = (id: string, status: string) => ({
  id,
  runId: 'run-1',
  testCaseId: `test-${id}`,
  status,
  passedExpectations: 1,
  failedExpectations: 1,
  errorExpectations: 0,
  failureReasons: [],
  createdAt: '2026-08-12T00:00:00Z',
});

const results = [
  {
    ...result('pass', 'Pass'),
    testCaseName: 'Passing test',
    testCaseExternalId: 'external-pass',
    traceJson: JSON.stringify({ response: 'ok' }),
  },
  {
    ...result('fail', 'Fail'),
    testCaseName: 'Failing test',
    testCaseExternalId: 'external-fail',
    failureReasons: ['Expected output did not match'],
  },
  {
    ...result('error', 'Error'),
    testCaseName: 'Errored test',
    errorExpectations: 1,
    failureReasons: ['Endpoint unavailable'],
  },
  {
    ...result('unknown', 'Skipped'),
    testCaseExternalId: 'external-skipped',
  },
];

const renderDetail = () => renderAtRoute(
  <RunDetail />,
  '/runs/run-1',
  '/runs/:runId',
);

describe('RunDetail', () => {
  beforeEach(() => {
    navigate.mockReset();
    vi.mocked(runsApi.getById).mockReset();
    vi.mocked(runsApi.getResults).mockReset();
  });

  afterEach(() => vi.restoreAllMocks());

  it('renders completed run metadata, summary, all result statuses, and expanded details', async () => {
    vi.mocked(runsApi.getById).mockResolvedValue(completedRun);
    vi.mocked(runsApi.getResults).mockResolvedValue(results);
    renderDetail();

    expect(await screen.findByRole('heading', { name: 'Test Run' })).toBeInTheDocument();
    expect(screen.getAllByText('Completed').length).toBeGreaterThan(0);
    expect(screen.getByText('75.0%')).toBeInTheDocument();
    expect(screen.getByText('120ms')).toBeInTheDocument();
    expect(screen.getByText('1,234')).toBeInTheDocument();
    expect(screen.getByText('$0.1250')).toBeInTheDocument();
    expect(screen.getByText('Ada')).toBeInTheDocument();
    expect(screen.getByText('Run-level warning')).toBeInTheDocument();
    expect(screen.getByText('external-pass')).toBeInTheDocument();
    expect(screen.getByText('external-skipped')).toBeInTheDocument();
    expect(screen.getByText('1 / 3')).toBeInTheDocument();
    for (const status of ['Pass', 'Fail', 'Error', 'Skipped']) {
      expect(screen.getByText(status)).toBeInTheDocument();
    }

    const detailButtons = screen.getAllByRole('button', { name: 'Details' });
    fireEvent.click(detailButtons[0]);
    expect(await screen.findByText('Trace')).toBeInTheDocument();
    expect(screen.getByText(/"response": "ok"/)).toBeInTheDocument();
    fireEvent.click(detailButtons[1]);
    expect(await screen.findByText('Expected output did not match')).toBeInTheDocument();
    fireEvent.click(detailButtons[2]);
    expect(await screen.findByText('Endpoint unavailable')).toBeInTheDocument();
    fireEvent.click(detailButtons[3]);
    fireEvent.click(detailButtons[0]);
    await waitFor(() => expect(screen.queryByText('Trace')).not.toBeInTheDocument());
    fireEvent.click(screen.getAllByTestId('ExpandMoreIcon')[0].closest('button')!);
    expect(await screen.findByText('Trace')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Back to Suite' }));
    expect(navigate).toHaveBeenCalledWith('/suites/suite-1');
  });

  it('renders a minimal failed run with malformed summary and no optional result data', async () => {
    vi.mocked(runsApi.getById).mockResolvedValue({
      ...completedRun,
      status: 'Failed',
      summaryJson: '{not-json',
      createdByUserId: undefined,
      errorMessage: undefined,
      startedAt: undefined,
      completedAt: undefined,
    });
    vi.mocked(runsApi.getResults).mockResolvedValue([]);
    renderDetail();

    expect(await screen.findByText('Failed')).toBeInTheDocument();
    expect(screen.queryByText('Summary')).not.toBeInTheDocument();
    expect(screen.getByText('Test Results (0)')).toBeInTheDocument();
    expect(screen.queryByText('Triggered By')).not.toBeInTheDocument();
  });

  it('ignores structurally invalid summaries and safely renders malformed legacy trace JSON', async () => {
    vi.mocked(runsApi.getById).mockResolvedValue({
      ...completedRun,
      summaryJson: JSON.stringify({ passRate: 'invalid' }),
    });
    vi.mocked(runsApi.getResults).mockResolvedValue([{
      ...result('legacy', 'Error'),
      testCaseName: 'Legacy result',
      traceJson: '{not-json',
    }]);
    renderDetail();

    expect(await screen.findByText('Legacy result')).toBeInTheDocument();
    expect(screen.queryByText('Summary')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Details' }));
    expect(await screen.findByText('{not-json')).toBeInTheDocument();
  });

  it('keeps an active run refreshable, schedules polling, and surfaces refresh failures', async () => {
    const queuedRun = {
      ...completedRun,
      status: 'Queued',
      summaryJson: undefined,
      createdByUserId: undefined,
      errorMessage: undefined,
      startedAt: undefined,
      completedAt: undefined,
    };
    vi.mocked(runsApi.getById)
      .mockResolvedValueOnce(queuedRun)
      .mockRejectedValueOnce(new Error('offline'));
    vi.mocked(runsApi.getResults)
      .mockResolvedValueOnce([])
      .mockRejectedValueOnce(new Error('offline'));
    const setInterval = vi.spyOn(globalThis, 'setInterval');
    const clearInterval = vi.spyOn(globalThis, 'clearInterval');
    const view = renderDetail();

    expect(await screen.findByText('Queued')).toBeInTheDocument();
    await waitFor(() => expect(setInterval).toHaveBeenCalledWith(expect.any(Function), 5000));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('Failed to load run data')).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Close'));
    expect(screen.queryByText('Failed to load run data')).not.toBeInTheDocument();
    view.unmount();
    expect(clearInterval).toHaveBeenCalled();
  });

  it('invokes the polling callback while a run is active', async () => {
    const queuedRun = { ...completedRun, status: 'Running', summaryJson: undefined };
    vi.mocked(runsApi.getById).mockResolvedValue(queuedRun);
    vi.mocked(runsApi.getResults).mockResolvedValue([]);
    let poll: (() => void) | undefined;
    vi.spyOn(globalThis, 'setInterval').mockImplementation(((handler: TimerHandler, timeout?: number) => {
      if (typeof handler === 'function' && timeout === 5000) {
        poll = () => handler();
      }
      return 1;
    }) as typeof setInterval);
    renderDetail();

    expect(await screen.findByText('Running')).toBeInTheDocument();
    await waitFor(() => expect(poll).toBeDefined());
    const pollCallback = poll;
    expect(pollCallback).toBeDefined();
    await act(async () => pollCallback?.());
    await waitFor(() => expect(runsApi.getById).toHaveBeenCalledTimes(2));
  });

  it('renders the load error after an initial request failure', async () => {
    vi.mocked(runsApi.getById).mockRejectedValue(new Error('offline'));
    vi.mocked(runsApi.getResults).mockResolvedValue([]);
    renderDetail();

    expect(await screen.findByText('Failed to load run data')).toBeInTheDocument();
  });

  it('reports a missing run route parameter without querying', async () => {
    render(
      <MemoryRouter>
        <RunDetail />
      </MemoryRouter>,
    );
    expect(await screen.findByText('Run ID is required')).toBeInTheDocument();
    expect(runsApi.getById).not.toHaveBeenCalled();
  });
});
