import React, { useCallback, useEffect, useState } from 'react';
import {
  Container,
  Typography,
  Box,
  Tabs,
  Tab,
  Button,
  Card,
  CardContent,
  List,
  ListItemText,
  ListItemButton,
  CircularProgress,
  Alert,
  Chip,
} from '@mui/material';
import { Add, CloudQueue, Assignment, ArrowBack } from '@mui/icons-material';
import { useParams, useNavigate } from 'react-router-dom';
import { projectsApi, type Project } from '../api/projects';
import { environmentsApi, type Environment } from '../api/environments';
import { suitesApi, type TestSuite } from '../api/suites';
import { Layout } from '../components/Layout';
import { getApiErrorMessage } from '../api/errors';

interface TabPanelProps {
  children?: React.ReactNode;
  index: number;
  value: number;
}

const TabPanel: React.FC<TabPanelProps> = ({ children, value, index }) => {
  return (
    <div role="tabpanel" hidden={value !== index}>
      {value === index && <Box sx={{ py: 3 }}>{children}</Box>}
    </div>
  );
};

export const ProjectDetail: React.FC = () => {
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [project, setProject] = useState<Project | null>(null);
  const [environments, setEnvironments] = useState<Environment[]>([]);
  const [suites, setSuites] = useState<TestSuite[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [tabValue, setTabValue] = useState(0);

  const loadProjectData = useCallback(async () => {
    if (!projectId) {
      setError('Project ID is required');
      setLoading(false);
      return;
    }

    try {
      setLoading(true);
      const [projectData, envsData, suitesData] = await Promise.all([
        projectsApi.getById(projectId),
        environmentsApi.getByProject(projectId),
        suitesApi.getByProject(projectId),
      ]);
      setProject(projectData);
      setEnvironments(envsData);
      setSuites(suitesData);
      setError('');
    } catch (loadError: unknown) {
      setError(getApiErrorMessage(loadError, 'Failed to load project data'));
    } finally {
      setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    void loadProjectData();
  }, [loadProjectData]);

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

  if (!project) {
    return (
      <Layout>
        <Container maxWidth="lg">
          <Alert severity="error" sx={{ mt: 4 }}>
            {error || 'Project not found'}
          </Alert>
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
            onClick={() => navigate('/')}
            sx={{ mb: 2 }}
          >
            Back to Projects
          </Button>

          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
            <Box>
              <Typography variant="h4" component="h1" gutterBottom>
                {project.name}
              </Typography>
              {project.description && (
                <Typography color="text.secondary">
                  {project.description}
                </Typography>
              )}
            </Box>
          </Box>

          {error && (
            <Alert severity="error" sx={{ mb: 3 }} onClose={() => setError('')}>
              {error}
            </Alert>
          )}

          <Box sx={{ borderBottom: 1, borderColor: 'divider' }}>
            <Tabs value={tabValue} onChange={(_event, newValue: number) => setTabValue(newValue)}>
              <Tab icon={<CloudQueue />} label="Environments" iconPosition="start" />
              <Tab icon={<Assignment />} label="Test Suites" iconPosition="start" />
            </Tabs>
          </Box>

          <TabPanel value={tabValue} index={0}>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 2 }}>
              <Typography variant="h6">Environments</Typography>
              <Button
                variant="contained"
                startIcon={<Add />}
                onClick={() => navigate(`/projects/${projectId}/environments/new`)}
              >
                Add Environment
              </Button>
            </Box>

            {environments.length === 0 ? (
              <Card>
                <CardContent sx={{ textAlign: 'center', py: 4 }}>
                  <CloudQueue sx={{ fontSize: 48, color: 'text.secondary', mb: 2 }} />
                  <Typography color="text.secondary" gutterBottom>
                    No environments configured
                  </Typography>
                  <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                    Environments define the base URLs and authentication for your services
                  </Typography>
                  <Button
                    variant="contained"
                    startIcon={<Add />}
                    onClick={() => navigate(`/projects/${projectId}/environments/new`)}
                  >
                    Create First Environment
                  </Button>
                </CardContent>
              </Card>
            ) : (
              <List>
                {environments.map((env) => (
                  <Card key={env.id} sx={{ mb: 2 }}>
                    <ListItemButton onClick={() => navigate(`/environments/${env.id}`)}>
                      <ListItemText
                        primary={env.name}
                        secondary={env.baseUrl}
                      />
                    </ListItemButton>
                  </Card>
                ))}
              </List>
            )}
          </TabPanel>

          <TabPanel value={tabValue} index={1}>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 2 }}>
              <Typography variant="h6">Test Suites</Typography>
              <Button
                variant="contained"
                startIcon={<Add />}
                onClick={() => navigate(`/projects/${projectId}/suites/new`)}
              >
                Create Suite
              </Button>
            </Box>

            {suites.length === 0 ? (
              <Card>
                <CardContent sx={{ textAlign: 'center', py: 4 }}>
                  <Assignment sx={{ fontSize: 48, color: 'text.secondary', mb: 2 }} />
                  <Typography color="text.secondary" gutterBottom>
                    No test suites created
                  </Typography>
                  <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                    Test suites organize your test cases
                  </Typography>
                  <Button
                    variant="contained"
                    startIcon={<Add />}
                    onClick={() => navigate(`/projects/${projectId}/suites/new`)}
                  >
                    Create First Suite
                  </Button>
                </CardContent>
              </Card>
            ) : (
              <List>
                {suites.map((suite) => (
                  <Card key={suite.id} sx={{ mb: 2 }}>
                    <ListItemButton onClick={() => navigate(`/suites/${suite.id}`)}>
                      <ListItemText
                        primary={suite.name}
                        secondary={suite.description || 'No description'}
                      />
                      <Chip
                        label={`${suite.testCaseCount} tests`}
                        size="small"
                        color="primary"
                        variant="outlined"
                      />
                    </ListItemButton>
                  </Card>
                ))}
              </List>
            )}
          </TabPanel>
        </Box>
      </Container>
    </Layout>
  );
};
