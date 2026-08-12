import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { runsApi } from '../api/runs';
import { suitesApi } from '../api/suites';
import { renderAtRoute } from '../test/render-route';
import { RunsList } from './RunsList';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async (importOriginal) => ({
  ...await importOriginal<typeof import('react-router-dom')>(),
  useNavigate: () => navigate,
}));
vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../api/runs', () => ({ runsApi: { getBySuite: vi.fn() } }));
vi.mock('../api/suites', () => ({ suitesApi: { getById: vi.fn() } }));

const suite = {
  id: 'suite-1',
  projectId: 'project-1',
  name: 'Smoke suite',
  description: 'Fast checks',
  createdAt: '2026-08-12T00:00:00Z',
  updatedAt: '2026-08-12T00:00:00Z',
  testCaseCount: 5,
};

const run = (id: string, status: string) => ({
  id,
  suiteId: 'suite-1',
  environmentId: 'environment-1',
  endpointId: 'endpoint-1',
  mappingSpecId: 'mapping-1',
  status,
  createdAt: '2026-08-12T00:00:00Z',
});

const renderList = () => renderAtRoute(
  <RunsList />,
  '/suites/suite-1/runs',
  '/suites/:suiteId/runs',
);

describe('RunsList', () => {
  beforeEach(() => {
    navigate.mockReset();
    vi.mocked(suitesApi.getById).mockReset();
    vi.mocked(runsApi.getBySuite).mockReset();
  });

  it('renders run history for every status and navigates to details', async () => {
    vi.mocked(suitesApi.getById).mockResolvedValue(suite);
    vi.mocked(runsApi.getBySuite).mockResolvedValue([
      {
        ...run('run-complete', 'Completed'),
        startedAt: '2026-08-12T01:00:00Z',
        completedAt: '2026-08-12T01:01:00Z',
        gitCommitHash: '1234567890abcdef',
      },
      run('run-running', 'Running'),
      run('run-failed', 'Failed'),
      run('run-queued', 'Queued'),
      run('run-unknown', 'Cancelled'),
    ]);
    renderList();

    expect(await screen.findByText('Smoke suite')).toBeInTheDocument();
    for (const status of ['Completed', 'Running', 'Failed', 'Queued', 'Cancelled']) {
      expect(screen.getAllByText(status).length).toBeGreaterThan(0);
    }
    expect(screen.getByText('12345678')).toBeInTheDocument();
    expect(screen.getAllByText('-').length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: 'View' })).toHaveLength(5);

    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[4]);
    fireEvent.click(screen.getByRole('button', { name: 'Back to Suite' }));
    expect(navigate).toHaveBeenNthCalledWith(1, '/runs/run-unknown');
    expect(navigate).toHaveBeenNthCalledWith(2, '/suites/suite-1');
  });

  it('renders the empty state and returns to the suite', async () => {
    vi.mocked(suitesApi.getById).mockResolvedValue(suite);
    vi.mocked(runsApi.getBySuite).mockResolvedValue([]);
    renderList();

    expect(await screen.findByText('No runs yet')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Go to Suite' }));
    expect(navigate).toHaveBeenCalledWith('/suites/suite-1');
  });

  it('surfaces and dismisses load failures without a stale suite', async () => {
    vi.mocked(suitesApi.getById).mockRejectedValue(new Error('offline'));
    vi.mocked(runsApi.getBySuite).mockResolvedValue([]);
    renderList();

    expect(await screen.findByText('Failed to load run history')).toBeInTheDocument();
    expect(screen.queryByText('Smoke suite')).not.toBeInTheDocument();
    expect(screen.getByText('No runs yet')).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Close'));
    expect(screen.queryByText('Failed to load run history')).not.toBeInTheDocument();
  });

  it('reports a missing suite route parameter without querying', async () => {
    render(
      <MemoryRouter>
        <RunsList />
      </MemoryRouter>,
    );
    expect(await screen.findByText('Suite ID is required')).toBeInTheDocument();
    expect(runsApi.getBySuite).not.toHaveBeenCalled();
  });
});
