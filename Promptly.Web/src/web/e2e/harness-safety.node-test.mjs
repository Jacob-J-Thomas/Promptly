import assert from 'node:assert/strict';
import test from 'node:test';
import { unzipSync, zipSync } from 'fflate';
import {
  assertBlockedEgressProbe,
  createArtifactSanitizer,
} from './harness-safety.mjs';

const jwt = `${'eyJhbGciOiJIUzI1NiJ9'}.${'eyJzdWIiOiJ1c2VyLTEifQ'}.${'signature_value_1234567890'}`;
const password = 'Promptly-12345678-1234-1234-1234-123456789abc-A1';

test('sanitizes known and derived credentials inside compressed traces', () => {
  const sanitizer = createArtifactSanitizer(['database-secret', 'jwt-signing-key']);
  const trace = Buffer.from(zipSync({
    'trace.network': Buffer.from(
      `database-secret\nAuthorization: Bearer ${jwt}\npassword=${password}\n`,
    ),
    'resources/binary': Buffer.from([0, 255, 1, 2, 3]),
  }));

  assert.throws(() => sanitizer.assertArchiveSafe(trace), /credential material/);
  const sanitized = sanitizer.sanitizeArchive(trace);

  assert.equal(sanitized.changed, true);
  sanitizer.assertArchiveSafe(sanitized.content);
  const entries = unzipSync(sanitized.content);
  const network = Buffer.from(entries['trace.network']).toString('utf8');
  assert.doesNotMatch(network, /database-secret/);
  assert.doesNotMatch(network, /eyJ/);
  assert.doesNotMatch(network, /Promptly-/);
  assert.deepEqual(Buffer.from(entries['resources/binary']), Buffer.from([0, 255, 1, 2, 3]));
});

test('rejects nested and over-limit archives instead of scanning them shallowly', () => {
  const nested = Buffer.from(zipSync({
    'nested.zip': zipSync({ 'secret.txt': Buffer.from(jwt) }),
  }));
  const nestedSanitizer = createArtifactSanitizer([]);
  assert.throws(() => nestedSanitizer.sanitizeArchive(nested), /Nested archive/);

  const boundedSanitizer = createArtifactSanitizer([], { maxArchiveBytes: 8 });
  assert.throws(
    () => boundedSanitizer.sanitizeArchive(Buffer.from(zipSync({ file: Buffer.alloc(16) }))),
    /artifact exceeds 8 bytes/,
  );
});

test('sanitizes Playwright HTML reports with compressed base64-embedded credentials', () => {
  const sanitizer = createArtifactSanitizer([]);
  const archive = Buffer.from(zipSync({
    'report.json': Buffer.from(JSON.stringify({ error: `expected Bearer ${jwt}` })),
  }));
  const html = Buffer.from(
    `<!doctype html><body>password=${password}</body>`
    + `<template id="playwrightReportBase64">data:application/zip;base64,${archive.toString('base64')}`
    + '</template>',
  );

  assert.throws(() => sanitizer.assertHtmlReportSafe(html), /credential material/);
  const sanitized = sanitizer.sanitizeHtmlReport(html);
  assert.equal(sanitized.changed, true);
  sanitizer.assertHtmlReportSafe(sanitized.content);
  assert.doesNotMatch(sanitized.content.toString('utf8'), /eyJ/);
  assert.doesNotMatch(sanitized.content.toString('utf8'), /Promptly-/);
});

test('egress proof requires both a working control and exact timeout marker', () => {
  const valid = {
    blocked: { code: 86, stderr: '', stdout: 'PROMPTLY_EGRESS_BLOCKED\n' },
    canary: { code: 0, stderr: '', stdout: '' },
    control: { code: 0, stderr: '', stdout: '' },
    marker: 'PROMPTLY_EGRESS_BLOCKED',
    service: 'promptly-web',
  };
  assert.doesNotThrow(() => assertBlockedEgressProbe(valid));
  assert.throws(
    () => assertBlockedEgressProbe({ ...valid, canary: { code: 1, stderr: '', stdout: '' } }),
    /routed egress canary failed/,
  );
  assert.throws(
    () => assertBlockedEgressProbe({ ...valid, control: { code: 127, stderr: 'nc: not found', stdout: '' } }),
    /control probe failed/,
  );
  assert.throws(
    () => assertBlockedEgressProbe({ ...valid, blocked: { code: 1, stderr: '', stdout: '' } }),
    /exact blocked-network contract/,
  );
  assert.throws(
    () => assertBlockedEgressProbe({ ...valid, blocked: { code: 86, stderr: 'exec failed', stdout: valid.marker } }),
    /exact blocked-network contract/,
  );
});

test('sanitization results can be rewritten without making safe finalization fail', () => {
  const sanitizer = createArtifactSanitizer([]);
  const html = Buffer.from(
    `<template id="playwrightReportBase64">data:application/zip;base64,${Buffer.from(zipSync({
      'report.json': Buffer.from(jwt),
    })).toString('base64')}</template>`,
  );

  const first = sanitizer.sanitizeHtmlReport(html);
  assert.equal(first.changed, true);
  const second = sanitizer.sanitizeHtmlReport(first.content);
  assert.equal(second.changed, false);
  sanitizer.assertHtmlReportSafe(second.content);
});
