const requiredFixture = Object.freeze({
  host: 'run-suite-provider-stub',
  port: '8080',
  cidr: '172.30.0.2/32',
  network: 'promptly-run-suite',
  address: '172.30.0.2',
});

const correlationPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export const assertProviderEvidence = (
  records,
  { phase, expectedCorrelation = null, expectedCorrelations = null } = {},
) => {
  if (phase !== 'proxy' && phase !== 'direct') {
    throw new Error('provider evidence phase must be proxy or direct');
  }
  if (!Array.isArray(records) || records.length === 0) {
    throw new Error('Provider evidence is empty');
  }
  const expected = Array.isArray(expectedCorrelations)
    ? expectedCorrelations
    : expectedCorrelation ? [expectedCorrelation] : null;
  if (phase === 'direct' && (!expected || expected.length === 0
    || expected.some((correlation) => !correlationPattern.test(correlation)))) {
    throw new Error('Direct provider evidence requires runner correlations');
  }
  if (expected && new Set(expected).size !== expected.length) {
    throw new Error('Runner correlations must be distinct');
  }

  const sequences = new Set();
  const correlations = new Set();
  for (const record of records) {
    const keys = Object.keys(record).sort();
    const expectedKeys = phase === 'direct'
      ? [
        'authorized', 'correlation_id', 'kind', 'message_count', 'method',
        'model', 'path', 'sequence', 'valid_json',
      ]
      : ['authorized', 'kind', 'method', 'path', 'sequence'];
    if (JSON.stringify(keys) !== JSON.stringify(expectedKeys)) {
      throw new Error(`Provider evidence has an unexpected schema: ${keys.join(',')}`);
    }
    const validRecord = phase === 'direct'
      ? record.authorized === true
        && record.kind === 'chat_completion'
        && record.method === 'POST'
        && record.path === '/v1/chat/completions'
        && record.valid_json === true
        && record.model === 'verification-model'
        && Number.isInteger(record.message_count)
        && record.message_count > 0
        && typeof record.correlation_id === 'string'
        && correlationPattern.test(record.correlation_id)
        && (!expected || expected.includes(record.correlation_id))
      : record.authorized === true
        && record.kind === 'models'
        && record.method === 'GET'
        && record.path === '/v1/models';
    if (
      !validRecord
      || !Number.isInteger(record.sequence)
      || record.sequence < 1
      || sequences.has(record.sequence)
      || (phase === 'direct' && correlations.has(record.correlation_id))
    ) {
      throw new Error('Provider evidence contains an unexpected request');
    }
    sequences.add(record.sequence);
    if (phase === 'direct') {
      correlations.add(record.correlation_id);
    }
  }
  if (phase === 'direct') {
    if (expected && records.length !== expected.length) {
      throw new Error(`Direct fixture expected exactly ${expected.length} provider POSTs, received ${records.length}`);
    }
    if (expected && JSON.stringify([...correlations].sort()) !== JSON.stringify([...expected].sort())) {
      throw new Error('Provider evidence correlations do not match the runner scenarios');
    }
  }
  return true;
};

export const evaluatePhaseReceipts = (results, requiredPhases = ['proxy', 'direct']) => {
  const failures = [];
  if (!Array.isArray(results)) {
    return { passed: false, failures: ['phase results are not an array'] };
  }

  const observedPhases = results.map((result) => result?.phase);
  if (
    observedPhases.length !== requiredPhases.length
    || new Set(observedPhases).size !== observedPhases.length
    || requiredPhases.some((phase) => !observedPhases.includes(phase))
  ) {
    failures.push('required phase inventory is incomplete or duplicated');
  }

  for (const phase of requiredPhases) {
    const result = results.find((candidate) => candidate?.phase === phase);
    if (!result) {
      continue;
    }
    const receipt = result.receipt;
    if (result.exitCode !== 0) {
      failures.push(`${phase} phase exited with ${result.exitCode}`);
    }
    if (receipt?.schema !== 1 || receipt.phase !== phase || receipt.status !== 'passed') {
      failures.push(`${phase} phase receipt is missing or failed`);
    }
    if (receipt?.uploadIsSafe !== true) {
      failures.push(`${phase} phase artifacts are not upload-safe`);
    }
    if (receipt?.cleanupCommandPassed !== true || receipt?.cleanupVerificationPassed !== true) {
      failures.push(`${phase} phase cleanup is incomplete`);
    }
  }

  return { passed: failures.length === 0, failures };
};

export const assertDirectFixtureBoundary = (configuration) => {
  const {
    fixture = {},
    server = {},
    provider = {},
    egressMembers = [],
    ingressMembers = [],
  } = configuration ?? {};
  const failures = [];
  if (
    fixture.host !== requiredFixture.host
    || fixture.port !== requiredFixture.port
    || fixture.cidr !== requiredFixture.cidr
    || fixture.network !== requiredFixture.network
    || fixture.address !== requiredFixture.address
  ) {
    failures.push('fixture host, port, address, network, or CIDR is outside the admitted boundary');
  }
  if (
    server.requireProxy !== false
    || server.proxyUrl !== null
    || server.allowlistHost !== requiredFixture.host
    || server.allowlistPort !== requiredFixture.port
    || server.allowlistCidr !== requiredFixture.cidr
    || server.ambientProxyCleared !== true
  ) {
    failures.push('direct server transport or exact allowlist boundary is invalid');
  }
  if (
    provider.network !== requiredFixture.network
    || provider.address !== requiredFixture.address
    || provider.attachedToEgress === true
    || provider.publishedPort === true
  ) {
    failures.push('fixture provider escaped its isolated internal network');
  }
  if (JSON.stringify([...egressMembers].sort()) !== JSON.stringify(['promptly-egress-proxy'])) {
    failures.push('egress membership is broader than the proxy');
  }
  if (JSON.stringify([...ingressMembers].sort()) !== JSON.stringify(['promptly-ingress-gateway'])) {
    failures.push('ingress membership is broader than the gateway');
  }
  if (failures.length > 0) {
    throw new Error(failures.join('; '));
  }
  return true;
};
