import assert from 'node:assert/strict';
import test from 'node:test';
import {
  assertDirectFixtureBoundary,
  assertProviderEvidence,
  evaluatePhaseReceipts,
} from './phase-verification.mjs';

const receipt = (phase, overrides = {}) => ({
  schema: 1,
  phase,
  status: 'passed',
  uploadIsSafe: true,
  cleanupCommandPassed: true,
  cleanupVerificationPassed: true,
  ...overrides,
});

const validPhaseResults = () => [
  { phase: 'proxy', exitCode: 0, receipt: receipt('proxy') },
  { phase: 'direct', exitCode: 0, receipt: receipt('direct') },
];

test('accepts exactly two successful, upload-safe, cleaned phases', () => {
  assert.deepEqual(evaluatePhaseReceipts(validPhaseResults()), { passed: true, failures: [] });
});

test('cancellation prevents a successful aggregate even when phase receipts passed', () => {
  const result = evaluatePhaseReceipts(validPhaseResults(), ['proxy', 'direct'], 'SIGTERM');
  assert.equal(result.passed, false);
  assert.match(result.failures.join('; '), /orchestrator received SIGTERM/);
});

for (const [name, results, expected] of [
  [
    'missing direct receipt',
    [{ phase: 'proxy', exitCode: 0, receipt: receipt('proxy') }],
    /required phase inventory is incomplete or duplicated/,
  ],
  [
    'failed phase',
    [{ phase: 'proxy', exitCode: 1, receipt: receipt('proxy', { status: 'failed' }) }, ...validPhaseResults().slice(1)],
    /proxy phase exited with 1/,
  ],
  [
    'unsafe artifacts',
    [{ phase: 'proxy', exitCode: 0, receipt: receipt('proxy', { uploadIsSafe: false }) }, ...validPhaseResults().slice(1)],
    /proxy phase artifacts are not upload-safe/,
  ],
  [
    'incomplete cleanup',
    [{ phase: 'proxy', exitCode: 0, receipt: receipt('proxy', { cleanupVerificationPassed: false }) }, ...validPhaseResults().slice(1)],
    /proxy phase cleanup is incomplete/,
  ],
]) {
  test(`rejects ${name}`, () => {
    const result = evaluatePhaseReceipts(results);
    assert.equal(result.passed, false);
    assert.match(result.failures.join('; '), expected);
  });
}

const validFixture = () => ({
  fixture: {
    host: 'run-suite-provider-stub',
    port: '8080',
    cidr: '172.30.0.2/32',
    network: 'promptly-run-suite',
    address: '172.30.0.2',
  },
  server: {
    requireProxy: false,
    proxyUrl: null,
    allowlistHost: 'run-suite-provider-stub',
    allowlistPort: '8080',
    allowlistCidr: '172.30.0.2/32',
    ambientProxyCleared: true,
  },
  provider: {
    network: 'promptly-run-suite',
    address: '172.30.0.2',
    attachedToEgress: false,
    publishedPort: false,
  },
  egressMembers: ['promptly-egress-proxy'],
  ingressMembers: ['promptly-ingress-gateway'],
});

test('accepts the exact direct fixture boundary', () => {
  assert.equal(assertDirectFixtureBoundary(validFixture()), true);
});

const expectedCorrelation = '123e4567-e89b-12d3-a456-426614174000';
const validProviderRecord = (correlationId = expectedCorrelation) => ({
  authorized: true,
  correlation_id: correlationId,
  kind: 'chat_completion',
  message_count: 1,
  method: 'POST',
  model: 'verification-model',
  path: '/v1/chat/completions',
  sequence: 1,
  valid_json: true,
});

test('accepts direct provider evidence bound to the runner correlation', () => {
  assert.equal(assertProviderEvidence([validProviderRecord()], {
    phase: 'direct',
    expectedCorrelation,
  }), true);
});

test('rejects a different valid UUID from the runner correlation', () => {
  assert.throws(
    () => assertProviderEvidence([
      validProviderRecord('987e6543-e21b-12d3-a456-426614174000'),
    ], { phase: 'direct', expectedCorrelation }),
    /correlation does not match the runner scenario/,
  );
});

for (const [name, mutate, expected] of [
  ['a widened CIDR', (configuration) => { configuration.fixture.cidr = '172.30.0.0/29'; }, /outside the admitted boundary/],
  ['a different private host', (configuration) => { configuration.server.allowlistHost = 'postgres'; }, /exact allowlist boundary is invalid/],
  ['proxy transport', (configuration) => { configuration.server.requireProxy = true; }, /exact allowlist boundary is invalid/],
  ['egress attachment', (configuration) => { configuration.provider.attachedToEgress = true; }, /escaped its isolated internal network/],
  ['a published fixture port', (configuration) => { configuration.provider.publishedPort = true; }, /escaped its isolated internal network/],
  ['an extra egress member', (configuration) => { configuration.egressMembers.push('promptly-server'); }, /egress membership is broader/],
]) {
  test(`rejects ${name}`, () => {
    const configuration = validFixture();
    mutate(configuration);
    assert.throws(() => assertDirectFixtureBoundary(configuration), expected);
  });
}
