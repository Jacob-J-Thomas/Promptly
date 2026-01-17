import { apiClient } from './client';

interface Project {
  id: string;
  name: string;
  description?: string;
  createdAt: string;
  updatedAt: string;
}

interface CreateProjectRequest {
  name: string;
  description?: string;
}

const projectsApi = {
  getAll: async (): Promise<Project[]> => {
    const response = await apiClient.get('/projects');
    return response.data;
  },

  getById: async (id: string): Promise<Project> => {
    const response = await apiClient.get(`/projects/${id}`);
    return response.data;
  },

  create: async (data: CreateProjectRequest): Promise<Project> => {
    const response = await apiClient.post('/projects', data);
    return response.data;
  },

  update: async (id: string, data: CreateProjectRequest): Promise<Project> => {
    const response = await apiClient.put(`/projects/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/projects/${id}`);
  },
};

export { projectsApi };
export type { Project, CreateProjectRequest };
