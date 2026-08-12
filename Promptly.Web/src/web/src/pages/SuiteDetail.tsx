import React, { useCallback, useEffect, useState } from 'react';
import {
  Container,
  Typography,
  Box,
  Button,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Paper,
  Chip,
  CircularProgress,
  Alert,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  TextField,
  IconButton,
  Menu,
  MenuItem,
  Card,
  CardContent,
  Grid,
  Divider,
} from '@mui/material';
import {
  Add,
  PlayArrow,
  Upload,
  Download,
  MoreVert,
  Edit,
  Delete,
  ArrowBack,
  History,
} from '@mui/icons-material';
import { useParams, useNavigate } from 'react-router-dom';
import { suitesApi, type TestSuite } from '../api/suites';
import { testsApi, type TestCase } from '../api/tests';
import { runsApi } from '../api/runs';
import { Layout } from '../components/Layout';
import { getApiErrorMessage } from '../api/errors';

export const SuiteDetail: React.FC = () => {
  const { suiteId } = useParams<{ suiteId: string }>();
  const navigate = useNavigate();

  const [suite, setSuite] = useState<TestSuite | null>(null);
  const [tests, setTests] = useState<TestCase[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [runDialogOpen, setRunDialogOpen] = useState(false);
  const [createTestDialogOpen, setCreateTestDialogOpen] = useState(false);
  const [importDialogOpen, setImportDialogOpen] = useState(false);

  const [anchorEl, setAnchorEl] = useState<null | HTMLElement>(null);
  const [selectedTest, setSelectedTest] = useState<TestCase | null>(null);

  const loadSuiteData = useCallback(async () => {
    if (!suiteId) {
      setError('Suite ID is required');
      setLoading(false);
      return;
    }

    try {
      setLoading(true);
      const [suiteData, testsData] = await Promise.all([
        suitesApi.getById(suiteId),
        testsApi.getBySuite(suiteId)
      ]);
      setSuite(suiteData);
      setTests(testsData);
      setError('');
    } catch (loadError: unknown) {
      setError(getApiErrorMessage(loadError, 'Failed to load suite data'));
    } finally {
      setLoading(false);
    }
  }, [suiteId]);

  useEffect(() => {
    void loadSuiteData();
  }, [loadSuiteData]);

  const handleRunSuite = () => {
    setRunDialogOpen(true);
  };

  const handleTestMenu = (event: React.MouseEvent<HTMLElement>, test: TestCase) => {
    setAnchorEl(event.currentTarget);
    setSelectedTest(test);
  };

  const handleCloseMenu = () => {
    setAnchorEl(null);
    setSelectedTest(null);
  };

  const handleEditTest = () => {
    // Test editing is tracked separately; keep the unavailable action non-destructive.
    handleCloseMenu();
  };

  const handleDeleteTest = async () => {
    if (selectedTest) {
      try {
        await testsApi.delete(selectedTest.id);
        await loadSuiteData();
      } catch (deleteError: unknown) {
        setError(getApiErrorMessage(deleteError, 'Failed to delete test'));
      }
    }
    handleCloseMenu();
  };

  const handleExportTests = async () => {
    try {
      const yamlData = await testsApi.exportYaml(suiteId!);
      const blob = new Blob([yamlData], { type: 'text/yaml' });
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${suite?.name || 'tests'}.yaml`;
      a.click();
      window.URL.revokeObjectURL(url);
    } catch (exportError: unknown) {
      setError(getApiErrorMessage(exportError, 'Failed to export tests'));
    }
  };

  if (loading) {
    return (
      <Layout>
        <Container maxWidth="lg">
          <Box sx={{ display: 'flex', justifyContent: 'center', mt: 4 }}>
            <CircularProgress />
          </Box>
        </Container>
      </Layout>
    );
  }

  if (error) {
    return (
      <Layout>
        <Container maxWidth="lg">
          <Box sx={{ mt: 4 }}>
            <Alert severity="error">{error}</Alert>
          </Box>
        </Container>
      </Layout>
    );
  }

  return (
    <Layout>
      <Container maxWidth="lg">
        <Box sx={{ mt: 4, mb: 4 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', mb: 3 }}>
            <IconButton onClick={() => navigate(-1)} sx={{ mr: 2 }}>
              <ArrowBack />
            </IconButton>
            <Typography variant="h4" sx={{ flexGrow: 1 }}>
              {suite?.name}
            </Typography>
            <Button
              variant="contained"
              color="primary"
              startIcon={<PlayArrow />}
              onClick={handleRunSuite}
              disabled={tests.length === 0}
            >
              Run Suite
            </Button>
          </Box>

          {suite?.description && (
            <Typography variant="body1" color="text.secondary" sx={{ mb: 3 }}>
              {suite.description}
            </Typography>
          )}

          <Card sx={{ mb: 3 }}>
            <CardContent>
              <Grid container spacing={3}>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <Typography variant="subtitle2" color="text.secondary">
                    Total Tests
                  </Typography>
                  <Typography variant="h5">{tests.length}</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <Typography variant="subtitle2" color="text.secondary">
                    Created
                  </Typography>
                  <Typography variant="body2">
                    {suite?.createdAt ? new Date(suite.createdAt).toLocaleDateString() : '-'}
                  </Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <Box sx={{ display: 'flex', gap: 1 }}>
                    <Button
                      size="small"
                      startIcon={<Add />}
                      onClick={() => setCreateTestDialogOpen(true)}
                    >
                      Create Test
                    </Button>
                    <Button
                      size="small"
                      startIcon={<Upload />}
                      onClick={() => setImportDialogOpen(true)}
                    >
                      Import
                    </Button>
                    <Button
                      size="small"
                      startIcon={<Download />}
                      onClick={handleExportTests}
                      disabled={tests.length === 0}
                    >
                      Export
                    </Button>
                  </Box>
                </Grid>
              </Grid>
            </CardContent>
          </Card>

          <Divider sx={{ mb: 3 }} />

          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
            <Typography variant="h6">Test Cases</Typography>
            <Button
              startIcon={<History />}
              onClick={() => navigate(`/suites/${suiteId}/runs`)}
            >
              View Run History
            </Button>
          </Box>

          {tests.length === 0 ? (
            <Paper sx={{ p: 4, textAlign: 'center' }}>
              <Typography variant="body1" color="text.secondary" gutterBottom>
                No test cases yet
              </Typography>
              <Button
                variant="contained"
                startIcon={<Add />}
                onClick={() => setCreateTestDialogOpen(true)}
                sx={{ mt: 2 }}
              >
                Create First Test
              </Button>
            </Paper>
          ) : (
            <TableContainer component={Paper}>
              <Table>
                <TableHead>
                  <TableRow>
                    <TableCell>External ID</TableCell>
                    <TableCell>Name</TableCell>
                    <TableCell>Description</TableCell>
                    <TableCell>Created</TableCell>
                    <TableCell align="right">Actions</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {tests.map((test) => (
                    <TableRow key={test.id} hover>
                      <TableCell>
                        <Chip label={test.externalId} size="small" variant="outlined" />
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2" fontWeight="medium">
                          {test.name}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2" color="text.secondary" noWrap sx={{ maxWidth: 300 }}>
                          {test.description || '-'}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2">
                          {new Date(test.createdAt).toLocaleDateString()}
                        </Typography>
                      </TableCell>
                      <TableCell align="right">
                        <IconButton
                          size="small"
                          onClick={(e) => handleTestMenu(e, test)}
                        >
                          <MoreVert />
                        </IconButton>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>
          )}

          <Menu
            anchorEl={anchorEl}
            open={Boolean(anchorEl)}
            onClose={handleCloseMenu}
          >
            <MenuItem onClick={handleEditTest} disabled>
              <Edit fontSize="small" sx={{ mr: 1 }} />
              Edit (coming soon)
            </MenuItem>
            <MenuItem onClick={handleDeleteTest}>
              <Delete fontSize="small" sx={{ mr: 1 }} />
              Delete
            </MenuItem>
          </Menu>

          <RunConfigDialog
            open={runDialogOpen}
            onClose={() => setRunDialogOpen(false)}
            suiteId={suiteId!}
            onRunStarted={(runId) => navigate(`/runs/${runId}`)}
          />

          <CreateTestDialog
            open={createTestDialogOpen}
            onClose={() => setCreateTestDialogOpen(false)}
            suiteId={suiteId!}
            onTestCreated={loadSuiteData}
          />

          <ImportTestsDialog
            open={importDialogOpen}
            onClose={() => setImportDialogOpen(false)}
            suiteId={suiteId!}
            onImportComplete={loadSuiteData}
          />
        </Box>
      </Container>
    </Layout>
  );
};

// Run Configuration Dialog Component
interface RunConfigDialogProps {
  open: boolean;
  onClose: () => void;
  suiteId: string;
  onRunStarted: (runId: string) => void;
}

const RunConfigDialog: React.FC<RunConfigDialogProps> = ({ open, onClose, suiteId, onRunStarted }) => {
  const selectedEnv = '';
  const selectedEndpoint = '';
  const selectedMapping = '';
  const [gitCommit, setGitCommit] = useState('');
  const [loading, setLoading] = useState(false);

  // TODO: Load environments, endpoints, and mappings

  const handleStartRun = async () => {
    try {
      setLoading(true);
      const run = await runsApi.queue({
        suiteId,
        environmentId: selectedEnv,
        endpointId: selectedEndpoint,
        mappingSpecId: selectedMapping,
        gitCommitHash: gitCommit || undefined,
      });
      onRunStarted(run.id);
      onClose();
    } catch (err) {
      console.error('Failed to start run:', err);
    } finally {
      setLoading(false);
    }
  };

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Run Test Suite</DialogTitle>
      <DialogContent>
        <Box sx={{ pt: 2 }}>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Configure the test run by selecting an environment, endpoint, and mapping spec.
          </Typography>
          <Typography variant="body2" color="info.main">
            Note: Environment/endpoint/mapping selection UI coming soon.
            This is a placeholder dialog.
          </Typography>
          <TextField
            fullWidth
            label="Git Commit Hash (optional)"
            value={gitCommit}
            onChange={(e) => setGitCommit(e.target.value)}
            sx={{ mt: 2 }}
          />
        </Box>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          onClick={handleStartRun}
          disabled={loading || !selectedEnv || !selectedEndpoint || !selectedMapping}
        >
          Start Run
        </Button>
      </DialogActions>
    </Dialog>
  );
};

// Create Test Dialog Component
interface CreateTestDialogProps {
  open: boolean;
  onClose: () => void;
  suiteId: string;
  onTestCreated: () => void;
}

const CreateTestDialog: React.FC<CreateTestDialogProps> = ({ open, onClose, suiteId, onTestCreated }) => {
  const [externalId, setExternalId] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleClose = () => {
    setExternalId('');
    setName('');
    setDescription('');
    setError('');
    onClose();
  };

  const handleCreate = async () => {
    try {
      setLoading(true);
      setError('');
      await testsApi.create(suiteId, {
        externalId,
        name,
        description,
        inputSpecJson: JSON.stringify({ messages: [] }),
        expectationsJson: JSON.stringify([]),
      });
      onTestCreated();
      handleClose();
    } catch (createError: unknown) {
      setError(getApiErrorMessage(createError, 'Failed to create test'));
    } finally {
      setLoading(false);
    }
  };

  return (
    <Dialog open={open} onClose={handleClose} maxWidth="sm" fullWidth>
      <DialogTitle>Create Test Case</DialogTitle>
      <DialogContent>
        <Box sx={{ pt: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
          {error && <Alert severity="error">{error}</Alert>}
          <TextField
            label="External ID"
            value={externalId}
            onChange={(e) => setExternalId(e.target.value)}
            required
            fullWidth
          />
          <TextField
            label="Name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            required
            fullWidth
          />
          <TextField
            label="Description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            multiline
            rows={3}
            fullWidth
          />
        </Box>
      </DialogContent>
      <DialogActions>
        <Button onClick={handleClose}>Cancel</Button>
        <Button
          variant="contained"
          onClick={handleCreate}
          disabled={loading || !externalId || !name}
        >
          Create
        </Button>
      </DialogActions>
    </Dialog>
  );
};

// Import Tests Dialog Component
interface ImportTestsDialogProps {
  open: boolean;
  onClose: () => void;
  suiteId: string;
  onImportComplete: () => void;
}

const ImportTestsDialog: React.FC<ImportTestsDialogProps> = ({ open, onClose, suiteId, onImportComplete }) => {
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<string>('');

  const handleFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    if (event.target.files && event.target.files[0]) {
      setFile(event.target.files[0]);
      setResult('');
    }
  };

  const handleImport = async () => {
    if (!file) return;

    try {
      setLoading(true);
      await testsApi.importYaml(suiteId, file);
      setResult('Tests imported successfully!');
      onImportComplete();
      setTimeout(() => {
        onClose();
        setFile(null);
        setResult('');
      }, 1500);
    } catch (importError: unknown) {
      setResult(getApiErrorMessage(importError, 'Failed to import tests'));
    } finally {
      setLoading(false);
    }
  };

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Import Tests from YAML</DialogTitle>
      <DialogContent>
        <Box sx={{ pt: 2 }}>
          <input
            type="file"
            accept=".yaml,.yml"
            onChange={handleFileChange}
            style={{ marginBottom: 16 }}
          />
          {result && (
            <Alert severity={result.includes('success') ? 'success' : 'error'} sx={{ mt: 2 }}>
              {result}
            </Alert>
          )}
        </Box>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          onClick={handleImport}
          disabled={loading || !file}
        >
          Import
        </Button>
      </DialogActions>
    </Dialog>
  );
};
