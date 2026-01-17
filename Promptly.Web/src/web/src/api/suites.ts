import { apiClient } from './client';

interface TestSuite {
  id: string;
  projectId: string;
  name: string;
  description?: string;
  createdAt: string;
  updatedAt: string;
  testCaseCount: number;
}

interface CreateTestSuiteRequest {
  name: string;
  description?: string;
}

interface TestCase {
  id: string;
  suiteId: string;
  externalId: string;
  name: string;
  description?: string;
  inputSpecJson: string;
  expectationsJson: string;
  createdAt: string;
  updatedAt: string;
}

interface CreateTestCaseRequest {
  externalId: string;
  name: string;
  description?: string;
  inputSpecJson: string;
  expectationsJson: string;
}

const suitesApi = {
  create: async (projectId: string, data: CreateTestSuiteRequest): Promise<TestSuite> => {
    const response = await apiClient.post(`/suites?projectId=${projectId}`, data);
    return response.data;
  },

  getByProject: async (projectId: string): Promise<TestSuite[]> => {
    const response = await apiClient.get(`/projects/${projectId}/suites`);
    return response.data;
  },

  getById: async (id: string): Promise<TestSuite> => {
    const response = await apiClient.get(`/suites/${id}`);
    return response.data;
  },

  update: async (id: string, data: CreateTestSuiteRequest): Promise<TestSuite> => {
    const response = await apiClient.put(`/suites/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/suites/${id}`);
  },

  importTests: async (suiteId: string, file: File): Promise<any> => {
    const formData = new FormData();
    formData.append('file', file);

    const response = await apiClient.post(`/suites/${suiteId}/tests/import`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    });
    return response.data;
  },

  exportTests: async (suiteId: string): Promise<Blob> => {
    const response = await apiClient.get(`/suites/${suiteId}/tests/export`, {
      responseType: 'blob',
    });
    return response.data;
  },
};

const testsApi = {
  create: async (suiteId: string, data: CreateTestCaseRequest): Promise<TestCase> => {
    const response = await apiClient.post(`/suites/${suiteId}/tests`, data);
    return response.data;
  },

  getBySuite: async (suiteId: string): Promise<TestCase[]> => {
    const response = await apiClient.get(`/suites/${suiteId}/tests`);
    return response.data;
  },

  getById: async (id: string): Promise<TestCase> => {
    const response = await apiClient.get(`/tests/${id}`);
    return response.data;
  },

  update: async (id: string, data: CreateTestCaseRequest): Promise<TestCase> => {
    const response = await apiClient.put(`/tests/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/tests/${id}`);
  },
};

export { suitesApi, testsApi };
export type { TestSuite, CreateTestSuiteRequest, TestCase, CreateTestCaseRequest };
