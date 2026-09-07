import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AxiosError, AxiosHeaders } from 'axios';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { endpointsApi, type Endpoint } from '../api/endpoints';
import { environmentsApi, type Environment } from '../api/environments';
import { mappingApi, type MappingSpec } from '../api/mapping';
import { runsApi } from '../api/runs';
import { RunConfigDialog } from './RunConfigDialog';

vi.mock('../api/environments', () => ({
  environmentsApi: {
    getByProject: vi.fn(),
  },
}));

vi.mock('../api/endpoints', () => ({
  endpointsApi: {
    getByEnvironment: vi.fn(),
  },
}));

vi.mock('../api/mapping', () => ({
  mappingApi: {
    getByEndpoint: vi.fn(),
  },
}));

vi.mock('../api/runs', () => ({
  runsApi: {
    queue: vi.fn(),
  },
}));

const environmentOne: Environment = {
  id: 'environment-1',
  projectId: 'project-1',
  name: 'Development',
  baseUrl: 'https://api.example.invalid',
  hasHeaders: false,
  createdAt: '2026-08-12T00:00:00Z',
};

const environmentTwo: Environment = {
  ...environmentOne,
  id: 'environment-2',
  name: 'Production',
};

const endpointOne: Endpoint = {
  id: 'endpoint-1',
  environmentId: environmentOne.id,
  name: 'Chat endpoint',
  path: '/chat',
  httpMethod: 'POST',
  timeoutSeconds: 30,
};

const endpointTwo: Endpoint = {
  ...endpointOne,
  id: 'endpoint-2',
  environmentId: environmentTwo.id,
  name: 'Production chat',
};

const mappingOne: MappingSpec = {
  id: 'mapping-1',
  endpointId: endpointOne.id,
  name: 'Development mapping',
  specJson: '{"version":1}',
  isDefault: false,
  createdAt: '2026-08-12T00:00:00Z',
  updatedAt: '2026-08-12T00:00:00Z',
};

const mappingTwo: MappingSpec = {
  ...mappingOne,
  id: 'mapping-2',
  endpointId: endpointTwo.id,
  name: 'Production default',
  isDefault: true,
};

const queuedRun = {
  id: 'run-1',
  suiteId: 'suite-1',
  environmentId: environmentOne.id,
  endpointId: endpointOne.id,
  mappingSpecId: mappingOne.id,
  status: 'Queued',
  createdAt: '2026-08-12T00:00:00Z',
};

const apiError = (message: string) => new AxiosError(
  'request failed',
  'ERR_BAD_REQUEST',
  undefined,
  undefined,
  {
    data: { message },
    status: 503,
    statusText: 'Service Unavailable',
    headers: {},
    config: { headers: new AxiosHeaders() },
  },
);

const renderDialog = () => {
  const onClose = vi.fn();
  const onRunStarted = vi.fn();
  const rendered = render(
    <MemoryRouter>
      <RunConfigDialog
        open
        onClose={onClose}
        projectId="project-1"
        suiteId="suite-1"
        onRunStarted={onRunStarted}
      />
    </MemoryRouter>,
  );
  return { ...rendered, onClose, onRunStarted };
};

const choose = async (label: string, optionName: string | RegExp) => {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: label }));
  fireEvent.click(await screen.findByRole('option', { name: optionName }));
};

const waitForReadySelection = async () => {
  await waitFor(() => {
    expect(screen.getByRole('combobox', { name: 'Environment' })).toHaveTextContent('Development');
    expect(screen.getByRole('combobox', { name: 'Endpoint' })).toHaveTextContent('Chat endpoint');
    expect(screen.getByRole('combobox', { name: 'Mapping Spec' })).toHaveTextContent('Development mapping');
  });
};

describe('RunConfigDialog', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(environmentsApi.getByProject).mockResolvedValue([environmentOne]);
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValue([endpointOne]);
    vi.mocked(mappingApi.getByEndpoint).mockResolvedValue([mappingOne]);
    vi.mocked(runsApi.queue).mockResolvedValue(queuedRun);
  });

  it('loads the cascading resources, applies defaults, and enables Start Run', async () => {
    renderDialog();

    await waitForReadySelection();

    expect(environmentsApi.getByProject).toHaveBeenCalledWith('project-1');
    expect(endpointsApi.getByEnvironment).toHaveBeenCalledWith(environmentOne.id);
    expect(mappingApi.getByEndpoint).toHaveBeenCalledWith(endpointOne.id);
    expect(screen.getByRole('button', { name: 'Start Run' })).toBeEnabled();
  });

  it('prefers the endpoint default mapping over the first mapping', async () => {
    vi.mocked(mappingApi.getByEndpoint).mockResolvedValue([
      mappingOne,
      { ...mappingOne, id: 'mapping-default', name: 'Preferred mapping', isDefault: true },
    ]);
    renderDialog();

    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Mapping Spec' }))
      .toHaveTextContent('Preferred mapping'));
  });

  it('filters resource responses to the active project resource graph', async () => {
    const unrelatedEnvironment = { ...environmentOne, id: 'other-environment', projectId: 'other-project' };
    const unrelatedEndpoint = { ...endpointOne, id: 'other-endpoint', environmentId: unrelatedEnvironment.id };
    const unrelatedMapping = { ...mappingOne, id: 'other-mapping', endpointId: unrelatedEndpoint.id };
    vi.mocked(environmentsApi.getByProject).mockResolvedValue([unrelatedEnvironment, environmentOne]);
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValue([unrelatedEndpoint, endpointOne]);
    vi.mocked(mappingApi.getByEndpoint).mockResolvedValue([unrelatedMapping, mappingOne]);
    const { onRunStarted } = renderDialog();

    await waitForReadySelection();
    expect(screen.queryByText('other-environment')).not.toBeInTheDocument();
    expect(screen.queryByText(/other-endpoint/)).not.toBeInTheDocument();
    expect(screen.queryByText(/other-mapping/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Start Run' }));
    await waitFor(() => expect(runsApi.queue).toHaveBeenCalledWith({
      suiteId: 'suite-1',
      environmentId: environmentOne.id,
      endpointId: endpointOne.id,
      mappingSpecId: mappingOne.id,
      gitCommitHash: undefined,
    }));
    expect(onRunStarted).toHaveBeenCalledWith('run-1');
  });

  it('clears the old dialog context before loading a changed project and suite', async () => {
    let resolveProjectTwo: ((value: Environment[]) => void) | undefined;
    const projectTwoEnvironment: Environment = {
      ...environmentTwo,
      projectId: 'project-2',
      name: 'Project Two',
    };
    vi.mocked(environmentsApi.getByProject).mockImplementation((projectId) => {
      if (projectId === 'project-2') {
        return new Promise((resolve) => { resolveProjectTwo = resolve; });
      }
      return Promise.resolve([environmentOne]);
    });
    vi.mocked(endpointsApi.getByEnvironment).mockImplementation((environmentId) => (
      Promise.resolve(environmentId === endpointTwo.environmentId ? [endpointTwo] : [endpointOne])
    ));
    const { rerender } = renderDialog();
    await waitForReadySelection();

    rerender(
      <RunConfigDialog
        open
        onClose={vi.fn()}
        projectId="project-2"
        suiteId="suite-2"
        onRunStarted={vi.fn()}
      />,
    );
    expect(screen.getByRole('combobox', { name: 'Environment' })).not.toHaveTextContent('Development');
    expect(screen.getByRole('combobox', { name: 'Endpoint' })).not.toHaveTextContent('Chat endpoint');
    resolveProjectTwo?.([projectTwoEnvironment]);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Environment' }))
      .toHaveTextContent('Project Two'));
  });

  it('clears dependent selections and ignores stale endpoint responses', async () => {
    let resolveDevelopment: ((value: Endpoint[]) => void) | undefined;
    vi.mocked(environmentsApi.getByProject).mockResolvedValue([environmentOne, environmentTwo]);
    vi.mocked(endpointsApi.getByEnvironment).mockImplementation((environmentId) => {
      if (environmentId === environmentOne.id) {
        return new Promise((resolve) => { resolveDevelopment = resolve; });
      }
      return Promise.resolve([endpointTwo]);
    });
    vi.mocked(mappingApi.getByEndpoint).mockResolvedValue([mappingTwo]);
    renderDialog();

    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Environment' }))
      .toBeEnabled());
    await choose('Environment', 'Development');
    await waitFor(() => expect(endpointsApi.getByEnvironment).toHaveBeenCalledWith(environmentOne.id));
    await choose('Environment', 'Production');
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Endpoint' }))
      .toHaveTextContent('Production chat'));

    resolveDevelopment?.([endpointOne]);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Endpoint' }))
      .toHaveTextContent('Production chat'));
    expect(mappingApi.getByEndpoint).toHaveBeenCalledWith(endpointTwo.id);
  });

  it('clears mapping immediately and ignores stale mapping responses', async () => {
    let resolveDevelopmentMapping: ((value: MappingSpec[]) => void) | undefined;
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValue([endpointOne, endpointTwo]);
    vi.mocked(mappingApi.getByEndpoint).mockImplementation((endpointId) => {
      if (endpointId === endpointOne.id) {
        return new Promise((resolve) => { resolveDevelopmentMapping = resolve; });
      }
      return Promise.resolve([mappingTwo]);
    });
    renderDialog();

    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Endpoint' }))
      .toHaveTextContent('Chat endpoint'));
    await choose('Endpoint', /Production chat/);
    expect(screen.getByRole('combobox', { name: 'Mapping Spec' })).not.toHaveTextContent('Production default');
    resolveDevelopmentMapping?.([mappingOne]);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Mapping Spec' }))
      .toHaveTextContent('Production default'));
  });

  it('shows an environment failure and retries the request', async () => {
    vi.mocked(environmentsApi.getByProject)
      .mockRejectedValueOnce(apiError('Environment service is unavailable'))
      .mockResolvedValueOnce([environmentOne]);
    renderDialog();

    expect(await screen.findByText('Environment service is unavailable')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitForReadySelection();
    expect(environmentsApi.getByProject).toHaveBeenCalledTimes(2);
  });

  it('shows an endpoint failure and retries the selected environment', async () => {
    vi.mocked(endpointsApi.getByEnvironment)
      .mockRejectedValueOnce(apiError('Endpoint service is unavailable'))
      .mockResolvedValueOnce([endpointOne]);
    renderDialog();

    expect(await screen.findByText('Endpoint service is unavailable')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitForReadySelection();
    expect(endpointsApi.getByEnvironment).toHaveBeenCalledTimes(2);
  });

  it('shows a mapping failure and retries the selected endpoint', async () => {
    vi.mocked(mappingApi.getByEndpoint)
      .mockRejectedValueOnce(apiError('Mapping service is unavailable'))
      .mockResolvedValueOnce([mappingOne]);
    renderDialog();

    expect(await screen.findByText('Mapping service is unavailable')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitForReadySelection();
    expect(mappingApi.getByEndpoint).toHaveBeenCalledTimes(2);
  });

  it('offers navigation when no environments exist', async () => {
    vi.mocked(environmentsApi.getByProject).mockResolvedValueOnce([]);
    const { unmount } = renderDialog();
    expect(await screen.findByText('No environments are available for this project.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Create Environment' })).toHaveAttribute(
      'href',
      '/projects/project-1',
    );
    unmount();
  });

  it('offers navigation when an environment has no endpoints', async () => {
    vi.mocked(environmentsApi.getByProject).mockResolvedValueOnce([environmentOne]);
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValueOnce([]);
    const { unmount } = renderDialog();
    expect(await screen.findByText('No endpoints are available for this environment.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Add Endpoint' })).toHaveAttribute(
      'href',
      `/environments/${environmentOne.id}`,
    );
    unmount();
  });

  it('offers navigation when an endpoint has no mapping specs', async () => {
    vi.mocked(environmentsApi.getByProject).mockResolvedValueOnce([environmentOne]);
    vi.mocked(endpointsApi.getByEnvironment).mockResolvedValueOnce([endpointOne]);
    vi.mocked(mappingApi.getByEndpoint).mockResolvedValueOnce([]);
    const { unmount } = renderDialog();
    expect(await screen.findByText(/No mapping specs are available/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Create Mapping' })).toHaveAttribute(
      'href',
      `/environments/${environmentOne.id}`,
    );
    unmount();
  });

  it('keeps a queue failure visible and submits only once while pending', async () => {
    let resolveQueue: ((value: typeof queuedRun) => void) | undefined;
    vi.mocked(runsApi.queue).mockImplementationOnce(() => new Promise((resolve) => {
      resolveQueue = resolve;
    }));
    const { onClose, onRunStarted } = renderDialog();
    await waitForReadySelection();

    const start = screen.getByRole('button', { name: 'Start Run' });
    fireEvent.click(start);
    fireEvent.click(start);
    expect(runsApi.queue).toHaveBeenCalledOnce();
    expect(start).toBeDisabled();

    resolveQueue?.(queuedRun);
    await waitFor(() => expect(onRunStarted).toHaveBeenCalledWith('run-1'));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('does not navigate from a queue response after close and reopen', async () => {
    let resolveQueue: ((value: typeof queuedRun) => void) | undefined;
    vi.mocked(runsApi.queue).mockImplementationOnce(() => new Promise((resolve) => {
      resolveQueue = resolve;
    }));
    const { rerender, onRunStarted } = renderDialog();
    await waitForReadySelection();
    fireEvent.click(screen.getByRole('button', { name: 'Start Run' }));

    rerender(
      <RunConfigDialog
        open={false}
        onClose={vi.fn()}
        projectId="project-1"
        suiteId="suite-1"
        onRunStarted={onRunStarted}
      />,
    );
    rerender(
      <RunConfigDialog
        open
        onClose={vi.fn()}
        projectId="project-1"
        suiteId="suite-1"
        onRunStarted={onRunStarted}
      />,
    );
    resolveQueue?.(queuedRun);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(onRunStarted).not.toHaveBeenCalled();
  });

  it('shows the queue API error and allows a retry', async () => {
    vi.mocked(runsApi.queue)
      .mockRejectedValueOnce(apiError('Run queue is unavailable'))
      .mockResolvedValueOnce(queuedRun);
    const { onRunStarted } = renderDialog();
    await waitForReadySelection();

    fireEvent.click(screen.getByRole('button', { name: 'Start Run' }));
    expect(await screen.findByText('Run queue is unavailable')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Start Run' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Start Run' }));
    await waitFor(() => expect(onRunStarted).toHaveBeenCalledWith('run-1'));
    expect(runsApi.queue).toHaveBeenCalledTimes(2);
  });
});
