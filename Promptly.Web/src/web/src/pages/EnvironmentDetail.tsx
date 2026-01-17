import React, { useState, useEffect } from 'react';
import {
  Container,
  Typography,
  Box,
  Button,
  Card,
  CardContent,
  List,
  ListItem,
  ListItemText,
  CircularProgress,
  Alert,
  IconButton,
  Chip,
  Paper,
  Collapse,
} from '@mui/material';
import {
  Add,
  ArrowBack,
  Visibility,
  VisibilityOff,
  Delete,
  Edit,
  Api,
  ExpandMore,
  ExpandLess,
} from '@mui/icons-material';
import { useParams, useNavigate } from 'react-router-dom';
import { environmentsApi, type Environment } from '../api/environments';
import { endpointsApi, type Endpoint } from '../api/endpoints';
import { Layout } from '../components/Layout';
import { MappingWizard } from '../components/MappingWizard';

export const EnvironmentDetail: React.FC = () => {
  const { environmentId } = useParams<{ environmentId: string }>();
  const navigate = useNavigate();
  const [environment, setEnvironment] = useState<Environment | null>(null);
  const [endpoints, setEndpoints] = useState<Endpoint[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showHeaders, setShowHeaders] = useState(false);
  const [wizardOpen, setWizardOpen] = useState(false);

  useEffect(() => {
    if (environmentId) {
      loadEnvironmentData();
    }
  }, [environmentId]);

  const loadEnvironmentData = async () => {
    try {
      setLoading(true);
      const [envData, endpointsData] = await Promise.all([
        environmentsApi.getById(environmentId!),
        endpointsApi.getByEnvironment(environmentId!),
      ]);
      setEnvironment(envData);
      setEndpoints(endpointsData);
      setError('');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load environment data');
    } finally {
      setLoading(false);
    }
  };

  const handleWizardComplete = async (endpointId: string) => {
    await loadEnvironmentData();
  };

  const handleDeleteEndpoint = async (id: string) => {
    if (!window.confirm('Are you sure you want to delete this endpoint? This will affect test suites using it.')) {
      return;
    }

    try {
      await endpointsApi.delete(id);
      await loadEnvironmentData();
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to delete endpoint');
    }
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

  if (!environment) {
    return (
      <Layout>
        <Container maxWidth="lg">
          <Alert severity="error" sx={{ mt: 4 }}>
            Environment not found
          </Alert>
        </Container>
      </Layout>
    );
  }

  const parsedHeaders = environment.headersEncrypted
    ? JSON.parse(environment.headersEncrypted)
    : {};

  return (
    <Layout>
      <Container maxWidth="lg">
        <Box sx={{ mt: 4, mb: 4 }}>
          <Button
            startIcon={<ArrowBack />}
            onClick={() => navigate(`/projects/${environment.projectId}`)}
            sx={{ mb: 2 }}
          >
            Back to Project
          </Button>

          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
            <Box>
              <Typography variant="h4" component="h1" gutterBottom>
                {environment.name}
              </Typography>
              <Chip label={environment.baseUrl} icon={<Api />} />
            </Box>
          </Box>

          {error && (
            <Alert severity="error" sx={{ mb: 3 }} onClose={() => setError('')}>
              {error}
            </Alert>
          )}

          {/* Environment Details Card */}
          <Card sx={{ mb: 3 }}>
            <CardContent>
              <Typography variant="h6" gutterBottom>
                Environment Configuration
              </Typography>

              <Box sx={{ mb: 2 }}>
                <Typography color="text.secondary" variant="body2">
                  Base URL
                </Typography>
                <Typography variant="body1">{environment.baseUrl}</Typography>
              </Box>

              {environment.headersEncrypted && (
                <Box>
                  <Box sx={{ display: 'flex', alignItems: 'center', mb: 1 }}>
                    <Typography color="text.secondary" variant="body2">
                      Headers
                    </Typography>
                    <IconButton
                      size="small"
                      onClick={() => setShowHeaders(!showHeaders)}
                      sx={{ ml: 1 }}
                    >
                      {showHeaders ? <VisibilityOff fontSize="small" /> : <Visibility fontSize="small" />}
                    </IconButton>
                  </Box>

                  <Collapse in={showHeaders}>
                    <Paper variant="outlined" sx={{ p: 2, bgcolor: 'background.default' }}>
                      <pre style={{ margin: 0, fontSize: '12px', whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                        {JSON.stringify(parsedHeaders, null, 2)}
                      </pre>
                    </Paper>
                  </Collapse>

                  {!showHeaders && (
                    <Typography variant="body2" color="text.secondary">
                      Headers are encrypted (click eye icon to view)
                    </Typography>
                  )}
                </Box>
              )}

              {!environment.headersEncrypted && (
                <Typography variant="body2" color="text.secondary">
                  No custom headers configured
                </Typography>
              )}
            </CardContent>
          </Card>

          {/* Endpoints List */}
          <Card>
            <CardContent>
              <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Typography variant="h6">Endpoints ({endpoints.length})</Typography>
                <Button
                  variant="contained"
                  startIcon={<Add />}
                  onClick={() => setWizardOpen(true)}
                >
                  Add Endpoint with Wizard
                </Button>
              </Box>

              {endpoints.length === 0 ? (
                <Box sx={{ textAlign: 'center', py: 4 }}>
                  <Api sx={{ fontSize: 48, color: 'text.secondary', mb: 2 }} />
                  <Typography color="text.secondary" gutterBottom>
                    No endpoints configured
                  </Typography>
                  <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                    Endpoints define the API paths you want to test
                  </Typography>
                  <Button
                    variant="contained"
                    startIcon={<Add />}
                    onClick={() => setWizardOpen(true)}
                  >
                    Create First Endpoint
                  </Button>
                </Box>
              ) : (
                <List>
                  {endpoints.map((endpoint) => (
                    <Card key={endpoint.id} sx={{ mb: 2 }}>
                      <ListItem
                        secondaryAction={
                          <IconButton
                            edge="end"
                            color="error"
                            onClick={(e) => {
                              e.stopPropagation();
                              handleDeleteEndpoint(endpoint.id);
                            }}
                          >
                            <Delete />
                          </IconButton>
                        }
                      >
                        <ListItemText
                          primary={
                            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                              <Typography variant="body1" fontWeight="medium">
                                {endpoint.name}
                              </Typography>
                              <Chip label={endpoint.method} size="small" color="primary" variant="outlined" />
                            </Box>
                          }
                          secondary={
                            <Box>
                              <Typography variant="body2" color="text.secondary">
                                {endpoint.path}
                              </Typography>
                              <Typography variant="caption" color="text.secondary">
                                Timeout: {endpoint.timeoutSeconds}s
                              </Typography>
                            </Box>
                          }
                        />
                      </ListItem>
                    </Card>
                  ))}
                </List>
              )}
            </CardContent>
          </Card>
        </Box>

        {/* Mapping Wizard */}
        <MappingWizard
          open={wizardOpen}
          onClose={() => setWizardOpen(false)}
          environmentId={environmentId!}
          onComplete={handleWizardComplete}
        />
      </Container>
    </Layout>
  );
};
