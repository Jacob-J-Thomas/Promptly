import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AxiosError, AxiosHeaders } from 'axios';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { suitesApi, type TestSuite } from '../api/suites';
import { SuiteForm } from './SuiteForm';

const router = vi.hoisted(() => ({
  navigate: vi.fn(),
  projectId: 'project-1' as string | undefined,
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return {
    ...actual,
    useNavigate: () => router.navigate,
    useParams: () => ({ projectId: router.projectId }),
  };
});

vi.mock('../components/Layout', () => ({
  Layout: ({ children }: { children: ReactNode }) => <div>{children}</div>,
}));

vi.mock('../api/suites', () => ({
  suitesApi: {
    create: vi.fn(),
  },
}));

const suite: TestSuite = {
  id: 'suite-1',
  projectId: 'project-1',
  name: 'Regression Suite',
  description: 'Checks the main flows',
  createdAt: '2026-08-12T00:00:00Z',
  updatedAt: '2026-08-12T00:00:00Z',
  testCaseCount: 0,
};

const getForm = () => screen.getByRole('button', { name: 'Create Test Suite' }).closest('form')!;

describe('SuiteForm', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    router.projectId = 'project-1';
    vi.mocked(suitesApi.create).mockResolvedValue(suite);
  });

  it('navigates back from either exit action', () => {
    render(<SuiteForm />);

    fireEvent.click(screen.getByRole('button', { name: 'Back to Project' }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(router.navigate).toHaveBeenNthCalledWith(1, '/projects/project-1');
    expect(router.navigate).toHaveBeenNthCalledWith(2, '/projects/project-1');
  });

  it('requires a non-whitespace suite name and allows dismissing the error', () => {
    render(<SuiteForm />);

    fireEvent.change(screen.getByLabelText(/Suite Name/), { target: { value: '   ' } });
    fireEvent.submit(getForm());

    expect(screen.getByText('Suite name is required')).toBeInTheDocument();
    expect(suitesApi.create).not.toHaveBeenCalled();

    fireEvent.click(screen.getByTitle('Close'));
    expect(screen.queryByText('Suite name is required')).not.toBeInTheDocument();
  });

  it('trims form values, creates the suite, and opens its detail page', async () => {
    render(<SuiteForm />);

    fireEvent.change(screen.getByLabelText(/Suite Name/), {
      target: { value: '  Regression Suite  ' },
    });
    fireEvent.change(screen.getByLabelText('Description'), {
      target: { value: '  Checks the main flows  ' },
    });
    fireEvent.submit(getForm());

    await waitFor(() => expect(suitesApi.create).toHaveBeenCalledWith('project-1', {
      name: 'Regression Suite',
      description: 'Checks the main flows',
    }));
    expect(router.navigate).toHaveBeenCalledWith('/suites/suite-1');
  });

  it('omits an empty description and disables fields while the request is pending', async () => {
    let resolveCreate: ((created: TestSuite) => void) | undefined;
    vi.mocked(suitesApi.create).mockImplementationOnce(() => new Promise((resolve) => {
      resolveCreate = resolve;
    }));
    render(<SuiteForm />);

    fireEvent.change(screen.getByLabelText(/Suite Name/), {
      target: { value: 'Minimal Suite' },
    });
    fireEvent.change(screen.getByLabelText('Description'), {
      target: { value: '   ' },
    });
    fireEvent.submit(getForm());

    expect(await screen.findByRole('button', { name: 'Creating...' })).toBeDisabled();
    expect(screen.getByLabelText(/Suite Name/)).toBeDisabled();
    expect(screen.getByLabelText('Description')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(suitesApi.create).toHaveBeenCalledWith('project-1', {
      name: 'Minimal Suite',
      description: undefined,
    });

    await act(async () => resolveCreate?.(suite));
    expect(router.navigate).toHaveBeenCalledWith('/suites/suite-1');
  });

  it('shows the API error and restores the form after creation fails', async () => {
    vi.mocked(suitesApi.create).mockRejectedValueOnce(new AxiosError(
      'request failed',
      'ERR_BAD_REQUEST',
      undefined,
      undefined,
      {
        data: { message: 'A suite with this name already exists' },
        status: 409,
        statusText: 'Conflict',
        headers: {},
        config: { headers: new AxiosHeaders() },
      },
    ));
    render(<SuiteForm />);

    fireEvent.change(screen.getByLabelText(/Suite Name/), {
      target: { value: 'Regression Suite' },
    });
    fireEvent.submit(getForm());

    expect(await screen.findByText('A suite with this name already exists')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create Test Suite' })).toBeEnabled();
    expect(router.navigate).not.toHaveBeenCalled();
  });
});
