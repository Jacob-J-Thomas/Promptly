import React, { useState } from 'react';
import {
  Container,
  Typography,
  Box,
  Button,
  TextField,
  Card,
  CardContent,
  Alert,
} from '@mui/material';
import { ArrowBack } from '@mui/icons-material';
import { useNavigate, useParams } from 'react-router-dom';
import { suitesApi } from '../api/suites';
import { Layout } from '../components/Layout';

export const SuiteForm: React.FC = () => {
  const navigate = useNavigate();
  const { projectId } = useParams<{ projectId: string }>();

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    if (!name.trim()) {
      setError('Suite name is required');
      return;
    }

    try {
      setLoading(true);
      setError('');

      const suite = await suitesApi.create(projectId!, {
        name: name.trim(),
        description: description.trim() || undefined,
      });

      navigate(`/suites/${suite.id}`);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to create test suite');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Layout>
      <Container maxWidth="md">
        <Box sx={{ mt: 4, mb: 4 }}>
          <Button
            startIcon={<ArrowBack />}
            onClick={() => navigate(`/projects/${projectId}`)}
            sx={{ mb: 2 }}
          >
            Back to Project
          </Button>

          <Typography variant="h4" component="h1" gutterBottom>
            Create New Test Suite
          </Typography>

          <Typography variant="body1" color="text.secondary" sx={{ mb: 3 }}>
            Test suites organize your test cases. After creating a suite, you can add individual tests or import them from YAML files.
          </Typography>

          {error && (
            <Alert severity="error" sx={{ mb: 3 }} onClose={() => setError('')}>
              {error}
            </Alert>
          )}

          <Card>
            <CardContent>
              <Box component="form" onSubmit={handleSubmit}>
                <TextField
                  fullWidth
                  label="Suite Name"
                  placeholder="e.g., Chat Functionality Tests, RAG Pipeline Tests"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  required
                  disabled={loading}
                  sx={{ mb: 3 }}
                  helperText="A descriptive name for this test suite"
                />

                <TextField
                  fullWidth
                  label="Description"
                  placeholder="Describe what this test suite covers..."
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  disabled={loading}
                  multiline
                  rows={4}
                  sx={{ mb: 3 }}
                  helperText="Optional description of the test suite's purpose and scope"
                />

                <Box sx={{ display: 'flex', gap: 2, justifyContent: 'flex-end', mt: 4 }}>
                  <Button
                    variant="outlined"
                    onClick={() => navigate(`/projects/${projectId}`)}
                    disabled={loading}
                  >
                    Cancel
                  </Button>
                  <Button
                    type="submit"
                    variant="contained"
                    disabled={loading}
                  >
                    {loading ? 'Creating...' : 'Create Test Suite'}
                  </Button>
                </Box>
              </Box>
            </CardContent>
          </Card>
        </Box>
      </Container>
    </Layout>
  );
};
