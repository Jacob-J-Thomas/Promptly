import { apiClient } from './client';

interface TestRun {
  id: string;
  suiteId: string;
  environmentId: string;
  endpointId: string;
  mappingSpecId: string;
  status: string;
  summaryJson?: string;
  gitCommitHash?: string;
  configSnapshotJson?: string;
  triggeredBy?: string;
  errorMessage?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
}

interface QueueRunRequest {
  suiteId: string;
  environmentId: string;
  endpointId: string;
  mappingSpecId: string;
  gitCommitHash?: string;
  configSnapshotJson?: string;
}

interface TestRunResult {
  id: string;
  runId: string;
  testCaseId: string;
  status: string;
  traceJson?: string;
  expectationResultsJson?: string;
  passedExpectations: number;
  failedExpectations: number;
  failureReason?: string;
  errorMessage?: string;
  createdAt: string;
  testCaseName?: string;
  testCaseExternalId?: string;
}

const runsApi = {
  queue: async (data: QueueRunRequest): Promise<TestRun> => {
    const response = await apiClient.post('/runs', data);
    return response.data;
  },

  getById: async (id: string): Promise<TestRun> => {
    const response = await apiClient.get(`/runs/${id}`);
    return response.data;
  },

  getBySuite: async (suiteId: string, status?: string, limit?: number): Promise<TestRun[]> => {
    const params = new URLSearchParams();
    if (status) params.append('status', status);
    if (limit) params.append('limit', limit.toString());

    const response = await apiClient.get(`/suites/${suiteId}/runs?${params.toString()}`);
    return response.data;
  },

  getResults: async (runId: string): Promise<TestRunResult[]> => {
    const response = await apiClient.get(`/runs/${runId}/results`);
    return response.data;
  },

  getResult: async (runId: string, resultId: string): Promise<TestRunResult> => {
    const response = await apiClient.get(`/runs/${runId}/results/${resultId}`);
    return response.data;
  },
};

export { runsApi };
export type { TestRun, QueueRunRequest, TestRunResult };
