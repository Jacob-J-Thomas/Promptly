import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { endpointsApi } from '../api/endpoints';
import { environmentsApi } from '../api/environments';
import { renderAtRoute } from '../test/render-route';
import { EnvironmentDetail } from './EnvironmentDetail';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async (importOriginal) => ({
  ...await importOriginal<typeof import('react-router-dom')>(),
  useNavigate: () => navigate,
}));
vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../components/MappingWizard', () => ({
  MappingWizard: ({
    open,
    onClose,
    onComplete,
  }: {
    open: boolean;
    onClose: () => void;
    onComplete: (endpointId: string) => void | Promise<void>;
  }) => open ? (
    <div>
      <span>Mapping wizard</span>
      <button type="button" onClick={onClose}>Close wizard</button>
      <button type="button" onClick={() => void onComplete('endpoint-1')}>Complete wizard</button>
    </div>
  ) : null,
}));
vi.mock('../api/environments', () => ({ environmentsApi: { getById: vi.fn() } }));
vi.mock('../api/endpoints', () => ({
  endpointsApi: {
    getByEnvironment: vi.fn(),
    delete: vi.fn(),
  },
}));

const environment = {
  id: 'environment-1',
  projectId: 'project-1',
  name: 'Staging',
  baseUrl: 'https://staging.example.test',
  hasHeaders: true,
  headers: { Authorization: 'Bearer token' },
  createdAt: '2026-08-12T00:00:00Z',
};

const endpoint = {
  id: 'endpoint-1',
  environmentId: 'environment-1',
  name: 'Chat completion',
  path: '/v1/chat',
  httpMethod: 'POST',
  timeoutSeconds: 30,
};

const renderDetail = () => renderAtRoute(
  <EnvironmentDetail />,
  '/environments/environment-1',
  '/environments/:environmentId',
);

describe('EnvironmentDetail', () => {
  beforeEach(() => {
    navigate.mockReset();
    vi.mocked(environmentsApi.getById).mockReset();
    vi.mocked(endpointsApi.getByEnvironment).mockReset();
    vi.mocked(endpointsApi.delete).mockReset();
  });

  afterEach(() => vi.restoreAllMocks());

  it('shows configuration, reveals headers, navigates back, and reloads after the wizard', async () => {
    vi.mocked(environmentsApi.getById).mockResolvedValue(environment);
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValue([endpoint]);
    renderDetail();

    expect(await screen.findByRole('heading', { name: 'Staging' })).toBeInTheDocument();
    expect(screen.getByText('Headers are hidden (click eye icon to view)')).toBeInTheDocument();
    expect(screen.queryByText(/Bearer token/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByTestId('VisibilityIcon').closest('button')!);
    expect(screen.getByText(/Bearer token/)).toBeInTheDocument();
    fireEvent.click(screen.getByTestId('VisibilityOffIcon').closest('button')!);
    expect(screen.getByText('Headers are hidden (click eye icon to view)')).toBeInTheDocument();

    expect(screen.getByText('Chat completion')).toBeInTheDocument();
    expect(screen.getByText('/v1/chat')).toBeInTheDocument();
    expect(screen.getByText('Timeout: 30s')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Back to Project' }));
    expect(navigate).toHaveBeenCalledWith('/projects/project-1');

    fireEvent.click(screen.getByRole('button', { name: 'Add Endpoint with Wizard' }));
    expect(screen.getByText('Mapping wizard')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Complete wizard' }));
    await waitFor(() => expect(environmentsApi.getById).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole('button', { name: 'Close wizard' }));
    expect(screen.queryByText('Mapping wizard')).not.toBeInTheDocument();
  });

  it('renders no-header and empty-endpoint states and opens the first-endpoint wizard', async () => {
    vi.mocked(environmentsApi.getById).mockResolvedValue({ ...environment, hasHeaders: false, headers: null });
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValue([]);
    renderDetail();

    expect(await screen.findByText('No custom headers configured')).toBeInTheDocument();
    expect(screen.getByText('No endpoints configured')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Create First Endpoint' }));
    expect(screen.getByText('Mapping wizard')).toBeInTheDocument();
  });

  it('honors endpoint deletion confirmation and reloads after deletion', async () => {
    vi.mocked(environmentsApi.getById).mockResolvedValue(environment);
    vi.mocked(endpointsApi.getByEnvironment)
      .mockResolvedValueOnce([endpoint])
      .mockResolvedValueOnce([]);
    vi.mocked(endpointsApi.delete).mockResolvedValue(undefined);
    vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    renderDetail();

    await screen.findByText('Chat completion');
    const deleteButton = screen.getByTestId('DeleteIcon').closest('button')!;
    fireEvent.click(deleteButton);
    expect(endpointsApi.delete).not.toHaveBeenCalled();
    fireEvent.click(deleteButton);
    await waitFor(() => expect(endpointsApi.delete).toHaveBeenCalledWith('endpoint-1'));
    expect(await screen.findByText('No endpoints configured')).toBeInTheDocument();
  });

  it('surfaces endpoint deletion failures and dismisses the alert', async () => {
    vi.mocked(environmentsApi.getById).mockResolvedValue(environment);
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValue([endpoint]);
    vi.mocked(endpointsApi.delete).mockRejectedValue(new Error('offline'));
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderDetail();

    await screen.findByText('Chat completion');
    fireEvent.click(screen.getByTestId('DeleteIcon').closest('button')!);
    expect(await screen.findByText('Failed to delete endpoint')).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Close'));
    expect(screen.queryByText('Failed to delete endpoint')).not.toBeInTheDocument();
  });

  it('renders the load error when environment retrieval fails', async () => {
    vi.mocked(environmentsApi.getById).mockRejectedValue(new Error('offline'));
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValue([]);
    renderDetail();

    expect(await screen.findByText('Failed to load environment data')).toBeInTheDocument();
  });

  it('reports a missing environment route parameter without querying', async () => {
    render(
      <MemoryRouter>
        <EnvironmentDetail />
      </MemoryRouter>,
    );
    expect(await screen.findByText('Environment ID is required')).toBeInTheDocument();
    expect(environmentsApi.getById).not.toHaveBeenCalled();
  });
});
