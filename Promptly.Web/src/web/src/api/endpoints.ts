import { apiClient } from './client';

interface Endpoint {
  id: string;
  environmentId: string;
  name: string;
  path: string;
  method: string;
  timeoutSeconds: number;
  createdAt: string;
  updatedAt: string;
}

interface CreateEndpointRequest {
  name: string;
  path: string;
  method: string;
  timeoutSeconds: number;
}

const endpointsApi = {
  getByEnvironment: async (environmentId: string): Promise<Endpoint[]> => {
    const response = await apiClient.get(`/environments/${environmentId}/endpoints`);
    return response.data;
  },

  getById: async (id: string): Promise<Endpoint> => {
    const response = await apiClient.get(`/endpoints/${id}`);
    return response.data;
  },

  create: async (environmentId: string, data: CreateEndpointRequest): Promise<Endpoint> => {
    const response = await apiClient.post(`/environments/${environmentId}/endpoints`, data);
    return response.data;
  },

  update: async (id: string, data: CreateEndpointRequest): Promise<Endpoint> => {
    const response = await apiClient.put(`/endpoints/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/endpoints/${id}`);
  },
};

export { endpointsApi };
export type { Endpoint, CreateEndpointRequest };
