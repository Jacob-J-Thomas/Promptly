import React, { useState, useEffect } from 'react';
import {
  Container,
  Typography,
  Box,
  Button,
  Card,
  CardContent,
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
} from '@mui/material';
import { ArrowBack, Visibility } from '@mui/icons-material';
import { useParams, useNavigate } from 'react-router-dom';
import { runsApi, type TestRun } from '../api/runs';
import { suitesApi, type TestSuite } from '../api/suites';
import { Layout } from '../components/Layout';

export const RunsList: React.FC = () => {
  const { suiteId } = useParams<{ suiteId: string }>();
  const navigate = useNavigate();

  const [suite, setSuite] = useState<TestSuite | null>(null);
  const [runs, setRuns] = useState<TestRun[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    if (suiteId) {
      loadData();
    }
  }, [suiteId]);

  const loadData = async () => {
    try {
      setLoading(true);
      const [suiteData, runsData] = await Promise.all([
        suitesApi.getById(suiteId!),
        runsApi.getBySuite(suiteId!),
      ]);
      setSuite(suiteData);
      setRuns(runsData);
      setError('');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load run history');
    } finally {
      setLoading(false);
    }
  };

  const getStatusColor = (status: string) => {
    switch (status.toLowerCase()) {
      case 'completed':
        return 'success';
      case 'running':
        return 'info';
      case 'failed':
        return 'error';
      case 'queued':
        return 'warning';
      default:
        return 'default';
    }
  };

  const formatDate = (dateString: string) => {
    const date = new Date(dateString);
    return date.toLocaleString();
  };

  if (loading) {
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

  return (
    <Layout>
      <Container maxWidth="lg">
        <Box sx={{ mt: 4, mb: 4 }}>
          <Button
            startIcon={<ArrowBack />}
            onClick={() => navigate(`/suites/${suiteId}`)}
            sx={{ mb: 2 }}
          >
            Back to Suite
          </Button>

          <Typography variant="h4" component="h1" gutterBottom>
            Run History
          </Typography>

          {suite && (
            <Typography variant="body1" color="text.secondary" sx={{ mb: 3 }}>
              {suite.name}
            </Typography>
          )}

          {error && (
            <Alert severity="error" sx={{ mb: 3 }} onClose={() => setError('')}>
              {error}
            </Alert>
          )}

          {runs.length === 0 ? (
            <Card>
              <CardContent sx={{ textAlign: 'center', py: 8 }}>
                <Typography variant="h6" color="text.secondary" gutterBottom>
                  No runs yet
                </Typography>
                <Typography variant="body2" color="text.secondary">
                  Run this test suite to see results here
                </Typography>
                <Button
                  variant="contained"
                  onClick={() => navigate(`/suites/${suiteId}`)}
                  sx={{ mt: 2 }}
                >
                  Go to Suite
                </Button>
              </CardContent>
            </Card>
          ) : (
            <TableContainer component={Paper}>
              <Table>
                <TableHead>
                  <TableRow>
                    <TableCell>Status</TableCell>
                    <TableCell>Started</TableCell>
                    <TableCell>Completed</TableCell>
                    <TableCell>Git Commit</TableCell>
                    <TableCell align="right">Actions</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {runs.map((run) => (
                    <TableRow key={run.id} hover>
                      <TableCell>
                        <Chip
                          label={run.status}
                          color={getStatusColor(run.status) as any}
                          size="small"
                        />
                      </TableCell>
                      <TableCell>
                        {run.startedAt ? formatDate(run.startedAt) : '-'}
                      </TableCell>
                      <TableCell>
                        {run.completedAt ? formatDate(run.completedAt) : '-'}
                      </TableCell>
                      <TableCell>
                        {run.gitCommitHash ? (
                          <Typography variant="body2" sx={{ fontFamily: 'monospace', fontSize: '0.85rem' }}>
                            {run.gitCommitHash.substring(0, 8)}
                          </Typography>
                        ) : (
                          '-'
                        )}
                      </TableCell>
                      <TableCell align="right">
                        <Button
                          size="small"
                          startIcon={<Visibility />}
                          onClick={() => navigate(`/runs/${run.id}`)}
                        >
                          View
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>
          )}
        </Box>
      </Container>
    </Layout>
  );
};
