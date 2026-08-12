import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { projectsApi } from '../api/projects';
import { deferred } from '../test/render-route';
import { ProjectsList } from './ProjectsList';

const navigate = vi.hoisted(() => vi.fn());

vi.mock('react-router-dom', () => ({ useNavigate: () => navigate }));
vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../api/projects', () => ({
  projectsApi: {
    getAll: vi.fn(),
    create: vi.fn(),
    delete: vi.fn(),
  },
}));

const project = {
  id: 'project-1',
  name: 'Evaluation API',
  description: 'Primary evaluation project',
  createdAt: '2026-08-12T00:00:00Z',
  updatedAt: '2026-08-12T00:00:00Z',
};

describe('ProjectsList', () => {
  beforeEach(() => {
    navigate.mockReset();
    vi.mocked(projectsApi.getAll).mockReset();
    vi.mocked(projectsApi.create).mockReset();
    vi.mocked(projectsApi.delete).mockReset();
  });

  it('renders projects and supports both project navigation affordances', async () => {
    vi.mocked(projectsApi.getAll).mockResolvedValue([
      project,
      { ...project, id: 'project-2', name: 'No description', description: undefined },
    ]);
    render(<ProjectsList />);

    expect(await screen.findByText('Evaluation API')).toBeInTheDocument();
    expect(screen.getByText('Primary evaluation project')).toBeInTheDocument();
    expect(screen.getByText('No description')).toBeInTheDocument();
    expect(screen.getAllByText(/Created/)).toHaveLength(2);

    fireEvent.click(screen.getByText('Evaluation API'));
    fireEvent.click(screen.getAllByRole('button', { name: 'Open' })[1]);
    expect(navigate).toHaveBeenNthCalledWith(1, '/projects/project-1');
    expect(navigate).toHaveBeenNthCalledWith(2, '/projects/project-2');
  });

  it('validates and creates a project, showing pending state and reloading the list', async () => {
    const pending = deferred<typeof project>();
    vi.mocked(projectsApi.getAll)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([project]);
    vi.mocked(projectsApi.create).mockReturnValue(pending.promise);
    render(<ProjectsList />);

    expect(await screen.findByText('No projects yet')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Create Your First Project' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    expect(screen.getByText('Project name is required')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Project Name/), { target: { value: 'Evaluation API' } });
    fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Primary evaluation project' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('button', { name: 'Creating...' })).toBeDisabled();
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    pending.resolve(project);
    expect(await screen.findByText('Evaluation API')).toBeInTheDocument();
    expect(projectsApi.create).toHaveBeenCalledWith({
      name: 'Evaluation API',
      description: 'Primary evaluation project',
    });
    expect(projectsApi.getAll).toHaveBeenCalledTimes(2);
  });

  it('closes an idle create dialog through its modal close behavior', async () => {
    vi.mocked(projectsApi.getAll).mockResolvedValue([]);
    render(<ProjectsList />);

    await screen.findByText('No projects yet');
    fireEvent.click(screen.getByRole('button', { name: 'Create Project' }));
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('omits an empty description and reports create failures', async () => {
    vi.mocked(projectsApi.getAll).mockResolvedValue([]);
    vi.mocked(projectsApi.create).mockRejectedValue(new Error('offline'));
    render(<ProjectsList />);

    await screen.findByText('No projects yet');
    fireEvent.click(screen.getByRole('button', { name: 'Create Project' }));
    fireEvent.change(screen.getByLabelText(/Project Name/), { target: { value: 'Bare project' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('Failed to create project')).toBeInTheDocument();
    expect(projectsApi.create).toHaveBeenCalledWith({ name: 'Bare project', description: undefined });
    expect(screen.getByRole('button', { name: 'Create' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('does not delete without confirmation, then deletes and reloads when confirmed', async () => {
    vi.mocked(projectsApi.getAll)
      .mockResolvedValueOnce([project])
      .mockResolvedValueOnce([]);
    vi.mocked(projectsApi.delete).mockResolvedValue(undefined);
    const confirm = vi.spyOn(window, 'confirm')
      .mockReturnValueOnce(false)
      .mockReturnValueOnce(true);
    render(<ProjectsList />);

    await screen.findByText('Evaluation API');
    const deleteButton = screen.getByTestId('DeleteIcon').closest('button');
    expect(deleteButton).not.toBeNull();
    fireEvent.click(deleteButton!);
    expect(projectsApi.delete).not.toHaveBeenCalled();
    fireEvent.click(deleteButton!);

    await waitFor(() => expect(projectsApi.delete).toHaveBeenCalledWith('project-1'));
    expect(await screen.findByText('No projects yet')).toBeInTheDocument();
    confirm.mockRestore();
  });

  it('surfaces load and delete failures and allows dismissing the alert', async () => {
    vi.mocked(projectsApi.getAll).mockRejectedValueOnce(new Error('offline'));
    const first = render(<ProjectsList />);
    expect(await screen.findByText('Failed to load projects')).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Close'));
    expect(screen.queryByText('Failed to load projects')).not.toBeInTheDocument();
    first.unmount();

    vi.mocked(projectsApi.getAll).mockResolvedValue([project]);
    vi.mocked(projectsApi.delete).mockRejectedValue(new Error('offline'));
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    render(<ProjectsList />);
    await screen.findByText('Evaluation API');
    fireEvent.click(screen.getByTestId('DeleteIcon').closest('button')!);
    expect(await screen.findByText('Failed to delete project')).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});
