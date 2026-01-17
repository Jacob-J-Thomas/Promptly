import React, { useState, useEffect } from 'react';
import {
  Container,
  Typography,
  Box,
  Button,
  Card,
  CardContent,
  CardActions,
  Grid,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  TextField,
  CircularProgress,
  Alert,
  IconButton,
} from '@mui/material';
import { Add, FolderOpen, Delete, Edit } from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import { projectsApi, type Project } from '../api/projects';
import { Layout } from '../components/Layout';

export const ProjectsList: React.FC = () => {
  const navigate = useNavigate();
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [createDialogOpen, setCreateDialogOpen] = useState(false);
  const [newProjectName, setNewProjectName] = useState('');
  const [newProjectDescription, setNewProjectDescription] = useState('');
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    loadProjects();
  }, []);

  const loadProjects = async () => {
    try {
      setLoading(true);
      const data = await projectsApi.getAll();
      setProjects(data);
      setError('');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load projects');
    } finally {
      setLoading(false);
    }
  };

  const handleCreateProject = async () => {
    if (!newProjectName.trim()) {
      setError('Project name is required');
      return;
    }

    try {
      setSubmitting(true);
      setError('');
      await projectsApi.create({
        name: newProjectName,
        description: newProjectDescription || undefined,
      });
      setCreateDialogOpen(false);
      setNewProjectName('');
      setNewProjectDescription('');
      await loadProjects();
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to create project');
    } finally {
      setSubmitting(false);
    }
  };

  const handleDeleteProject = async (id: string) => {
    if (!window.confirm('Are you sure you want to delete this project? This will delete all associated data.')) {
      return;
    }

    try {
      await projectsApi.delete(id);
      await loadProjects();
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to delete project');
    }
  };

  return (
    <Layout>
      <Container maxWidth="lg">
        <Box sx={{ mt: 4, mb: 4 }}>
          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
            <Typography variant="h4" component="h1">
              Projects
            </Typography>
            <Button
              variant="contained"
              startIcon={<Add />}
              onClick={() => setCreateDialogOpen(true)}
            >
              Create Project
            </Button>
          </Box>

          {error && (
            <Alert severity="error" sx={{ mb: 3 }} onClose={() => setError('')}>
              {error}
            </Alert>
          )}

          {loading ? (
            <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
              <CircularProgress />
            </Box>
          ) : projects.length === 0 ? (
            <Box sx={{ textAlign: 'center', py: 8 }}>
              <FolderOpen sx={{ fontSize: 64, color: 'text.secondary', mb: 2 }} />
              <Typography variant="h6" color="text.secondary" gutterBottom>
                No projects yet
              </Typography>
              <Typography color="text.secondary" sx={{ mb: 3 }}>
                Create your first project to get started with testing your LLM applications
              </Typography>
              <Button
                variant="contained"
                startIcon={<Add />}
                onClick={() => setCreateDialogOpen(true)}
              >
                Create Your First Project
              </Button>
            </Box>
          ) : (
            <Grid container spacing={3}>
              {projects.map((project) => (
                <Grid item xs={12} md={6} lg={4} key={project.id}>
                  <Card
                    sx={{
                      height: '100%',
                      display: 'flex',
                      flexDirection: 'column',
                      '&:hover': {
                        boxShadow: 4,
                      },
                    }}
                  >
                    <CardContent sx={{ flexGrow: 1, cursor: 'pointer' }} onClick={() => navigate(`/projects/${project.id}`)}>
                      <Typography variant="h6" component="h2" gutterBottom>
                        {project.name}
                      </Typography>
                      {project.description && (
                        <Typography variant="body2" color="text.secondary">
                          {project.description}
                        </Typography>
                      )}
                      <Typography variant="caption" color="text.secondary" sx={{ mt: 2, display: 'block' }}>
                        Created {new Date(project.createdAt).toLocaleDateString()}
                      </Typography>
                    </CardContent>
                    <CardActions>
                      <Button
                        size="small"
                        startIcon={<FolderOpen />}
                        onClick={() => navigate(`/projects/${project.id}`)}
                      >
                        Open
                      </Button>
                      <IconButton
                        size="small"
                        color="error"
                        onClick={(e) => {
                          e.stopPropagation();
                          handleDeleteProject(project.id);
                        }}
                      >
                        <Delete />
                      </IconButton>
                    </CardActions>
                  </Card>
                </Grid>
              ))}
            </Grid>
          )}
        </Box>

        {/* Create Project Dialog */}
        <Dialog open={createDialogOpen} onClose={() => !submitting && setCreateDialogOpen(false)} maxWidth="sm" fullWidth>
          <DialogTitle>Create New Project</DialogTitle>
          <DialogContent>
            <TextField
              autoFocus
              margin="dense"
              label="Project Name"
              fullWidth
              required
              value={newProjectName}
              onChange={(e) => setNewProjectName(e.target.value)}
              disabled={submitting}
              sx={{ mb: 2 }}
            />
            <TextField
              margin="dense"
              label="Description"
              fullWidth
              multiline
              rows={3}
              value={newProjectDescription}
              onChange={(e) => setNewProjectDescription(e.target.value)}
              disabled={submitting}
            />
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setCreateDialogOpen(false)} disabled={submitting}>
              Cancel
            </Button>
            <Button onClick={handleCreateProject} variant="contained" disabled={submitting}>
              {submitting ? 'Creating...' : 'Create'}
            </Button>
          </DialogActions>
        </Dialog>
      </Container>
    </Layout>
  );
};
