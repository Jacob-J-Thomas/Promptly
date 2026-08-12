import { apiClient } from './client';

const runStatusNames = ['Queued', 'Running', 'Completed', 'Failed'] as const;
const resultStatusNames = ['Pass', 'Fail', 'Error'] as const;

type RunStatus = typeof runStatusNames[number] | string;
type ResultStatus = typeof resultStatusNames[number] | string;

interface TestRunDto {
  id: string;
  suiteId: string;
  environmentId: string;
  endpointId: string;
  mappingSpecId: string;
  status: number | string;
  summaryJson?: string;
  gitCommitHash?: string;
  configSnapshotJson?: string;
  createdByUserId?: string;
  errorMessage?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
}

interface TestRun extends Omit<TestRunDto, 'status'> {
  status: RunStatus;
}

interface QueueRunRequest {
  suiteId: string;
  environmentId: string;
  endpointId: string;
  mappingSpecId: string;
  gitCommitHash?: string;
  configSnapshotJson?: string;
}

interface TestRunResultDto {
  id: string;
  runId: string;
  testCaseId: string;
  status: number | string;
  traceJson?: string;
  metricsJson?: string;
  failureReasonsJson?: string;
  createdAt: string;
  testCaseName?: string;
  testCaseExternalId?: string;
}

interface TestRunResult extends Omit<TestRunResultDto, 'status'> {
  status: ResultStatus;
  passedExpectations: number;
  failedExpectations: number;
  errorExpectations: number;
  failureReasons: string[];
}

const normalizeEnum = <T extends string>(value: number | string, names: readonly T[]): T | string => {
  if (typeof value === 'number') {
    return names[value] ?? `Unknown (${value})`;
  }

  return value;
};

const parseMetrics = (value?: string): { passed: number; failed: number; errors: number } => {
  if (!value) {
    return { passed: 0, failed: 0, errors: 0 };
  }

  try {
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      return { passed: 0, failed: 0, errors: 0 };
    }
    const metrics = parsed as Record<string, unknown>;
    return {
      passed: typeof metrics.passed === 'number' ? metrics.passed : 0,
      failed: typeof metrics.failed === 'number' ? metrics.failed : 0,
      errors: typeof metrics.errors === 'number' ? metrics.errors : 0,
    };
  } catch {
    return { passed: 0, failed: 0, errors: 0 };
  }
};

const parseFailureReasons = (value?: string): string[] => {
  if (!value) {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed) && parsed.every((reason) => typeof reason === 'string')
      ? parsed
      : [];
  } catch {
    return [];
  }
};

const normalizeRun = (run: TestRunDto): TestRun => ({
  ...run,
  status: normalizeEnum(run.status, runStatusNames),
});

const normalizeResult = (result: TestRunResultDto): TestRunResult => {
  const metrics = parseMetrics(result.metricsJson);
  return {
    ...result,
    status: normalizeEnum(result.status, resultStatusNames),
    passedExpectations: metrics.passed,
    failedExpectations: metrics.failed,
    errorExpectations: metrics.errors,
    failureReasons: parseFailureReasons(result.failureReasonsJson),
  };
};

const runsApi = {
  queue: async (data: QueueRunRequest): Promise<TestRun> => {
    const response = await apiClient.post('/runs', data);
    return normalizeRun(response.data as TestRunDto);
  },

  getById: async (id: string): Promise<TestRun> => {
    const response = await apiClient.get(`/runs/${id}`);
    return normalizeRun(response.data as TestRunDto);
  },

  getBySuite: async (suiteId: string, status?: string, limit?: number): Promise<TestRun[]> => {
    const params = new URLSearchParams();
    if (status) params.append('status', status);
    if (limit) params.append('limit', limit.toString());

    const response = await apiClient.get(`/suites/${suiteId}/runs?${params.toString()}`);
    return (response.data as TestRunDto[]).map(normalizeRun);
  },

  getResults: async (runId: string): Promise<TestRunResult[]> => {
    const response = await apiClient.get(`/runs/${runId}/results`);
    return (response.data as TestRunResultDto[]).map(normalizeResult);
  },

  getResult: async (runId: string, resultId: string): Promise<TestRunResult> => {
    const response = await apiClient.get(`/runs/${runId}/results/${resultId}`);
    return normalizeResult(response.data as TestRunResultDto);
  },
};

export { runsApi };
export type { TestRun, QueueRunRequest, TestRunResult, TestRunDto, TestRunResultDto };
