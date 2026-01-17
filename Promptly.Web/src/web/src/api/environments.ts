import { apiClient } from './client';

interface Environment {
  id: string;
  projectId: string;
  name: string;
  baseUrl: string;
  headersEncrypted?: string;
  createdAt: string;
  updatedAt: string;
}

interface CreateEnvironmentRequest {
  name: string;
  baseUrl: string;
  headers?: Record<string, string>;
}

const environmentsApi = {
  getByProject: async (projectId: string): Promise<Environment[]> => {
    const response = await apiClient.get(`/projects/${projectId}/environments`);
    return response.data;
  },

  getById: async (id: string): Promise<Environment> => {
    const response = await apiClient.get(`/environments/${id}`);
    return response.data;
  },

  create: async (projectId: string, data: CreateEnvironmentRequest): Promise<Environment> => {
    const response = await apiClient.post(`/projects/${projectId}/environments`, data);
    return response.data;
  },

  update: async (id: string, data: CreateEnvironmentRequest): Promise<Environment> => {
    const response = await apiClient.put(`/environments/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/environments/${id}`);
  },

  getDecryptedHeaders: async (id: string): Promise<Record<string, string>> => {
    const response = await apiClient.get(`/environments/${id}/headers`);
    return response.data;
  },
};

export { environmentsApi };
export type { Environment, CreateEnvironmentRequest };
