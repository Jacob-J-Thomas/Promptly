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
  IconButton,
  List,
  ListItem,
  ListItemText,
  ListItemSecondaryAction,
} from '@mui/material';
import { ArrowBack, Delete, Add } from '@mui/icons-material';
import { useNavigate, useParams } from 'react-router-dom';
import { environmentsApi } from '../api/environments';
import { getApiErrorMessage } from '../api/errors';
import { Layout } from '../components/Layout';

export const EnvironmentForm: React.FC = () => {
  const navigate = useNavigate();
  const { projectId } = useParams<{ projectId: string }>();

  const [name, setName] = useState('');
  const [baseUrl, setBaseUrl] = useState('');
  const [headers, setHeaders] = useState<Record<string, string>>({});
  const [headerKey, setHeaderKey] = useState('');
  const [headerValue, setHeaderValue] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleAddHeader = () => {
    if (headerKey.trim() && headerValue.trim()) {
      setHeaders({ ...headers, [headerKey.trim()]: headerValue.trim() });
      setHeaderKey('');
      setHeaderValue('');
    }
  };

  const handleRemoveHeader = (key: string) => {
    const newHeaders = { ...headers };
    delete newHeaders[key];
    setHeaders(newHeaders);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    if (!name.trim() || !baseUrl.trim()) {
      setError('Name and Base URL are required');
      return;
    }

    try {
      setLoading(true);
      setError('');

      await environmentsApi.create(projectId!, {
        name: name.trim(),
        baseUrl: baseUrl.trim(),
        headers: Object.keys(headers).length > 0 ? headers : undefined,
      });

      navigate(`/projects/${projectId}`);
    } catch (error: unknown) {
      setError(getApiErrorMessage(error, 'Failed to create environment'));
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
            Create New Environment
          </Typography>

          <Typography variant="body1" color="text.secondary" sx={{ mb: 3 }}>
            Environments define the base URL and authentication headers for your API endpoints.
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
                  label="Environment Name"
                  placeholder="e.g., Development, Staging, Production"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  required
                  disabled={loading}
                  sx={{ mb: 3 }}
                  helperText="A descriptive name for this environment"
                />

                <TextField
                  fullWidth
                  label="Base URL"
                  placeholder="https://api.example.com"
                  value={baseUrl}
                  onChange={(e) => setBaseUrl(e.target.value)}
                  required
                  disabled={loading}
                  sx={{ mb: 3 }}
                  helperText="The base URL for API requests in this environment"
                />

                <Typography variant="h6" gutterBottom sx={{ mt: 3 }}>
                  HTTP Headers (Optional)
                </Typography>
                <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                  Add authentication headers or other custom headers. These will be encrypted at rest.
                </Typography>

                <Box sx={{ display: 'flex', gap: 2, mb: 2 }}>
                  <TextField
                    label="Header Name"
                    placeholder="Authorization"
                    value={headerKey}
                    onChange={(e) => setHeaderKey(e.target.value)}
                    disabled={loading}
                    sx={{ flex: 1 }}
                  />
                  <TextField
                    label="Header Value"
                    placeholder="Bearer token..."
                    value={headerValue}
                    onChange={(e) => setHeaderValue(e.target.value)}
                    disabled={loading}
                    sx={{ flex: 1 }}
                  />
                  <Button
                    variant="outlined"
                    startIcon={<Add />}
                    onClick={handleAddHeader}
                    disabled={loading || !headerKey.trim() || !headerValue.trim()}
                  >
                    Add
                  </Button>
                </Box>

                {Object.keys(headers).length > 0 && (
                  <List sx={{ mb: 2, bgcolor: 'background.default', borderRadius: 1 }}>
                    {Object.entries(headers).map(([key, value]) => (
                      <ListItem key={key}>
                        <ListItemText
                          primary={key}
                          secondary={value.length > 50 ? `${value.substring(0, 50)}...` : value}
                        />
                        <ListItemSecondaryAction>
                          <IconButton
                            edge="end"
                            onClick={() => handleRemoveHeader(key)}
                            disabled={loading}
                          >
                            <Delete />
                          </IconButton>
                        </ListItemSecondaryAction>
                      </ListItem>
                    ))}
                  </List>
                )}

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
                    {loading ? 'Creating...' : 'Create Environment'}
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
