import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClientMock = vi.hoisted(() => ({
  delete: vi.fn(),
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
}));

vi.mock('./client', () => ({
  apiClient: apiClientMock,
}));

import { authApi } from './auth';
import { endpointsApi } from './endpoints';
import { environmentsApi } from './environments';
import { mappingApi } from './mapping';
import { projectsApi } from './projects';
import { runsApi } from './runs';
import { suitesApi, testsApi as suiteTestsApi } from './suites';
import { testsApi } from './tests';

const failure = new Error('request failed');

beforeEach(() => {
  vi.resetAllMocks();
});

describe('authApi', () => {
  it('posts registration and login payloads and returns response data', async () => {
    const registerRequest = {
      name: 'Ada Lovelace',
      email: 'ada@example.test',
      password: 'correct horse battery staple',
    };
    const loginRequest = {
      email: registerRequest.email,
      password: registerRequest.password,
    };
    const registered = {
      token: 'register-token',
      user: { id: 'user-1', name: registerRequest.name, email: registerRequest.email },
    };
    const loggedIn = { ...registered, token: 'login-token' };
    apiClientMock.post
      .mockResolvedValueOnce({ data: registered })
      .mockResolvedValueOnce({ data: loggedIn });

    await expect(authApi.register(registerRequest)).resolves.toEqual(registered);
    await expect(authApi.login(loginRequest)).resolves.toEqual(loggedIn);

    expect(apiClientMock.post).toHaveBeenNthCalledWith(1, '/auth/register', registerRequest);
    expect(apiClientMock.post).toHaveBeenNthCalledWith(2, '/auth/login', loginRequest);
  });

  it('propagates authentication failures', async () => {
    apiClientMock.post.mockRejectedValueOnce(failure);

    await expect(authApi.login({ email: 'ada@example.test', password: 'wrong' })).rejects.toBe(failure);
  });
});

describe('endpointsApi', () => {
  const endpoint = {
    id: 'endpoint-1',
    environmentId: 'environment-1',
    name: 'Chat completions',
    path: '/v1/chat/completions',
    httpMethod: 'POST',
    timeoutSeconds: 30,
  };
  const request = {
    name: endpoint.name,
    path: endpoint.path,
    httpMethod: endpoint.httpMethod,
    timeoutSeconds: endpoint.timeoutSeconds,
  };

  it('serializes every endpoint route and returns response data', async () => {
    apiClientMock.get
      .mockResolvedValueOnce({ data: [endpoint] })
      .mockResolvedValueOnce({ data: endpoint });
    apiClientMock.post.mockResolvedValueOnce({ data: endpoint });
    apiClientMock.put.mockResolvedValueOnce({ data: endpoint });
    apiClientMock.delete.mockResolvedValueOnce({});

    await expect(endpointsApi.getByEnvironment('environment-1')).resolves.toEqual([endpoint]);
    await expect(endpointsApi.getById('endpoint-1')).resolves.toEqual(endpoint);
    await expect(endpointsApi.create('environment-1', request)).resolves.toEqual(endpoint);
    await expect(endpointsApi.update('endpoint-1', request)).resolves.toEqual(endpoint);
    await expect(endpointsApi.delete('endpoint-1')).resolves.toBeUndefined();

    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/environments/environment-1/endpoints');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/endpoints/endpoint-1');
    expect(apiClientMock.post).toHaveBeenCalledWith('/environments/environment-1/endpoints', request);
    expect(apiClientMock.put).toHaveBeenCalledWith('/endpoints/endpoint-1', request);
    expect(apiClientMock.delete).toHaveBeenCalledWith('/endpoints/endpoint-1');
  });

  it('propagates endpoint failures', async () => {
    apiClientMock.get.mockRejectedValueOnce(failure);

    await expect(endpointsApi.getById('missing')).rejects.toBe(failure);
  });
});

describe('environmentsApi', () => {
  const environment = {
    id: 'environment-1',
    projectId: 'project-1',
    name: 'Staging',
    baseUrl: 'https://staging.example.test',
    hasHeaders: true,
    headers: { Authorization: 'Bearer secret' },
    createdAt: '2026-08-12T00:00:00Z',
  };
  const request = {
    name: environment.name,
    baseUrl: environment.baseUrl,
    headers: { Authorization: 'Bearer secret' },
  };

  it('serializes every environment route and returns response data', async () => {
    apiClientMock.get
      .mockResolvedValueOnce({ data: [environment] })
      .mockResolvedValueOnce({ data: environment });
    apiClientMock.post.mockResolvedValueOnce({ data: environment });
    apiClientMock.put.mockResolvedValueOnce({ data: environment });
    apiClientMock.delete.mockResolvedValueOnce({});

    await expect(environmentsApi.getByProject('project-1')).resolves.toEqual([environment]);
    await expect(environmentsApi.getById('environment-1')).resolves.toEqual(environment);
    await expect(environmentsApi.create('project-1', request)).resolves.toEqual(environment);
    await expect(environmentsApi.update('environment-1', request)).resolves.toEqual(environment);
    await expect(environmentsApi.delete('environment-1')).resolves.toBeUndefined();

    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/projects/project-1/environments');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/environments/environment-1');
    expect(apiClientMock.post).toHaveBeenCalledWith('/projects/project-1/environments', request);
    expect(apiClientMock.put).toHaveBeenCalledWith('/environments/environment-1', request);
    expect(apiClientMock.delete).toHaveBeenCalledWith('/environments/environment-1');
  });

  it('propagates environment failures', async () => {
    apiClientMock.delete.mockRejectedValueOnce(failure);

    await expect(environmentsApi.delete('environment-1')).rejects.toBe(failure);
  });
});

describe('mappingApi', () => {
  const mapping = {
    id: 'mapping-1',
    endpointId: 'endpoint-1',
    name: 'Default mapping',
    specJson: '{"messages":"$.choices"}',
    isDefault: true,
    createdAt: '2026-08-12T00:00:00Z',
    updatedAt: '2026-08-12T00:00:00Z',
  };

  it('serializes mapping operations and returns response data', async () => {
    const proposalRequest = {
      sampleResponseJson: '{"choices":[]}',
      sampleRequestJson: '{"messages":[]}',
      hints: { selection: 'Use the first choice' },
    };
    const proposal = { mappingSpecJson: mapping.specJson, reason: 'Matched choices' };
    const validationRequest = {
      mappingSpecJson: mapping.specJson,
      sampleResponseJson: proposalRequest.sampleResponseJson,
    };
    const validation = {
      success: true,
      previewTrace: {
        messages: [{ role: 'assistant', content: 'Hello' }],
        toolCalls: [],
        retrievedDocs: [],
      },
    };
    const createRequest = { name: mapping.name, specJson: mapping.specJson };
    const updateRequest = { name: 'Updated mapping', specJson: mapping.specJson };
    apiClientMock.post
      .mockResolvedValueOnce({ data: proposal })
      .mockResolvedValueOnce({ data: validation })
      .mockResolvedValueOnce({ data: mapping })
      .mockResolvedValueOnce({});
    apiClientMock.get
      .mockResolvedValueOnce({ data: [mapping] })
      .mockResolvedValueOnce({ data: mapping });
    apiClientMock.put.mockResolvedValueOnce({ data: { ...mapping, ...updateRequest } });
    apiClientMock.delete.mockResolvedValueOnce({});

    await expect(mappingApi.propose('endpoint-1', proposalRequest)).resolves.toEqual(proposal);
    await expect(mappingApi.validate('endpoint-1', validationRequest)).resolves.toEqual(validation);
    await expect(mappingApi.create('endpoint-1', createRequest)).resolves.toEqual(mapping);
    await expect(mappingApi.getByEndpoint('endpoint-1')).resolves.toEqual([mapping]);
    await expect(mappingApi.getById('mapping-1')).resolves.toEqual(mapping);
    await expect(mappingApi.update('mapping-1', updateRequest)).resolves.toEqual({ ...mapping, ...updateRequest });
    await expect(mappingApi.setDefault('mapping-1')).resolves.toBeUndefined();
    await expect(mappingApi.delete('mapping-1')).resolves.toBeUndefined();

    expect(apiClientMock.post).toHaveBeenNthCalledWith(1, '/endpoints/endpoint-1/mapping/propose', proposalRequest);
    expect(apiClientMock.post).toHaveBeenNthCalledWith(2, '/endpoints/endpoint-1/mapping/validate', validationRequest);
    expect(apiClientMock.post).toHaveBeenNthCalledWith(3, '/endpoints/endpoint-1/mapping', createRequest);
    expect(apiClientMock.post).toHaveBeenNthCalledWith(4, '/mapping/mapping-1/set-default');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/endpoints/endpoint-1/mapping');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/mapping/mapping-1');
    expect(apiClientMock.put).toHaveBeenCalledWith('/mapping/mapping-1', updateRequest);
    expect(apiClientMock.delete).toHaveBeenCalledWith('/mapping/mapping-1');
  });

  it('propagates mapping failures', async () => {
    apiClientMock.post.mockRejectedValueOnce(failure);

    await expect(mappingApi.setDefault('mapping-1')).rejects.toBe(failure);
  });
});

describe('projectsApi', () => {
  const project = {
    id: 'project-1',
    name: 'Prompt quality',
    description: 'Regression project',
    createdAt: '2026-08-12T00:00:00Z',
    updatedAt: '2026-08-12T00:00:00Z',
  };
  const request = { name: project.name, description: project.description };

  it('serializes every project route and returns response data', async () => {
    apiClientMock.get
      .mockResolvedValueOnce({ data: [project] })
      .mockResolvedValueOnce({ data: project });
    apiClientMock.post.mockResolvedValueOnce({ data: project });
    apiClientMock.put.mockResolvedValueOnce({ data: project });
    apiClientMock.delete.mockResolvedValueOnce({});

    await expect(projectsApi.getAll()).resolves.toEqual([project]);
    await expect(projectsApi.getById('project-1')).resolves.toEqual(project);
    await expect(projectsApi.create(request)).resolves.toEqual(project);
    await expect(projectsApi.update('project-1', request)).resolves.toEqual(project);
    await expect(projectsApi.delete('project-1')).resolves.toBeUndefined();

    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/projects');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/projects/project-1');
    expect(apiClientMock.post).toHaveBeenCalledWith('/projects', request);
    expect(apiClientMock.put).toHaveBeenCalledWith('/projects/project-1', request);
    expect(apiClientMock.delete).toHaveBeenCalledWith('/projects/project-1');
  });

  it('propagates project failures', async () => {
    apiClientMock.get.mockRejectedValueOnce(failure);

    await expect(projectsApi.getAll()).rejects.toBe(failure);
  });
});

describe('runsApi', () => {
  const run = {
    id: 'run-1',
    suiteId: 'suite-1',
    environmentId: 'environment-1',
    endpointId: 'endpoint-1',
    mappingSpecId: 'mapping-1',
    status: 1,
    createdAt: '2026-08-12T00:00:00Z',
  };
  const result = {
    id: 'result-1',
    runId: run.id,
    testCaseId: 'test-1',
    status: 0,
    metricsJson: '{"passed":2,"failed":0,"errors":0}',
    failureReasonsJson: null,
    createdAt: '2026-08-12T00:00:01Z',
  };

  it('serializes queue, lookup, filtered history, and result routes', async () => {
    const queueRequest = {
      suiteId: run.suiteId,
      environmentId: run.environmentId,
      endpointId: run.endpointId,
      mappingSpecId: run.mappingSpecId,
      gitCommitHash: 'abc1234',
    };
    apiClientMock.post.mockResolvedValueOnce({ data: run });
    apiClientMock.get
      .mockResolvedValueOnce({ data: run })
      .mockResolvedValueOnce({ data: [run] })
      .mockResolvedValueOnce({ data: [result] })
      .mockResolvedValueOnce({ data: result });

    const normalizedRun = { ...run, status: 'Running' };
    const normalizedResult = {
      ...result,
      status: 'Pass',
      passedExpectations: 2,
      failedExpectations: 0,
      errorExpectations: 0,
      failureReasons: [],
    };
    await expect(runsApi.queue(queueRequest)).resolves.toEqual(normalizedRun);
    await expect(runsApi.getById('run-1')).resolves.toEqual(normalizedRun);
    await expect(runsApi.getBySuite('suite-1', 'Running', 25)).resolves.toEqual([normalizedRun]);
    await expect(runsApi.getResults('run-1')).resolves.toEqual([normalizedResult]);
    await expect(runsApi.getResult('run-1', 'result-1')).resolves.toEqual(normalizedResult);

    expect(apiClientMock.post).toHaveBeenCalledWith('/runs', queueRequest);
    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/runs/run-1');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/suites/suite-1/runs?status=Running&limit=25');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(3, '/runs/run-1/results');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(4, '/runs/run-1/results/result-1');
  });

  it('omits absent run-history filters', async () => {
    apiClientMock.get.mockResolvedValueOnce({ data: [] });

    await expect(runsApi.getBySuite('suite-1')).resolves.toEqual([]);
    expect(apiClientMock.get).toHaveBeenCalledWith('/suites/suite-1/runs?');
  });

  it('normalizes numeric enums and safely parses or defaults result JSON', async () => {
    apiClientMock.get
      .mockResolvedValueOnce({ data: [{ ...run, status: 99 }] })
      .mockResolvedValueOnce({
        data: [{
          ...result,
          status: 2,
          metricsJson: '{"passed":1,"failed":2,"errors":3}',
          failureReasonsJson: '["first failure","second failure"]',
        }],
      })
      .mockResolvedValueOnce({
        data: {
          ...result,
          status: 99,
          metricsJson: '{not-json',
          failureReasonsJson: '{"reason":"not-an-array"}',
        },
      });

    await expect(runsApi.getBySuite('suite-1')).resolves.toEqual([
      { ...run, status: 'Unknown (99)' },
    ]);
    await expect(runsApi.getResults('run-1')).resolves.toEqual([
      expect.objectContaining({
        status: 'Error',
        passedExpectations: 1,
        failedExpectations: 2,
        errorExpectations: 3,
        failureReasons: ['first failure', 'second failure'],
      }),
    ]);
    await expect(runsApi.getResult('run-1', 'result-1')).resolves.toEqual(
      expect.objectContaining({
        status: 'Unknown (99)',
        passedExpectations: 0,
        failedExpectations: 0,
        errorExpectations: 0,
        failureReasons: [],
      }),
    );
  });

  it('propagates run failures', async () => {
    apiClientMock.post.mockRejectedValueOnce(failure);

    await expect(runsApi.queue({
      suiteId: 'suite-1',
      environmentId: 'environment-1',
      endpointId: 'endpoint-1',
      mappingSpecId: 'mapping-1',
    })).rejects.toBe(failure);
  });
});

describe('suitesApi', () => {
  const suite = {
    id: 'suite-1',
    projectId: 'project-1',
    name: 'Core prompts',
    description: 'Core regression suite',
    createdAt: '2026-08-12T00:00:00Z',
    updatedAt: '2026-08-12T00:00:00Z',
    testCaseCount: 1,
  };
  const request = { name: suite.name, description: suite.description };

  it('serializes suite CRUD, import, and export operations', async () => {
    const imported = { importedCount: 1, importedTestIds: ['external-1'] };
    const exported = new Blob(['tests: []'], { type: 'application/x-yaml' });
    const file = new File(['tests: []'], 'tests.yaml', { type: 'application/x-yaml' });
    apiClientMock.post
      .mockResolvedValueOnce({ data: suite })
      .mockResolvedValueOnce({ data: imported });
    apiClientMock.get
      .mockResolvedValueOnce({ data: [suite] })
      .mockResolvedValueOnce({ data: suite })
      .mockResolvedValueOnce({ data: exported });
    apiClientMock.put.mockResolvedValueOnce({ data: suite });
    apiClientMock.delete.mockResolvedValueOnce({});

    await expect(suitesApi.create('project-1', request)).resolves.toEqual(suite);
    await expect(suitesApi.getByProject('project-1')).resolves.toEqual([suite]);
    await expect(suitesApi.getById('suite-1')).resolves.toEqual(suite);
    await expect(suitesApi.update('suite-1', request)).resolves.toEqual(suite);
    await expect(suitesApi.delete('suite-1')).resolves.toBeUndefined();
    await expect(suitesApi.importTests('suite-1', file)).resolves.toEqual(imported);
    await expect(suitesApi.exportTests('suite-1')).resolves.toBe(exported);

    expect(apiClientMock.post).toHaveBeenNthCalledWith(1, '/suites?projectId=project-1', request);
    const importCall = apiClientMock.post.mock.calls[1];
    expect(importCall[0]).toBe('/suites/suite-1/tests/import');
    expect(importCall[1]).toBeInstanceOf(FormData);
    expect((importCall[1] as FormData).get('file')).toBe(file);
    expect(importCall[2]).toEqual({ headers: { 'Content-Type': 'multipart/form-data' } });
    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/projects/project-1/suites');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/suites/suite-1');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(3, '/suites/suite-1/tests/export', { responseType: 'blob' });
    expect(apiClientMock.put).toHaveBeenCalledWith('/suites/suite-1', request);
    expect(apiClientMock.delete).toHaveBeenCalledWith('/suites/suite-1');
  });

  it('propagates suite failures', async () => {
    apiClientMock.get.mockRejectedValueOnce(failure);

    await expect(suitesApi.getById('suite-1')).rejects.toBe(failure);
  });
});

describe('suite module testsApi', () => {
  const testCase = {
    id: 'test-1',
    suiteId: 'suite-1',
    externalId: 'external-1',
    name: 'Greets the user',
    inputSpecJson: '{"messages":[]}',
    expectationsJson: '[]',
    createdAt: '2026-08-12T00:00:00Z',
    updatedAt: '2026-08-12T00:00:00Z',
  };
  const request = {
    externalId: testCase.externalId,
    name: testCase.name,
    inputSpecJson: testCase.inputSpecJson,
    expectationsJson: testCase.expectationsJson,
  };

  it('serializes the suite module test-case CRUD operations', async () => {
    apiClientMock.post.mockResolvedValueOnce({ data: testCase });
    apiClientMock.get
      .mockResolvedValueOnce({ data: [testCase] })
      .mockResolvedValueOnce({ data: testCase });
    apiClientMock.put.mockResolvedValueOnce({ data: testCase });
    apiClientMock.delete.mockResolvedValueOnce({});

    await expect(suiteTestsApi.create('suite-1', request)).resolves.toEqual(testCase);
    await expect(suiteTestsApi.getBySuite('suite-1')).resolves.toEqual([testCase]);
    await expect(suiteTestsApi.getById('test-1')).resolves.toEqual(testCase);
    await expect(suiteTestsApi.update('test-1', request)).resolves.toEqual(testCase);
    await expect(suiteTestsApi.delete('test-1')).resolves.toBeUndefined();

    expect(apiClientMock.post).toHaveBeenCalledWith('/suites/suite-1/tests', request);
    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/suites/suite-1/tests');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/tests/test-1');
    expect(apiClientMock.put).toHaveBeenCalledWith('/tests/test-1', request);
    expect(apiClientMock.delete).toHaveBeenCalledWith('/tests/test-1');
  });

  it('propagates suite module test-case failures', async () => {
    apiClientMock.put.mockRejectedValueOnce(failure);

    await expect(suiteTestsApi.update('test-1', request)).rejects.toBe(failure);
  });
});

describe('testsApi', () => {
  const testCase = {
    id: 'test-1',
    suiteId: 'suite-1',
    externalId: 'external-1',
    name: 'Greets the user',
    inputSpecJson: '{"messages":[]}',
    expectationsJson: '[]',
    createdAt: '2026-08-12T00:00:00Z',
  };
  const request = {
    externalId: testCase.externalId,
    name: testCase.name,
    inputSpecJson: testCase.inputSpecJson,
    expectationsJson: testCase.expectationsJson,
  };

  it('serializes test CRUD, YAML import, and YAML export operations', async () => {
    const file = new File(['tests: []'], 'tests.yaml', { type: 'application/x-yaml' });
    const yaml = 'tests: []';
    apiClientMock.get
      .mockResolvedValueOnce({ data: [testCase] })
      .mockResolvedValueOnce({ data: testCase })
      .mockResolvedValueOnce({ data: yaml });
    apiClientMock.post
      .mockResolvedValueOnce({ data: testCase })
      .mockResolvedValueOnce({});
    apiClientMock.put.mockResolvedValueOnce({ data: testCase });
    apiClientMock.delete.mockResolvedValueOnce({});

    await expect(testsApi.getBySuite('suite-1')).resolves.toEqual([testCase]);
    await expect(testsApi.getById('test-1')).resolves.toEqual(testCase);
    await expect(testsApi.create('suite-1', request)).resolves.toEqual(testCase);
    await expect(testsApi.update('test-1', request)).resolves.toEqual(testCase);
    await expect(testsApi.delete('test-1')).resolves.toBeUndefined();
    await expect(testsApi.importYaml('suite-1', file)).resolves.toBeUndefined();
    await expect(testsApi.exportYaml('suite-1')).resolves.toBe(yaml);

    expect(apiClientMock.get).toHaveBeenNthCalledWith(1, '/suites/suite-1/tests');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(2, '/tests/test-1');
    expect(apiClientMock.get).toHaveBeenNthCalledWith(3, '/suites/suite-1/tests/export', { responseType: 'text' });
    expect(apiClientMock.post).toHaveBeenNthCalledWith(1, '/suites/suite-1/tests', request);
    const importCall = apiClientMock.post.mock.calls[1];
    expect(importCall[0]).toBe('/suites/suite-1/tests/import');
    expect(importCall[1]).toBeInstanceOf(FormData);
    expect((importCall[1] as FormData).get('file')).toBe(file);
    expect(importCall[2]).toEqual({ headers: { 'Content-Type': 'multipart/form-data' } });
    expect(apiClientMock.put).toHaveBeenCalledWith('/tests/test-1', request);
    expect(apiClientMock.delete).toHaveBeenCalledWith('/tests/test-1');
  });

  it('propagates test-case failures', async () => {
    apiClientMock.get.mockRejectedValueOnce(failure);

    await expect(testsApi.exportYaml('suite-1')).rejects.toBe(failure);
  });
});
