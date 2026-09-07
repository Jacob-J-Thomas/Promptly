import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormHelperText,
  InputLabel,
  MenuItem,
  Select,
  TextField,
  Typography,
} from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';
import { endpointsApi, type Endpoint } from '../api/endpoints';
import { environmentsApi, type Environment } from '../api/environments';
import { getApiErrorMessage } from '../api/errors';
import { mappingApi, type MappingSpec } from '../api/mapping';
import { runsApi } from '../api/runs';

interface RunConfigDialogProps {
  open: boolean;
  onClose: () => void;
  projectId: string;
  suiteId: string;
  onRunStarted: (runId: string) => void;
}

type SelectionKind = 'environment' | 'endpoint' | 'mapping';

const RunConfigDialog: React.FC<RunConfigDialogProps> = ({
  open,
  onClose,
  projectId,
  suiteId,
  onRunStarted,
}) => {
  const [environments, setEnvironments] = useState<Environment[]>([]);
  const [endpoints, setEndpoints] = useState<Endpoint[]>([]);
  const [mappings, setMappings] = useState<MappingSpec[]>([]);
  const [selectedEnv, setSelectedEnv] = useState('');
  const [selectedEndpoint, setSelectedEndpoint] = useState('');
  const [selectedMapping, setSelectedMapping] = useState('');
  const [gitCommit, setGitCommit] = useState('');
  const [environmentLoading, setEnvironmentLoading] = useState(false);
  const [endpointLoading, setEndpointLoading] = useState(false);
  const [mappingLoading, setMappingLoading] = useState(false);
  const [environmentError, setEnvironmentError] = useState('');
  const [endpointError, setEndpointError] = useState('');
  const [mappingError, setMappingError] = useState('');
  const [submitError, setSubmitError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const environmentRequest = useRef(0);
  const endpointRequest = useRef(0);
  const mappingRequest = useRef(0);
  const dialogSession = useRef(0);
  const submissionId = useRef(0);
  const submissionInFlight = useRef(false);

  const resetState = useCallback(() => {
    setEnvironments([]);
    setEndpoints([]);
    setMappings([]);
    setSelectedEnv('');
    setSelectedEndpoint('');
    setSelectedMapping('');
    setGitCommit('');
    setEnvironmentLoading(false);
    setEndpointLoading(false);
    setMappingLoading(false);
    setEnvironmentError('');
    setEndpointError('');
    setMappingError('');
    setSubmitError('');
    setSubmitting(false);
  }, []);

  const invalidateSession = useCallback(() => {
    environmentRequest.current += 1;
    endpointRequest.current += 1;
    mappingRequest.current += 1;
    dialogSession.current += 1;
    submissionId.current += 1;
    submissionInFlight.current = false;
  }, []);

  const contextKey = `${projectId}:${suiteId}`;

  useLayoutEffect(() => {
    invalidateSession();
    resetState();
  }, [contextKey, invalidateSession, open, resetState]);

  const loadEnvironments = useCallback(async () => {
    const requestId = ++environmentRequest.current;
    setEnvironmentLoading(true);
    setEnvironmentError('');

    try {
      const values = await environmentsApi.getByProject(projectId);
      if (requestId !== environmentRequest.current) {
        return;
      }
      const scopedValues = values.filter((environment) => environment.projectId === projectId);
      setEnvironments(scopedValues);
      setSelectedEnv(scopedValues.length === 1 ? scopedValues[0].id : '');
    } catch (error: unknown) {
      if (requestId === environmentRequest.current) {
        setEnvironments([]);
        setSelectedEnv('');
        setEnvironmentError(getApiErrorMessage(error, 'Failed to load environments'));
      }
    } finally {
      if (requestId === environmentRequest.current) {
        setEnvironmentLoading(false);
      }
    }
  }, [projectId]);

  const loadEndpoints = useCallback(async (environmentId: string) => {
    const requestId = ++endpointRequest.current;
    setEndpointLoading(true);
    setEndpointError('');

    try {
      const values = await endpointsApi.getByEnvironment(environmentId);
      if (requestId !== endpointRequest.current) {
        return;
      }
      const scopedValues = values.filter((endpoint) => endpoint.environmentId === environmentId);
      setEndpoints(scopedValues);
      setSelectedEndpoint(scopedValues[0]?.id ?? '');
    } catch (error: unknown) {
      if (requestId === endpointRequest.current) {
        setEndpoints([]);
        setSelectedEndpoint('');
        setEndpointError(getApiErrorMessage(error, 'Failed to load endpoints'));
      }
    } finally {
      if (requestId === endpointRequest.current) {
        setEndpointLoading(false);
      }
    }
  }, []);

  const loadMappings = useCallback(async (endpointId: string) => {
    const requestId = ++mappingRequest.current;
    setMappingLoading(true);
    setMappingError('');

    try {
      const values = await mappingApi.getByEndpoint(endpointId);
      if (requestId !== mappingRequest.current) {
        return;
      }
      const scopedValues = values.filter((mapping) => mapping.endpointId === endpointId);
      setMappings(scopedValues);
      const selected = scopedValues.find((mapping) => mapping.isDefault) ?? scopedValues[0];
      setSelectedMapping(selected?.id ?? '');
    } catch (error: unknown) {
      if (requestId === mappingRequest.current) {
        setMappings([]);
        setSelectedMapping('');
        setMappingError(getApiErrorMessage(error, 'Failed to load mapping specs'));
      }
    } finally {
      if (requestId === mappingRequest.current) {
        setMappingLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    if (open && projectId) {
      void loadEnvironments();
    }
  }, [loadEnvironments, open, projectId, suiteId]);

  useEffect(() => {
    endpointRequest.current += 1;
    mappingRequest.current += 1;
    setEndpoints([]);
    setSelectedEndpoint('');
    setMappings([]);
    setSelectedMapping('');
    setEndpointError('');
    setMappingError('');

    if (selectedEnv) {
      void loadEndpoints(selectedEnv);
    } else {
      setEndpointLoading(false);
      setMappingLoading(false);
    }
  }, [loadEndpoints, selectedEnv]);

  useEffect(() => {
    mappingRequest.current += 1;
    setMappings([]);
    setSelectedMapping('');
    setMappingError('');

    if (selectedEndpoint) {
      void loadMappings(selectedEndpoint);
    } else {
      setMappingLoading(false);
    }
  }, [loadMappings, selectedEndpoint]);

  const clearSelection = (kind: SelectionKind) => {
    setSubmitError('');
    if (kind === 'environment') {
      endpointRequest.current += 1;
      mappingRequest.current += 1;
      setEndpoints([]);
      setSelectedEndpoint('');
      setMappings([]);
      setSelectedMapping('');
      setEndpointError('');
      setMappingError('');
    } else if (kind === 'endpoint') {
      mappingRequest.current += 1;
      setMappings([]);
      setSelectedMapping('');
      setMappingError('');
    }
  };

  const handleClose = () => {
    environmentRequest.current += 1;
    endpointRequest.current += 1;
    mappingRequest.current += 1;
    setEnvironments([]);
    setEndpoints([]);
    setMappings([]);
    setSelectedEnv('');
    setSelectedEndpoint('');
    setSelectedMapping('');
    setGitCommit('');
    setEnvironmentError('');
    setEndpointError('');
    setMappingError('');
    setSubmitError('');
    onClose();
  };

  const handleStartRun = async () => {
    if (submissionInFlight.current || !selectedEnv || !selectedEndpoint || !selectedMapping) {
      return;
    }

    const sessionAtStart = dialogSession.current;
    const submissionAtStart = ++submissionId.current;
    const onRunStartedAtStart = onRunStarted;
    submissionInFlight.current = true;
    setSubmitting(true);
    setSubmitError('');
    try {
      const run = await runsApi.queue({
        suiteId,
        environmentId: selectedEnv,
        endpointId: selectedEndpoint,
        mappingSpecId: selectedMapping,
        gitCommitHash: gitCommit.trim() || undefined,
      });
      if (dialogSession.current !== sessionAtStart) {
        return;
      }
      onRunStartedAtStart(run.id);
      handleClose();
    } catch (error: unknown) {
      if (dialogSession.current === sessionAtStart) {
        setSubmitError(getApiErrorMessage(error, 'Failed to start run'));
      }
    } finally {
      if (submissionId.current === submissionAtStart) {
        submissionInFlight.current = false;
        if (dialogSession.current === sessionAtStart) {
          setSubmitting(false);
        }
      }
    }
  };

  const retryButton = (onRetry: () => void) => (
    <Button color="inherit" size="small" onClick={onRetry}>
      Retry
    </Button>
  );

  return (
    <Dialog open={open} onClose={handleClose} maxWidth="sm" fullWidth>
      <DialogTitle>Run Test Suite</DialogTitle>
      <DialogContent>
        <Box sx={{ pt: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
          <Typography variant="body2" color="text.secondary">
            Configure the test run by selecting an environment, endpoint, and mapping spec.
          </Typography>

          {submitError && <Alert severity="error">{submitError}</Alert>}

          <FormControl fullWidth disabled={environmentLoading || !projectId}>
            <InputLabel id="run-environment-label">Environment</InputLabel>
            <Select
              labelId="run-environment-label"
              label="Environment"
              value={selectedEnv}
              onChange={(event) => {
                clearSelection('environment');
                setSelectedEnv(event.target.value);
              }}
            >
              <MenuItem value=""><em>Select an environment</em></MenuItem>
              {environments.map((environment) => (
                <MenuItem key={environment.id} value={environment.id}>
                  {environment.name}
                </MenuItem>
              ))}
            </Select>
            <FormHelperText>
              {environmentLoading ? <CircularProgress size={16} aria-label="Loading environments" />
                : environmentError ? 'Unable to load environments'
                  : `${environments.length} environment${environments.length === 1 ? '' : 's'} available`}
            </FormHelperText>
          </FormControl>

          {environmentError && (
            <Alert severity="error" action={retryButton(() => void loadEnvironments())}>
              {environmentError}
            </Alert>
          )}
          {!environmentLoading && !environmentError && environments.length === 0 && projectId && (
            <Alert severity="info" action={(
              <Button component={RouterLink} to={`/projects/${projectId}`} size="small">
                Create Environment
              </Button>
            )}>
              No environments are available for this project.
            </Alert>
          )}

          <FormControl fullWidth disabled={!selectedEnv || endpointLoading}>
            <InputLabel id="run-endpoint-label">Endpoint</InputLabel>
            <Select
              labelId="run-endpoint-label"
              label="Endpoint"
              value={selectedEndpoint}
              onChange={(event) => {
                clearSelection('endpoint');
                setSelectedEndpoint(event.target.value);
              }}
            >
              <MenuItem value=""><em>Select an endpoint</em></MenuItem>
              {endpoints.map((endpoint) => (
                <MenuItem key={endpoint.id} value={endpoint.id}>
                  {endpoint.name} ({endpoint.httpMethod} {endpoint.path})
                </MenuItem>
              ))}
            </Select>
            <FormHelperText>
              {endpointLoading ? <CircularProgress size={16} aria-label="Loading endpoints" />
                : `${endpoints.length} endpoint${endpoints.length === 1 ? '' : 's'} available`}
            </FormHelperText>
          </FormControl>

          {endpointError && (
            <Alert severity="error" action={retryButton(() => void loadEndpoints(selectedEnv))}>
              {endpointError}
            </Alert>
          )}
          {!endpointLoading && !endpointError && selectedEnv && endpoints.length === 0 && (
            <Alert severity="info" action={(
              <Button component={RouterLink} to={`/environments/${selectedEnv}`} size="small">
                Add Endpoint
              </Button>
            )}>
              No endpoints are available for this environment.
            </Alert>
          )}

          <FormControl fullWidth disabled={!selectedEndpoint || mappingLoading}>
            <InputLabel id="run-mapping-label">Mapping Spec</InputLabel>
            <Select
              labelId="run-mapping-label"
              label="Mapping Spec"
              value={selectedMapping}
              onChange={(event) => {
                setSubmitError('');
                setSelectedMapping(event.target.value);
              }}
            >
              <MenuItem value=""><em>Select a mapping spec</em></MenuItem>
              {mappings.map((mapping) => (
                <MenuItem key={mapping.id} value={mapping.id}>
                  {mapping.name}{mapping.isDefault ? ' (default)' : ''}
                </MenuItem>
              ))}
            </Select>
            <FormHelperText>
              {mappingLoading ? <CircularProgress size={16} aria-label="Loading mapping specs" />
                : `${mappings.length} mapping spec${mappings.length === 1 ? '' : 's'} available`}
            </FormHelperText>
          </FormControl>

          {mappingError && (
            <Alert severity="error" action={retryButton(() => void loadMappings(selectedEndpoint))}>
              {mappingError}
            </Alert>
          )}
          {!mappingLoading && !mappingError && selectedEndpoint && mappings.length === 0 && (
            <Alert severity="info" action={(
              <Button component={RouterLink} to={`/environments/${selectedEnv}`} size="small">
                Create Mapping
              </Button>
            )}>
              No mapping specs are available for this endpoint. Create one in the Mapping Wizard.
            </Alert>
          )}

          <TextField
            fullWidth
            label="Git Commit Hash (optional)"
            value={gitCommit}
            onChange={(event) => setGitCommit(event.target.value)}
            disabled={submitting}
          />
        </Box>
      </DialogContent>
      <DialogActions>
        <Button onClick={handleClose} disabled={submitting}>Cancel</Button>
        <Button
          variant="contained"
          onClick={() => void handleStartRun()}
          disabled={submitting || environmentLoading || endpointLoading || mappingLoading
            || !selectedEnv || !selectedEndpoint || !selectedMapping}
        >
          {submitting ? 'Starting…' : 'Start Run'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export { RunConfigDialog };
export type { RunConfigDialogProps };
