import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { environmentsApi } from '../api/environments';
import { projectsApi } from '../api/projects';
import { suitesApi } from '../api/suites';
import { renderAtRoute } from '../test/render-route';
import { ProjectDetail } from './ProjectDetail';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async (importOriginal) => ({
  ...await importOriginal<typeof import('react-router-dom')>(),
  useNavigate: () => navigate,
}));
vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../api/projects', () => ({ projectsApi: { getById: vi.fn() } }));
vi.mock('../api/environments', () => ({ environmentsApi: { getByProject: vi.fn() } }));
vi.mock('../api/suites', () => ({ suitesApi: { getByProject: vi.fn() } }));

const project = {
  id: 'project-1',
  name: 'Evaluation API',
  description: 'Project description',
  createdAt: '2026-08-12T00:00:00Z',
  updatedAt: '2026-08-12T00:00:00Z',
};

const renderDetail = () => renderAtRoute(
  <ProjectDetail />,
  '/projects/project-1',
  '/projects/:projectId',
);

describe('ProjectDetail', () => {
  beforeEach(() => {
    navigate.mockReset();
    vi.mocked(projectsApi.getById).mockReset();
    vi.mocked(environmentsApi.getByProject).mockReset();
    vi.mocked(suitesApi.getByProject).mockReset();
  });

  it('loads related resources and navigates through environments and suites', async () => {
    vi.mocked(projectsApi.getById).mockResolvedValue(project);
    vi.mocked(environmentsApi.getByProject).mockResolvedValue([{
      id: 'environment-1',
      projectId: 'project-1',
      name: 'Staging',
      baseUrl: 'https://staging.example.test',
      hasHeaders: false,
      createdAt: '2026-08-12T00:00:00Z',
    }]);
    vi.mocked(suitesApi.getByProject).mockResolvedValue([
      {
        id: 'suite-1', projectId: 'project-1', name: 'Smoke suite', description: 'Fast checks',
        createdAt: '2026-08-12T00:00:00Z', updatedAt: '2026-08-12T00:00:00Z', testCaseCount: 3,
      },
      {
        id: 'suite-2', projectId: 'project-1', name: 'No description suite', description: undefined,
        createdAt: '2026-08-12T00:00:00Z', updatedAt: '2026-08-12T00:00:00Z', testCaseCount: 0,
      },
    ]);
    renderDetail();

    expect(await screen.findByRole('heading', { name: 'Evaluation API' })).toBeInTheDocument();
    expect(screen.getByText('Project description')).toBeInTheDocument();
    expect(screen.getByText('https://staging.example.test')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Staging'));
    fireEvent.click(screen.getByRole('button', { name: 'Add Environment' }));
    fireEvent.click(screen.getByRole('button', { name: 'Back to Projects' }));
    expect(navigate).toHaveBeenCalledWith('/environments/environment-1');
    expect(navigate).toHaveBeenCalledWith('/projects/project-1/environments/new');
    expect(navigate).toHaveBeenCalledWith('/');

    fireEvent.click(screen.getByRole('tab', { name: 'Test Suites' }));
    expect(screen.getByText('Smoke suite')).toBeInTheDocument();
    expect(screen.getByText('Fast checks')).toBeInTheDocument();
    expect(screen.getByText('No description')).toBeInTheDocument();
    expect(screen.getByText('3 tests')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Smoke suite'));
    fireEvent.click(screen.getByRole('button', { name: 'Create Suite' }));
    expect(navigate).toHaveBeenCalledWith('/suites/suite-1');
    expect(navigate).toHaveBeenCalledWith('/projects/project-1/suites/new');
  });

  it('renders both empty states and their primary actions', async () => {
    vi.mocked(projectsApi.getById).mockResolvedValue({ ...project, description: undefined });
    vi.mocked(environmentsApi.getByProject).mockResolvedValue([]);
    vi.mocked(suitesApi.getByProject).mockResolvedValue([]);
    renderDetail();

    expect(await screen.findByText('No environments configured')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Create First Environment' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Test Suites' }));
    expect(screen.getByText('No test suites created')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Create First Suite' }));
    expect(navigate).toHaveBeenNthCalledWith(1, '/projects/project-1/environments/new');
    expect(navigate).toHaveBeenNthCalledWith(2, '/projects/project-1/suites/new');
    expect(screen.queryByText('Project description')).not.toBeInTheDocument();
  });

  it('renders the load error when project retrieval fails', async () => {
    vi.mocked(projectsApi.getById).mockRejectedValue(new Error('offline'));
    vi.mocked(environmentsApi.getByProject).mockResolvedValue([]);
    vi.mocked(suitesApi.getByProject).mockResolvedValue([]);
    renderDetail();

    expect(await screen.findByText('Failed to load project data')).toBeInTheDocument();
  });

  it('reports a missing project route parameter without querying', async () => {
    render(
      <MemoryRouter>
        <ProjectDetail />
      </MemoryRouter>,
    );

    expect(await screen.findByText('Project ID is required')).toBeInTheDocument();
    expect(projectsApi.getById).not.toHaveBeenCalled();
  });
});
