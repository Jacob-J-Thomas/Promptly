import { apiClient } from './client';

interface MappingSpec {
  id: string;
  endpointId: string;
  name: string;
  specJson: string;
  isDefault: boolean;
  createdAt: string;
  updatedAt: string;
}

interface ProposeMappingRequest {
  sampleResponseJson: string;
  sampleRequestJson?: string;
  hints?: string;
}

interface ProposeMappingResponse {
  mappingSpecJson: string;
  reason?: string;
}

interface ValidateMappingRequest {
  mappingSpecJson: string;
  sampleResponseJson: string;
}

interface ValidateMappingResponse {
  success: boolean;
  previewTrace?: any;
  errorMessage?: string;
}

interface CreateMappingSpecRequest {
  name: string;
  specJson: string;
}

interface UpdateMappingSpecRequest {
  name: string;
  specJson: string;
}

const mappingApi = {
  propose: async (endpointId: string, data: ProposeMappingRequest): Promise<ProposeMappingResponse> => {
    const response = await apiClient.post(`/endpoints/${endpointId}/mapping/propose`, data);
    return response.data;
  },

  validate: async (endpointId: string, data: ValidateMappingRequest): Promise<ValidateMappingResponse> => {
    const response = await apiClient.post(`/endpoints/${endpointId}/mapping/validate`, data);
    return response.data;
  },

  create: async (endpointId: string, data: CreateMappingSpecRequest): Promise<MappingSpec> => {
    const response = await apiClient.post(`/endpoints/${endpointId}/mapping`, data);
    return response.data;
  },

  getByEndpoint: async (endpointId: string): Promise<MappingSpec[]> => {
    const response = await apiClient.get(`/endpoints/${endpointId}/mapping`);
    return response.data;
  },

  getById: async (id: string): Promise<MappingSpec> => {
    const response = await apiClient.get(`/mapping/${id}`);
    return response.data;
  },

  update: async (id: string, data: UpdateMappingSpecRequest): Promise<MappingSpec> => {
    const response = await apiClient.put(`/mapping/${id}`, data);
    return response.data;
  },

  setDefault: async (id: string): Promise<void> => {
    await apiClient.post(`/mapping/${id}/set-default`);
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/mapping/${id}`);
  },
};

export { mappingApi };
export type { MappingSpec, ProposeMappingRequest, ProposeMappingResponse, ValidateMappingRequest, ValidateMappingResponse, CreateMappingSpecRequest, UpdateMappingSpecRequest };
