import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AxiosError, AxiosHeaders } from 'axios';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { endpointsApi } from '../api/endpoints';
import { mappingApi, type ValidateMappingResponse } from '../api/mapping';
import { MappingWizard } from './MappingWizard';

vi.mock('../api/endpoints', () => ({
  endpointsApi: {
    create: vi.fn(),
  },
}));

vi.mock('../api/mapping', () => ({
  mappingApi: {
    propose: vi.fn(),
    validate: vi.fn(),
    create: vi.fn(),
    setDefault: vi.fn(),
  },
}));

vi.mock('@monaco-editor/react', () => ({
  default: ({
    value,
    onChange,
  }: {
    value?: string;
    onChange?: (value: string | undefined) => void;
  }) => (
    <textarea
      aria-label="Mapping Specification Editor"
      value={value ?? ''}
      onChange={(event) => onChange?.(event.target.value)}
    />
  ),
}));

const endpoint = {
  id: 'endpoint-1',
  environmentId: 'environment-1',
  name: 'Chat endpoint',
  path: '/chat',
  httpMethod: 'POST',
  timeoutSeconds: 30,
};

const mappingSpec = {
  id: 'mapping-1',
  endpointId: endpoint.id,
  name: 'Chat endpoint Default Mapping',
  specJson: '{"version":1}',
  isDefault: false,
  createdAt: '2026-08-12T00:00:00Z',
  updatedAt: '2026-08-12T00:00:00Z',
};

const previewTrace = {
  messages: [{ role: 'assistant', content: 'Hello' }],
  toolCalls: [],
  retrievedDocs: [],
};

const renderWizard = () => {
  const onClose = vi.fn();
  const onComplete = vi.fn();

  render(
    <MappingWizard
      open
      environmentId="environment-1"
      onClose={onClose}
      onComplete={onComplete}
    />,
  );

  return { onClose, onComplete };
};

const enterEndpointBasics = () => {
  fireEvent.change(screen.getByLabelText(/Endpoint Name/), {
    target: { value: 'Chat endpoint' },
  });
  fireEvent.change(screen.getByLabelText(/Endpoint Path/), {
    target: { value: '/chat' },
  });
  fireEvent.change(screen.getByLabelText(/Timeout/), {
    target: { value: '45' },
  });
  fireEvent.mouseDown(screen.getByRole('combobox'));
  fireEvent.click(screen.getByRole('option', { name: 'PATCH' }));
  fireEvent.click(screen.getByRole('button', { name: 'Next' }));
};

const enterSamples = (withRequest = true) => {
  if (withRequest) {
    fireEvent.change(screen.getByLabelText(/Sample Request JSON/), {
      target: { value: '{"messages":[{"role":"user","content":"Hi"}]}' },
    });
  }
  fireEvent.change(screen.getByLabelText(/Sample Response JSON/), {
    target: { value: '{"choices":[{"message":{"content":"Hello"}}]}' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Next' }));
};

const reachMappingEditor = async (withRequest = true) => {
  enterEndpointBasics();
  enterSamples(withRequest);
  fireEvent.click(screen.getByRole('button', { name: 'Propose Mapping' }));
  return screen.findByLabelText('Mapping Specification Editor');
};

const reachFinalize = async (validation: ValidateMappingResponse = {
  success: true,
  previewTrace,
}) => {
  await reachMappingEditor();
  vi.mocked(mappingApi.validate).mockResolvedValueOnce(validation);
  fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
  return screen.findByLabelText(/Mapping Name/);
};

describe('MappingWizard', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(endpointsApi.create).mockResolvedValue(endpoint);
    vi.mocked(mappingApi.propose).mockResolvedValue({
      mappingSpecJson: '{"version":1}',
      reason: 'The response contains an assistant message.',
    });
    vi.mocked(mappingApi.validate).mockResolvedValue({
      success: true,
      previewTrace,
    });
    vi.mocked(mappingApi.create).mockResolvedValue(mappingSpec);
    vi.mocked(mappingApi.setDefault).mockResolvedValue(undefined);
  });

  it('validates endpoint and sample input before proposing', () => {
    renderWizard();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('Endpoint name and path are required')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Endpoint Name/), {
      target: { value: 'Chat endpoint' },
    });
    fireEvent.change(screen.getByLabelText(/Endpoint Path/), {
      target: { value: '/chat' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('Sample response JSON is required')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Sample Response JSON/), {
      target: { value: 'not-json' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('Invalid JSON format in sample data')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Sample Response JSON/), {
      target: { value: '{"ok":true}' },
    });
    fireEvent.change(screen.getByLabelText(/Sample Request JSON/), {
      target: { value: '{invalid' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('Invalid JSON format in sample data')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Sample Request JSON/), {
      target: { value: '' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByRole('button', { name: 'Propose Mapping' })).toBeInTheDocument();
  });

  it('supports going back and cancelling with a clean reset', () => {
    const { onClose } = renderWizard();

    fireEvent.change(screen.getByLabelText(/Endpoint Name/), {
      target: { value: 'Temporary endpoint' },
    });
    fireEvent.change(screen.getByLabelText(/Endpoint Path/), {
      target: { value: '/temporary' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByDisplayValue('Temporary endpoint')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledOnce();
    expect(screen.getByLabelText(/Endpoint Name/)).toHaveValue('');
    expect(screen.getByLabelText(/Timeout/)).toHaveValue(30);
  });

  it('creates an endpoint, proposes and validates a mapping, and saves it as default', async () => {
    const { onClose, onComplete } = renderWizard();

    const editor = await reachMappingEditor();
    expect(endpointsApi.create).toHaveBeenCalledWith('environment-1', {
      name: 'Chat endpoint',
      path: '/chat',
      httpMethod: 'PATCH',
      timeoutSeconds: 45,
    });
    expect(mappingApi.propose).toHaveBeenCalledWith(endpoint.id, {
      sampleResponseJson: '{"choices":[{"message":{"content":"Hello"}}]}',
      sampleRequestJson: '{"messages":[{"role":"user","content":"Hi"}]}',
    });
    expect(screen.getByText('The response contains an assistant message.')).toBeInTheDocument();

    fireEvent.change(editor, { target: { value: '{"version":2}' } });
    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));

    await screen.findByText('Preview: Canonical Trace');
    expect(screen.getByText(/"content": "Hello"/)).toBeInTheDocument();
    expect(mappingApi.validate).toHaveBeenCalledWith(endpoint.id, {
      mappingSpecJson: '{"version":2}',
      sampleResponseJson: '{"choices":[{"message":{"content":"Hello"}}]}',
    });

    fireEvent.click(screen.getByRole('button', { name: 'Save & Complete' }));

    await waitFor(() => expect(mappingApi.create).toHaveBeenCalledWith(endpoint.id, {
      name: 'Chat endpoint Default Mapping',
      specJson: '{"version":2}',
    }));
    expect(mappingApi.setDefault).toHaveBeenCalledWith(mappingSpec.id);
    expect(onComplete).toHaveBeenCalledWith(endpoint.id);
    expect(onClose).toHaveBeenCalledOnce();
    expect(screen.getByLabelText(/Endpoint Name/)).toHaveValue('');
  });

  it('can save a validated mapping without making it the default', async () => {
    const { onComplete } = renderWizard();

    const mappingName = await reachFinalize({ success: true, previewTrace: null });
    expect(screen.queryByText('Preview: Canonical Trace')).not.toBeInTheDocument();

    fireEvent.change(mappingName, { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save & Complete' }));
    expect(screen.getByText('Mapping name is required')).toBeInTheDocument();
    expect(mappingApi.create).not.toHaveBeenCalled();

    fireEvent.change(mappingName, { target: { value: 'Manual mapping' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /Set as default/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Save & Complete' }));

    await waitFor(() => expect(mappingApi.create).toHaveBeenCalledWith(endpoint.id, {
      name: 'Manual mapping',
      specJson: '{"version":1}',
    }));
    expect(mappingApi.setDefault).not.toHaveBeenCalled();
    expect(onComplete).toHaveBeenCalledWith(endpoint.id);
  });

  it('shows provider and validation failures without advancing', async () => {
    renderWizard();
    vi.mocked(mappingApi.propose).mockRejectedValueOnce(new Error('worker unavailable'));

    enterEndpointBasics();
    enterSamples(false);
    fireEvent.click(screen.getByRole('button', { name: 'Propose Mapping' }));

    expect(await screen.findByText('Failed to propose mapping')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Propose Mapping' })).toBeEnabled();

    vi.mocked(mappingApi.propose).mockResolvedValueOnce({ mappingSpecJson: '{}' });
    fireEvent.click(screen.getByRole('button', { name: 'Propose Mapping' }));
    await screen.findByLabelText('Mapping Specification Editor');

    vi.mocked(mappingApi.validate).mockResolvedValueOnce({
      success: false,
      errorMessage: 'Mapping did not match the sample',
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
    expect(await screen.findByText('Mapping did not match the sample')).toBeInTheDocument();
    expect(screen.getByLabelText('Mapping Specification Editor')).toBeInTheDocument();

    vi.mocked(mappingApi.validate).mockRejectedValueOnce(new Error('validation offline'));
    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
    expect(await screen.findByText('Failed to validate mapping')).toBeInTheDocument();
  });

  it('uses an API message for a save failure and leaves the final step open', async () => {
    renderWizard();
    await reachFinalize();
    vi.mocked(mappingApi.create).mockRejectedValueOnce(new AxiosError(
      'request failed',
      'ERR_BAD_REQUEST',
      undefined,
      undefined,
      {
        data: { message: 'Mapping name already exists' },
        status: 409,
        statusText: 'Conflict',
        headers: {},
        config: { headers: new AxiosHeaders() },
      },
    ));

    fireEvent.click(screen.getByRole('button', { name: 'Save & Complete' }));

    expect(await screen.findByText('Mapping name already exists')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save & Complete' })).toBeEnabled();
  });
});
