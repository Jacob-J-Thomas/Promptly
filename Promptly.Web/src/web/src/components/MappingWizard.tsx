import React, { useState } from 'react';
import {
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Button,
  Stepper,
  Step,
  StepLabel,
  TextField,
  Box,
  Typography,
  Alert,
  CircularProgress,
  Select,
  MenuItem,
  FormControl,
  InputLabel,
  Paper,
  Divider,
} from '@mui/material';
import Editor from './configuredMonacoEditor';
import { mappingApi, type CanonicalTrace } from '../api/mapping';
import { endpointsApi, type CreateEndpointRequest } from '../api/endpoints';
import { getApiErrorMessage } from '../api/errors';

interface MappingWizardProps {
  open: boolean;
  onClose: () => void;
  environmentId: string;
  onComplete: (endpointId: string) => void;
}

const steps = [
  'Endpoint Basics',
  'Sample Data',
  'Propose Mapping',
  'Edit & Preview',
  'Save & Finalize',
];

export const MappingWizard: React.FC<MappingWizardProps> = ({
  open,
  onClose,
  environmentId,
  onComplete,
}) => {
  const [activeStep, setActiveStep] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  // Step 1: Endpoint basics
  const [endpointName, setEndpointName] = useState('');
  const [endpointPath, setEndpointPath] = useState('');
  const [httpMethod, setHttpMethod] = useState('POST');
  const [timeoutSeconds, setTimeoutSeconds] = useState(30);

  // Step 2: Sample data
  const [sampleRequest, setSampleRequest] = useState('');
  const [sampleResponse, setSampleResponse] = useState('');

  // Step 3 & 4: Proposed mapping
  const [proposedMappingJson, setProposedMappingJson] = useState('');
  const [proposalReason, setProposalReason] = useState('');
  const [previewTrace, setPreviewTrace] = useState<CanonicalTrace | null>(null);

  // Step 5: Mapping name
  const [mappingName, setMappingName] = useState('');
  const [setAsDefault, setSetAsDefault] = useState(true);

  // Created endpoint ID
  const [createdEndpointId, setCreatedEndpointId] = useState<string | null>(null);

  const handleNext = async () => {
    try {
      setError('');

      if (activeStep === 0) {
        // Validate endpoint basics
        if (!endpointName || !endpointPath) {
          setError('Endpoint name and path are required');
          return;
        }
        setActiveStep(1);
      } else if (activeStep === 1) {
        // Validate sample data
        if (!sampleResponse) {
          setError('Sample response JSON is required');
          return;
        }
        // Try to parse JSON to validate
        try {
          JSON.parse(sampleResponse);
          if (sampleRequest) {
            JSON.parse(sampleRequest);
          }
        } catch {
          setError('Invalid JSON format in sample data');
          return;
        }
        setActiveStep(2);
      } else if (activeStep === 2) {
        // Propose mapping
        await handleProposeMapping();
      } else if (activeStep === 3) {
        // Validate mapping
        await handleValidateMapping();
      } else if (activeStep === 4) {
        // Save mapping
        await handleSaveMapping();
      }
    } catch (error: unknown) {
      const fallback = error instanceof Error && error.message
        ? error.message
        : 'An error occurred';
      setError(getApiErrorMessage(error, fallback));
    }
  };

  const handleBack = () => {
    setError('');
    setActiveStep((prev) => prev - 1);
  };

  const handleProposeMapping = async () => {
    try {
      setLoading(true);
      setError('');

      // First, create the endpoint
      const endpointData: CreateEndpointRequest = {
        name: endpointName,
        path: endpointPath,
        httpMethod,
        timeoutSeconds,
      };

      const endpoint = await endpointsApi.create(environmentId, endpointData);
      setCreatedEndpointId(endpoint.id);

      // Then propose mapping
      const result = await mappingApi.propose(endpoint.id, {
        sampleResponseJson: sampleResponse,
        sampleRequestJson: sampleRequest || undefined,
      });

      setProposedMappingJson(result.mappingSpecJson);
      setProposalReason(result.reason || '');
      setMappingName(`${endpointName} Default Mapping`);

      // Move to next step
      setActiveStep(3);
    } catch (error: unknown) {
      setError(getApiErrorMessage(error, 'Failed to propose mapping'));
    } finally {
      setLoading(false);
    }
  };

  const handleValidateMapping = async () => {
    try {
      setLoading(true);
      setError('');

      if (!createdEndpointId) {
        setError('Endpoint not created');
        return;
      }

      // Validate the mapping spec
      const result = await mappingApi.validate(createdEndpointId, {
        mappingSpecJson: proposedMappingJson,
        sampleResponseJson: sampleResponse,
      });

      if (!result.success) {
        setError(result.errorMessage || 'Mapping validation failed');
        return;
      }

      setPreviewTrace(result.previewTrace ?? null);
      setActiveStep(4);
    } catch (error: unknown) {
      setError(getApiErrorMessage(error, 'Failed to validate mapping'));
    } finally {
      setLoading(false);
    }
  };

  const handleSaveMapping = async () => {
    try {
      setLoading(true);
      setError('');

      if (!createdEndpointId) {
        setError('Endpoint not created');
        return;
      }

      if (!mappingName) {
        setError('Mapping name is required');
        return;
      }

      // Save the mapping spec
      const mappingSpec = await mappingApi.create(createdEndpointId, {
        name: mappingName,
        specJson: proposedMappingJson,
      });

      // Set as default if requested
      if (setAsDefault) {
        await mappingApi.setDefault(mappingSpec.id);
      }

      // Success - close wizard and notify parent
      onComplete(createdEndpointId);
      handleReset();
      onClose();
    } catch (error: unknown) {
      setError(getApiErrorMessage(error, 'Failed to save mapping'));
    } finally {
      setLoading(false);
    }
  };

  const handleReset = () => {
    setActiveStep(0);
    setError('');
    setEndpointName('');
    setEndpointPath('');
    setHttpMethod('POST');
    setTimeoutSeconds(30);
    setSampleRequest('');
    setSampleResponse('');
    setProposedMappingJson('');
    setProposalReason('');
    setPreviewTrace(null);
    setMappingName('');
    setSetAsDefault(true);
    setCreatedEndpointId(null);
  };

  const handleCancel = () => {
    handleReset();
    onClose();
  };

  const renderStepContent = () => {
    switch (activeStep) {
      case 0:
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, mt: 2 }}>
            <Typography variant="body2" color="text.secondary">
              Define the endpoint details for your LLM service.
            </Typography>
            <TextField
              label="Endpoint Name"
              value={endpointName}
              onChange={(e) => setEndpointName(e.target.value)}
              required
              fullWidth
              placeholder="e.g., ChatGPT Completion"
            />
            <TextField
              label="Endpoint Path"
              value={endpointPath}
              onChange={(e) => setEndpointPath(e.target.value)}
              required
              fullWidth
              placeholder="e.g., /v1/chat/completions"
            />
            <FormControl fullWidth>
              <InputLabel>HTTP Method</InputLabel>
              <Select
                value={httpMethod}
                onChange={(e) => setHttpMethod(e.target.value)}
                label="HTTP Method"
              >
                <MenuItem value="GET">GET</MenuItem>
                <MenuItem value="POST">POST</MenuItem>
                <MenuItem value="PUT">PUT</MenuItem>
                <MenuItem value="PATCH">PATCH</MenuItem>
              </Select>
            </FormControl>
            <TextField
              label="Timeout (seconds)"
              type="number"
              value={timeoutSeconds}
              onChange={(e) => setTimeoutSeconds(parseInt(e.target.value))}
              required
              fullWidth
            />
          </Box>
        );

      case 1:
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, mt: 2 }}>
            <Typography variant="body2" color="text.secondary">
              Paste sample JSON data from your endpoint. The response is required, request is optional.
            </Typography>
            <TextField
              label="Sample Request JSON (Optional)"
              value={sampleRequest}
              onChange={(e) => setSampleRequest(e.target.value)}
              multiline
              rows={8}
              fullWidth
              placeholder='{"messages": [{"role": "user", "content": "Hello"}]}'
              sx={{ fontFamily: 'monospace' }}
            />
            <TextField
              label="Sample Response JSON (Required)"
              value={sampleResponse}
              onChange={(e) => setSampleResponse(e.target.value)}
              multiline
              rows={12}
              required
              fullWidth
              placeholder='{"choices": [{"message": {"role": "assistant", "content": "Hi there!"}}]}'
              sx={{ fontFamily: 'monospace' }}
            />
          </Box>
        );

      case 2:
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, mt: 2, alignItems: 'center' }}>
            <Typography variant="body1">
              Click "Propose Mapping" to use LLM-based analysis to generate a MappingSpec for your endpoint.
            </Typography>
            <Typography variant="body2" color="text.secondary">
              This will create the endpoint and use the Python evaluation worker to analyze your sample data.
            </Typography>
            {loading && (
              <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 2, mt: 2 }}>
                <CircularProgress />
                <Typography variant="body2">Analyzing sample data...</Typography>
              </Box>
            )}
          </Box>
        );

      case 3:
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, mt: 2 }}>
            <Typography variant="body2" color="text.secondary">
              Review and edit the proposed mapping specification. The editor uses Monaco for JSON editing.
            </Typography>
            {proposalReason && (
              <Alert severity="info">
                <Typography variant="body2">
                  <strong>AI Reasoning:</strong> {proposalReason}
                </Typography>
              </Alert>
            )}
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography variant="subtitle2" gutterBottom>
                Mapping Specification (Editable)
              </Typography>
              <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden' }}>
                <Editor
                  height="300px"
                  language="json"
                  value={proposedMappingJson}
                  onChange={(value) => setProposedMappingJson(value || '')}
                  options={{
                    minimap: { enabled: false },
                    lineNumbers: 'on',
                    scrollBeyondLastLine: false,
                    automaticLayout: true,
                  }}
                />
              </Box>
            </Paper>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
              Click "Validate" to test this mapping against your sample response and see a preview of the parsed trace.
            </Typography>
          </Box>
        );

      case 4:
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, mt: 2 }}>
            <Typography variant="body2" color="text.secondary">
              Preview the canonical trace and save the mapping specification.
            </Typography>
            {previewTrace && (
              <Paper variant="outlined" sx={{ p: 2, maxHeight: 300, overflow: 'auto' }}>
                <Typography variant="subtitle2" gutterBottom>
                  Preview: Canonical Trace
                </Typography>
                <Divider sx={{ mb: 1 }} />
                <Box sx={{ fontFamily: 'monospace', fontSize: '0.875rem' }}>
                  <pre>{JSON.stringify(previewTrace, null, 2)}</pre>
                </Box>
              </Paper>
            )}
            <TextField
              label="Mapping Name"
              value={mappingName}
              onChange={(e) => setMappingName(e.target.value)}
              required
              fullWidth
              placeholder="e.g., Default Mapping"
            />
            <FormControl component="fieldset">
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                <input
                  type="checkbox"
                  checked={setAsDefault}
                  onChange={(e) => setSetAsDefault(e.target.checked)}
                  id="set-default-checkbox"
                />
                <label htmlFor="set-default-checkbox">
                  <Typography variant="body2">Set as default mapping for this endpoint</Typography>
                </label>
              </Box>
            </FormControl>
          </Box>
        );

      default:
        return null;
    }
  };

  return (
    <Dialog open={open} onClose={handleCancel} maxWidth="md" fullWidth>
      <DialogTitle>Add Endpoint with Mapping Wizard</DialogTitle>
      <DialogContent>
        <Stepper activeStep={activeStep} sx={{ mt: 2, mb: 3 }}>
          {steps.map((label) => (
            <Step key={label}>
              <StepLabel>{label}</StepLabel>
            </Step>
          ))}
        </Stepper>

        {error && (
          <Alert severity="error" sx={{ mb: 2 }}>
            {error}
          </Alert>
        )}

        {renderStepContent()}
      </DialogContent>
      <DialogActions>
        <Button onClick={handleCancel} disabled={loading}>
          Cancel
        </Button>
        {activeStep > 0 && activeStep < 2 && (
          <Button onClick={handleBack} disabled={loading}>
            Back
          </Button>
        )}
        {activeStep < 2 && (
          <Button variant="contained" onClick={handleNext} disabled={loading}>
            Next
          </Button>
        )}
        {activeStep === 2 && (
          <Button variant="contained" onClick={handleNext} disabled={loading}>
            {loading ? 'Proposing...' : 'Propose Mapping'}
          </Button>
        )}
        {activeStep === 3 && (
          <Button variant="contained" onClick={handleNext} disabled={loading}>
            {loading ? 'Validating...' : 'Validate'}
          </Button>
        )}
        {activeStep === 4 && (
          <Button variant="contained" onClick={handleNext} disabled={loading}>
            {loading ? 'Saving...' : 'Save & Complete'}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
};
