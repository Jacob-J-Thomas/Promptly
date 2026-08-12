import React, { useCallback, useEffect, useState } from 'react';
import {
  Container,
  Typography,
  Box,
  Card,
  CardContent,
  Chip,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Paper,
  CircularProgress,
  Alert,
  Button,
  Collapse,
  IconButton,
} from '@mui/material';
import {
  CheckCircle,
  Error,
  Warning,
  ArrowBack,
  ExpandMore,
  ExpandLess,
  Refresh,
} from '@mui/icons-material';
import { useParams, useNavigate } from 'react-router-dom';
import { runsApi, type TestRun, type TestRunResult } from '../api/runs';
import { Layout } from '../components/Layout';
import { getApiErrorMessage } from '../api/errors';

interface RunSummary {
  passRate: number;
  passed: number;
  failed: number;
  errors: number;
  avgLatencyMs: number;
  totalTokens: number;
  totalCost: number;
}

const isFiniteNumber = (value: unknown): value is number => (
  typeof value === 'number' && Number.isFinite(value)
);

const parseSummary = (summaryJson?: string): RunSummary | null => {
  if (!summaryJson) return null;

  try {
    const parsed: unknown = JSON.parse(summaryJson);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      return null;
    }

    const summary = parsed as Record<string, unknown>;
    if (!isFiniteNumber(summary.passRate)
      || !isFiniteNumber(summary.passed)
      || !isFiniteNumber(summary.failed)
      || !isFiniteNumber(summary.errors)
      || !isFiniteNumber(summary.avgLatencyMs)
      || !isFiniteNumber(summary.totalTokens)
      || !isFiniteNumber(summary.totalCost)) {
      return null;
    }

    return {
      passRate: summary.passRate,
      passed: summary.passed,
      failed: summary.failed,
      errors: summary.errors,
      avgLatencyMs: summary.avgLatencyMs,
      totalTokens: summary.totalTokens,
      totalCost: summary.totalCost,
    };
  } catch {
    return null;
  }
};

const formatJsonForDisplay = (value: string): string => {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
};

export const RunDetail: React.FC = () => {
  const { runId } = useParams<{ runId: string }>();
  const navigate = useNavigate();
  const [run, setRun] = useState<TestRun | null>(null);
  const [results, setResults] = useState<TestRunResult[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [expandedResults, setExpandedResults] = useState<Set<string>>(new Set());
  const [autoRefresh, setAutoRefresh] = useState(true);

  const loadRunData = useCallback(async () => {
    if (!runId) {
      setError('Run ID is required');
      setLoading(false);
      setAutoRefresh(false);
      return;
    }

    try {
      setLoading(true);
      const [runData, resultsData] = await Promise.all([
        runsApi.getById(runId),
        runsApi.getResults(runId),
      ]);
      setRun(runData);
      setResults(resultsData);
      setError('');

      // Stop auto-refresh if completed
      if (runData.status === 'Completed' || runData.status === 'Failed') {
        setAutoRefresh(false);
      }
    } catch (loadError: unknown) {
      setError(getApiErrorMessage(loadError, 'Failed to load run data'));
      setAutoRefresh(false);
    } finally {
      setLoading(false);
    }
  }, [runId]);

  useEffect(() => {
    void loadRunData();
  }, [loadRunData]);

  useEffect(() => {
    if (autoRefresh && run && (run.status === 'Queued' || run.status === 'Running')) {
      const interval = setInterval(() => {
        void loadRunData();
      }, 5000);
      return () => clearInterval(interval);
    }
  }, [autoRefresh, loadRunData, run]);

  const toggleExpand = (resultId: string) => {
    const newExpanded = new Set(expandedResults);
    if (newExpanded.has(resultId)) {
      newExpanded.delete(resultId);
    } else {
      newExpanded.add(resultId);
    }
    setExpandedResults(newExpanded);
  };

  const getStatusIcon = (status: string) => {
    switch (status) {
      case 'Pass':
        return <CheckCircle color="success" />;
      case 'Fail':
        return <Error color="error" />;
      case 'Error':
        return <Warning color="warning" />;
      default:
        return undefined;
    }
  };

  const getStatusColor = (status: string): "default" | "success" | "error" | "warning" => {
    switch (status) {
      case 'Pass':
      case 'Completed':
        return 'success';
      case 'Fail':
      case 'Failed':
        return 'error';
      case 'Error':
        return 'warning';
      default:
        return 'default';
    }
  };

  if (loading && !run) {
    return (
      <Layout>
        <Container maxWidth="lg">
          <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
            <CircularProgress />
          </Box>
        </Container>
      </Layout>
    );
  }

  if (!run) {
    return (
      <Layout>
        <Container maxWidth="lg">
          <Alert severity="error" sx={{ mt: 4 }}>
            {error || 'Test run not found'}
          </Alert>
        </Container>
      </Layout>
    );
  }

  const summary = parseSummary(run.summaryJson);

  return (
    <Layout>
      <Container maxWidth="lg">
        <Box sx={{ mt: 4, mb: 4 }}>
          <Button
            startIcon={<ArrowBack />}
            onClick={() => navigate(`/suites/${run.suiteId}`)}
            sx={{ mb: 2 }}
          >
            Back to Suite
          </Button>

          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
            <Typography variant="h4" component="h1">
              Test Run
            </Typography>
            {(run.status === 'Queued' || run.status === 'Running') && (
              <Button
                startIcon={<Refresh />}
                onClick={loadRunData}
                variant="outlined"
              >
                Refresh
              </Button>
            )}
          </Box>

          {error && (
            <Alert severity="error" sx={{ mb: 3 }} onClose={() => setError('')}>
              {error}
            </Alert>
          )}

          {/* Run Status Card */}
          <Card sx={{ mb: 3 }}>
            <CardContent>
              <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 2 }}>
                <Typography variant="h6">Run Status</Typography>
                <Chip
                  label={run.status}
                  color={getStatusColor(run.status)}
                  icon={(run.status === 'Queued' || run.status === 'Running') ? <CircularProgress size={16} /> : undefined}
                />
              </Box>

              <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 2 }}>
                <Box>
                  <Typography color="text.secondary" variant="body2">Created</Typography>
                  <Typography>{new Date(run.createdAt).toLocaleString()}</Typography>
                </Box>
                {run.startedAt && (
                  <Box>
                    <Typography color="text.secondary" variant="body2">Started</Typography>
                    <Typography>{new Date(run.startedAt).toLocaleString()}</Typography>
                  </Box>
                )}
                {run.completedAt && (
                  <Box>
                    <Typography color="text.secondary" variant="body2">Completed</Typography>
                    <Typography>{new Date(run.completedAt).toLocaleString()}</Typography>
                  </Box>
                )}
                {run.createdByUserId && (
                  <Box>
                    <Typography color="text.secondary" variant="body2">Triggered By</Typography>
                    <Typography>{run.createdByUserId}</Typography>
                  </Box>
                )}
              </Box>

              {summary && (
                <Box sx={{ mt: 3, p: 2, bgcolor: 'background.default', borderRadius: 1 }}>
                  <Typography variant="subtitle2" gutterBottom>Summary</Typography>
                  <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: 2 }}>
                    <Box>
                      <Typography color="text.secondary" variant="body2">Pass Rate</Typography>
                      <Typography variant="h6">{(summary.passRate * 100).toFixed(1)}%</Typography>
                    </Box>
                    <Box>
                      <Typography color="text.secondary" variant="body2">Passed</Typography>
                      <Typography variant="h6" color="success.main">{summary.passed}</Typography>
                    </Box>
                    <Box>
                      <Typography color="text.secondary" variant="body2">Failed</Typography>
                      <Typography variant="h6" color="error.main">{summary.failed}</Typography>
                    </Box>
                    {summary.errors > 0 && (
                      <Box>
                        <Typography color="text.secondary" variant="body2">Errors</Typography>
                        <Typography variant="h6" color="warning.main">{summary.errors}</Typography>
                      </Box>
                    )}
                    <Box>
                      <Typography color="text.secondary" variant="body2">Avg Latency</Typography>
                      <Typography variant="h6">{summary.avgLatencyMs}ms</Typography>
                    </Box>
                    {summary.totalTokens > 0 && (
                      <Box>
                        <Typography color="text.secondary" variant="body2">Total Tokens</Typography>
                        <Typography variant="h6">{summary.totalTokens.toLocaleString()}</Typography>
                      </Box>
                    )}
                    {summary.totalCost > 0 && (
                      <Box>
                        <Typography color="text.secondary" variant="body2">Total Cost</Typography>
                        <Typography variant="h6">${summary.totalCost.toFixed(4)}</Typography>
                      </Box>
                    )}
                  </Box>
                </Box>
              )}

              {run.errorMessage && (
                <Alert severity="error" sx={{ mt: 2 }}>
                  {run.errorMessage}
                </Alert>
              )}
            </CardContent>
          </Card>

          {/* Test Results Table */}
          <Card>
            <CardContent>
              <Typography variant="h6" gutterBottom>
                Test Results ({results.length})
              </Typography>

              <TableContainer component={Paper} variant="outlined">
                <Table>
                  <TableHead>
                    <TableRow>
                      <TableCell width="50px"></TableCell>
                      <TableCell>Test Case</TableCell>
                      <TableCell align="center">Status</TableCell>
                      <TableCell align="center">Expectations</TableCell>
                      <TableCell align="right">Actions</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {results.map((result) => (
                      <React.Fragment key={result.id}>
                        <TableRow>
                          <TableCell>
                            <IconButton
                              size="small"
                              onClick={() => toggleExpand(result.id)}
                            >
                              {expandedResults.has(result.id) ? <ExpandLess /> : <ExpandMore />}
                            </IconButton>
                          </TableCell>
                          <TableCell>
                            <Typography variant="body2" fontWeight="medium">
                              {result.testCaseName || result.testCaseExternalId}
                            </Typography>
                            {result.testCaseExternalId && result.testCaseName && (
                              <Typography variant="caption" color="text.secondary">
                                {result.testCaseExternalId}
                              </Typography>
                            )}
                          </TableCell>
                          <TableCell align="center">
                            <Chip
                              icon={getStatusIcon(result.status)}
                              label={result.status}
                              color={getStatusColor(result.status)}
                              size="small"
                            />
                          </TableCell>
                          <TableCell align="center">
                            <Typography variant="body2">
                              {result.passedExpectations} / {result.passedExpectations + result.failedExpectations + result.errorExpectations}
                            </Typography>
                          </TableCell>
                          <TableCell align="right">
                            <Button
                              size="small"
                              onClick={() => toggleExpand(result.id)}
                            >
                              Details
                            </Button>
                          </TableCell>
                        </TableRow>
                        <TableRow>
                          <TableCell colSpan={5} sx={{ py: 0, border: 0 }}>
                            <Collapse in={expandedResults.has(result.id)} timeout="auto" unmountOnExit>
                              <Box sx={{ p: 2, bgcolor: 'background.default' }}>
                                {result.failureReasons.length > 0 && (
                                  <Alert severity="error" sx={{ mb: 2 }}>
                                    {result.failureReasons.join('\n')}
                                  </Alert>
                                )}
                                {result.traceJson && (
                                  <Box sx={{ mt: 2 }}>
                                    <Typography variant="subtitle2" gutterBottom>Trace</Typography>
                                    <Paper variant="outlined" sx={{ p: 2, maxHeight: 300, overflow: 'auto' }}>
                                      <pre style={{ margin: 0, fontSize: '12px' }}>
                                        {formatJsonForDisplay(result.traceJson)}
                                      </pre>
                                    </Paper>
                                  </Box>
                                )}
                              </Box>
                            </Collapse>
                          </TableCell>
                        </TableRow>
                      </React.Fragment>
                    ))}
                  </TableBody>
                </Table>
              </TableContainer>
            </CardContent>
          </Card>
        </Box>
      </Container>
    </Layout>
  );
};
