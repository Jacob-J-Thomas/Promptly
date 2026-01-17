import { apiClient } from './client';

interface TestCase {
  id: string;
  suiteId: string;
  externalId: string;
  name: string;
  description?: string;
  inputSpecJson: string;
  expectationsJson: string;
  createdAt: string;
}

interface CreateTestCaseRequest {
  externalId: string;
  name: string;
  description?: string;
  inputSpecJson: string;
  expectationsJson: string;
}

interface UpdateTestCaseRequest {
  externalId: string;
  name: string;
  description?: string;
  inputSpecJson: string;
  expectationsJson: string;
}

export const testsApi = {
  getBySuite: async (suiteId: string): Promise<TestCase[]> => {
    const response = await apiClient.get(`/suites/${suiteId}/tests`);
    return response.data;
  },

  getById: async (id: string): Promise<TestCase> => {
    const response = await apiClient.get(`/tests/${id}`);
    return response.data;
  },

  create: async (suiteId: string, data: CreateTestCaseRequest): Promise<TestCase> => {
    const response = await apiClient.post(`/suites/${suiteId}/tests`, data);
    return response.data;
  },

  update: async (id: string, data: UpdateTestCaseRequest): Promise<TestCase> => {
    const response = await apiClient.put(`/tests/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/tests/${id}`);
  },

  importYaml: async (suiteId: string, file: File): Promise<void> => {
    const formData = new FormData();
    formData.append('file', file);
    await apiClient.post(`/suites/${suiteId}/tests/import`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    });
  },

  exportYaml: async (suiteId: string): Promise<string> => {
    const response = await apiClient.get(`/suites/${suiteId}/tests/export`, {
      responseType: 'text',
    });
    return response.data;
  },
};

export type { TestCase, CreateTestCaseRequest, UpdateTestCaseRequest };
