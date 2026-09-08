import React, { useCallback, useEffect, useRef, useState } from 'react';
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
import { Layout } from '../components/Layout';
import { RunConfigDialog } from '../components/RunConfigDialog';
import { TestCaseDialog } from '../components/TestCaseDialog';
import { getApiErrorMessage } from '../api/errors';

export const SuiteDetail: React.FC = () => {
  const { suiteId } = useParams<{ suiteId: string }>();
  const navigate = useNavigate();

  const [suite, setSuite] = useState<TestSuite | null>(null);
  const [tests, setTests] = useState<TestCase[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const loadRequestRef = useRef(0);

  const [runDialogOpen, setRunDialogOpen] = useState(false);
  const [createTestDialogOpen, setCreateTestDialogOpen] = useState(false);
  const [importDialogOpen, setImportDialogOpen] = useState(false);

  const [anchorEl, setAnchorEl] = useState<null | HTMLElement>(null);
  const [selectedTest, setSelectedTest] = useState<TestCase | null>(null);

  const loadSuiteData = useCallback(async () => {
    const requestId = ++loadRequestRef.current;
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
      if (requestId !== loadRequestRef.current) {
        return;
      }
      setSuite(suiteData);
      setTests(testsData);
      setError('');
    } catch (loadError: unknown) {
      if (requestId === loadRequestRef.current) {
        setError(getApiErrorMessage(loadError, 'Failed to load suite data'));
      }
    } finally {
      if (requestId === loadRequestRef.current) {
        setLoading(false);
      }
    }
  }, [suiteId]);

  useEffect(() => {
    void loadSuiteData();
  }, [loadSuiteData]);

  useEffect(() => {
    setAnchorEl(null);
    setSelectedTest(null);
    setCreateTestDialogOpen(false);
  }, [suiteId]);

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
    setAnchorEl(null);
    setCreateTestDialogOpen(true);
  };

  const handleCreateTest = () => {
    setSelectedTest(null);
    setCreateTestDialogOpen(true);
  };

  const handleCloseTestDialog = () => {
    setCreateTestDialogOpen(false);
    setSelectedTest(null);
  };

  const handleTestSaved = async () => {
    await loadSuiteData();
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
                      onClick={handleCreateTest}
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
                onClick={handleCreateTest}
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
            <MenuItem onClick={handleEditTest}>
              <Edit fontSize="small" sx={{ mr: 1 }} />
              Edit
            </MenuItem>
            <MenuItem onClick={handleDeleteTest}>
              <Delete fontSize="small" sx={{ mr: 1 }} />
              Delete
            </MenuItem>
          </Menu>

          <RunConfigDialog
            open={runDialogOpen}
            onClose={() => setRunDialogOpen(false)}
            projectId={suite?.projectId ?? ''}
            suiteId={suiteId!}
            onRunStarted={(runId) => navigate(`/runs/${runId}`)}
          />

          <TestCaseDialog
            open={createTestDialogOpen}
            onClose={handleCloseTestDialog}
            suiteId={suiteId!}
            testCase={selectedTest}
            onSaved={handleTestSaved}
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
