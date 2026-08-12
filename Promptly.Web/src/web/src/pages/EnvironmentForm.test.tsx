import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { environmentsApi } from '../api/environments';
import { deferred, renderAtRoute } from '../test/render-route';
import { EnvironmentForm } from './EnvironmentForm';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', async (importOriginal) => ({
  ...await importOriginal<typeof import('react-router-dom')>(),
  useNavigate: () => navigate,
}));
vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../api/environments', () => ({ environmentsApi: { create: vi.fn() } }));

const createdEnvironment = {
  id: 'environment-1',
  projectId: 'project-1',
  name: 'Staging',
  baseUrl: 'https://staging.example.test',
  hasHeaders: false,
  createdAt: '2026-08-12T00:00:00Z',
};

const renderForm = () => renderAtRoute(
  <EnvironmentForm />,
  '/projects/project-1/environments/new',
  '/projects/:projectId/environments/new',
);

const submit = () => {
  const button = screen.getByRole('button', { name: /Create Environment|Creating/ });
  const form = button.closest('form');
  expect(form).not.toBeNull();
  fireEvent.submit(form!);
};

describe('EnvironmentForm', () => {
  beforeEach(() => {
    navigate.mockReset();
    vi.mocked(environmentsApi.create).mockReset();
  });

  it('requires a name and base URL before calling the API', () => {
    renderForm();
    submit();

    expect(screen.getByText('Name and Base URL are required')).toBeInTheDocument();
    expect(environmentsApi.create).not.toHaveBeenCalled();
  });

  it('adds, truncates, and removes optional headers', () => {
    renderForm();
    const add = screen.getByRole('button', { name: 'Add' });
    expect(add).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Header Name'), { target: { value: 'Authorization' } });
    fireEvent.change(screen.getByLabelText('Header Value'), { target: { value: 'Bearer short-token' } });
    fireEvent.click(add);
    expect(screen.getByText('Authorization')).toBeInTheDocument();
    expect(screen.getByText('Bearer short-token')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Header Name'), { target: { value: 'X-Long' } });
    fireEvent.change(screen.getByLabelText('Header Value'), { target: { value: 'x'.repeat(60) } });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));
    expect(screen.getByText(`${'x'.repeat(50)}...`)).toBeInTheDocument();

    const deleteButtons = screen.getAllByTestId('DeleteIcon').map((icon) => icon.closest('button'));
    fireEvent.click(deleteButtons[0]!);
    expect(screen.queryByText('Authorization')).not.toBeInTheDocument();
  });

  it('submits trimmed values and headers, then navigates to the project', async () => {
    const pending = deferred<typeof createdEnvironment>();
    vi.mocked(environmentsApi.create).mockReturnValue(pending.promise);
    renderForm();

    fireEvent.change(screen.getByLabelText(/Environment Name/), { target: { value: '  Staging  ' } });
    fireEvent.change(screen.getByLabelText(/Base URL/), { target: { value: '  https://staging.example.test  ' } });
    fireEvent.change(screen.getByLabelText('Header Name'), { target: { value: ' Authorization ' } });
    fireEvent.change(screen.getByLabelText('Header Value'), { target: { value: ' Bearer token ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));
    submit();

    expect(await screen.findByRole('button', { name: 'Creating...' })).toBeDisabled();
    pending.resolve(createdEnvironment);
    await waitFor(() => expect(environmentsApi.create).toHaveBeenCalledWith('project-1', {
      name: 'Staging',
      baseUrl: 'https://staging.example.test',
      headers: { Authorization: 'Bearer token' },
    }));
    expect(navigate).toHaveBeenCalledWith('/projects/project-1');
  });

  it('omits empty headers and surfaces create failures', async () => {
    vi.mocked(environmentsApi.create).mockRejectedValue(new Error('offline'));
    renderForm();
    fireEvent.change(screen.getByLabelText(/Environment Name/), { target: { value: 'Staging' } });
    fireEvent.change(screen.getByLabelText(/Base URL/), { target: { value: 'https://staging.example.test' } });
    submit();

    expect(await screen.findByText('Failed to create environment')).toBeInTheDocument();
    expect(environmentsApi.create).toHaveBeenCalledWith('project-1', {
      name: 'Staging',
      baseUrl: 'https://staging.example.test',
      headers: undefined,
    });
    expect(screen.getByRole('button', { name: 'Create Environment' })).toBeEnabled();
    fireEvent.click(screen.getByTitle('Close'));
    expect(screen.queryByText('Failed to create environment')).not.toBeInTheDocument();
  });

  it('supports both back and cancel navigation', () => {
    renderForm();
    fireEvent.click(screen.getByRole('button', { name: 'Back to Project' }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(navigate).toHaveBeenNthCalledWith(1, '/projects/project-1');
    expect(navigate).toHaveBeenNthCalledWith(2, '/projects/project-1');
  });
});
