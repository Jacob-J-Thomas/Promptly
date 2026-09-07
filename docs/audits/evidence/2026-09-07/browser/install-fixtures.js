// Discovery fixture for playwright-cli run-code, AFTER navigating to http://localhost:3000/login.
// Inert synthetic auth/header strings are not credentials accepted by any backend. API calls are intercepted.
async page => {
  const ids = {
    project: '11111111-1111-4111-8111-111111111111',
    environment: '22222222-2222-4222-8222-222222222222',
    endpoint: '33333333-3333-4333-8333-333333333333',
    mapping: '44444444-4444-4444-8444-444444444444',
    suite: '55555555-5555-4555-8555-555555555555',
    test: '66666666-6666-4666-8666-666666666666',
    run: '77777777-7777-4777-8777-777777777777',
    result: '88888888-8888-4888-8888-888888888888',
  };
  const now = '2026-09-07T18:00:00Z';
  const state = {
    projects: [{ id: ids.project, name: 'Fixture Project', description: 'UI-only fixture', ownerUserId: 'fixture-user', createdAt: now }],
    environments: [{ id: ids.environment, projectId: ids.project, name: 'Staging', baseUrl: 'https://api.example.test', hasHeaders: true, createdAt: now }],
    suites: [{ id: ids.suite, projectId: ids.project, name: 'Chat smoke tests', description: 'Fixture suite', createdAt: now, testCaseCount: 1 }],
    endpoints: [{ id: ids.endpoint, environmentId: ids.environment, name: 'Chat Completions', path: '/v1/chat/completions', httpMethod: 'POST', method: 'POST', timeoutSeconds: 30, createdAt: now, updatedAt: now }],
    tests: [{ id: ids.test, suiteId: ids.suite, externalId: 'TC-001', name: 'returns assistant text', description: 'Fixture test', inputSpecJson: '{"messages":[{"role":"user","content":"Hello"}]}', expectationsJson: '[]', createdAt: now, updatedAt: now }],
    run: { id: ids.run, suiteId: ids.suite, environmentId: ids.environment, endpointId: ids.endpoint, mappingSpecId: ids.mapping, status: 2, summaryJson: '{"passRate":1,"passed":1,"failed":0,"errors":0,"avgLatencyMs":123,"totalTokens":42,"totalCost":0.0012}', gitCommitHash: 'abcdef1234567890', createdAt: now, startedAt: now, completedAt: '2026-09-07T18:00:02Z' },
    result: { id: ids.result, runId: ids.run, testCaseId: ids.test, status: 0, traceJson: '{"input":"Hello","output":"Hi"}', metricsJson: '{"latencyMs":123}', failureReasonsJson: '[]', createdAt: now, testCaseName: 'returns assistant text', testCaseExternalId: 'TC-001' },
  };

  await page.evaluate(() => {
    localStorage.setItem('auth_token', 'fixture-token');
    localStorage.setItem('user', JSON.stringify({ id: 'fixture-user', name: 'Fixture User', email: 'fixture@example.test' }));
  });

  await page.unroute('http://localhost:5000/api/**');
  await page.route('http://localhost:5000/api/**', async route => {
    const request = route.request();
    const method = request.method();
    const path = request.url().replace(/^https?:\/\/[^/]+\/api/, '').split('?')[0];
    const json = async (payload, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(payload) });

    if (method === 'GET' && path === '/projects') return json(state.projects);
    if (method === 'GET' && path === `/projects/${ids.project}`) return json(state.projects[0]);
    if (method === 'POST' && path === '/projects') return json({ ...state.projects[0], name: 'Created Project' }, 201);
    if (method === 'GET' && path === `/projects/${ids.project}/environments`) return json(state.environments);
    if (method === 'GET' && path === `/environments/${ids.environment}`) return json({ ...state.environments[0], headers: { Authorization: 'Bearer fixture-secret' } });
    if (method === 'POST' && path === `/projects/${ids.project}/environments`) return json({ ...state.environments[0], name: 'Created Environment' }, 201);
    if (method === 'GET' && path === `/environments/${ids.environment}/endpoints`) return json(state.endpoints);
    if (method === 'POST' && path === `/environments/${ids.environment}/endpoints`) return json(state.endpoints[0], 201);
    if (method === 'GET' && path === `/projects/${ids.project}/suites`) return json(state.suites);
    if (method === 'GET' && path === `/suites/${ids.suite}`) return json(state.suites[0]);
    if (method === 'POST' && path === '/suites') return json({ ...state.suites[0], name: 'Created Suite' }, 201);
    if (method === 'GET' && path === `/suites/${ids.suite}/tests`) return json(state.tests);
    if (method === 'POST' && path === `/suites/${ids.suite}/tests`) return json(state.tests[0], 201);
    if (method === 'POST' && path === `/suites/${ids.suite}/tests/import`) return json({ importedCount: 1, importedTestIds: ['TC-IMPORT'] });
    if (method === 'GET' && path === `/suites/${ids.suite}/tests/export`) return route.fulfill({ status: 200, contentType: 'application/x-yaml', body: 'tests:\n  - externalId: TC-001\n    name: returns assistant text\n' });
    if (method === 'POST' && path === `/endpoints/${ids.endpoint}/mapping/propose`) return json({ mappingSpecJson: '{"outputPath":"$.choices[0].message.content"}', reason: 'Fixture proposal' });
    if (method === 'POST' && path === `/endpoints/${ids.endpoint}/mapping/validate`) return json({ success: true, previewTrace: { output: 'Hi' } });
    if (method === 'POST' && path === `/endpoints/${ids.endpoint}/mapping`) return json({ id: ids.mapping, endpointId: ids.endpoint, name: 'Chat Completions Default Mapping', specJson: '{"outputPath":"$.choices[0].message.content"}', isDefault: false, createdAt: now, updatedAt: now }, 201);
    if (method === 'POST' && path === `/mapping/${ids.mapping}/set-default`) return route.fulfill({ status: 204 });
    if (method === 'GET' && path === `/suites/${ids.suite}/runs`) return json([state.run]);
    if (method === 'POST' && path === '/runs') return json(state.run, 201);
    if (method === 'GET' && path === `/runs/${ids.run}`) return json(state.run);
    if (method === 'GET' && path === `/runs/${ids.run}/results`) return json([state.result]);

    return json({ message: `Unhandled fixture route: ${method} ${path}` }, 500);
  });

  await page.goto('http://localhost:3000/');
  await page.waitForLoadState('networkidle');
  return { ids, url: page.url(), title: await page.title(), body: (await page.locator('body').innerText()).slice(0, 4000) };
}
