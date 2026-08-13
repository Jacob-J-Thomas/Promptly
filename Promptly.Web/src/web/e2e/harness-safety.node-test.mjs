import assert from 'node:assert/strict';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { unzipSync, zipSync } from 'fflate';
import {
  assertNoDefaultInternetRoute,
  createSafeRunMetadata,
  createArtifactSanitizer,
  resolveFixedE2EPaths,
} from './harness-safety.mjs';

const jwt = `${'eyJhbGciOiJIUzI1NiJ9'}.${'eyJzdWIiOiJ1c2VyLTEifQ'}.${'signature_value_1234567890'}`;
const password = 'Promptly-12345678-1234-1234-1234-123456789abc-A1';

test('pins E2E artifacts to the repository even when the environment requests a redirect', () => {
  const previous = process.env.PROMPTLY_E2E_ARTIFACT_DIR;
  const redirectedRoot = path.join(path.parse(process.cwd()).root, 'attacker-controlled-artifacts');
  process.env.PROMPTLY_E2E_ARTIFACT_DIR = redirectedRoot;
  try {
    const paths = resolveFixedE2EPaths(import.meta.url);
    const expectedE2ERoot = path.dirname(fileURLToPath(import.meta.url));
    assert.equal(paths.e2eRoot, expectedE2ERoot);
    assert.equal(
      paths.artifactsRoot,
      path.join(paths.repositoryRoot, 'artifacts/test-results/e2e'),
    );
    assert.notEqual(paths.artifactsRoot, redirectedRoot);
  } finally {
    if (previous === undefined) {
      delete process.env.PROMPTLY_E2E_ARTIFACT_DIR;
    } else {
      process.env.PROMPTLY_E2E_ARTIFACT_DIR = previous;
    }
  }
});

test('persists only allowlisted error categories, never runtime error contents', () => {
  const networkDerivedSecret = 'upstream said token=network-secret';
  const metadata = createSafeRunMetadata({
    completedAt: new Date('2026-08-12T12:01:00.000Z'),
    errorCategories: ['verification', networkDerivedSecret],
    errorCount: 2,
    startedAt: new Date('2026-08-12T12:00:00.000Z'),
  });
  const serialized = JSON.stringify(metadata);

  assert.deepEqual(metadata.errorCategories, ['verification', 'unclassified']);
  assert.equal(metadata.errorCount, 2);
  assert.equal(metadata.status, 'failed');
  assert.doesNotMatch(serialized, /network-secret|upstream said|token=/);
});

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

test('rejects duplicate ZIP entry names before a safe entry can shadow a credential', () => {
  const shadowedName = Buffer.from('shadowed.txt');
  const retainedName = Buffer.from('retained.txt');
  const duplicate = Buffer.from(zipSync({
    'shadowed.txt': Buffer.from(jwt),
    'retained.txt': Buffer.from('safe content'),
  }));
  let replacements = 0;
  for (let offset = duplicate.indexOf(shadowedName); offset >= 0;) {
    duplicate.set(retainedName, offset);
    replacements += 1;
    offset = duplicate.indexOf(shadowedName, offset + retainedName.length);
  }
  assert.equal(replacements, 2);

  const sanitizer = createArtifactSanitizer([]);
  assert.throws(() => sanitizer.sanitizeArchive(duplicate), /duplicate entry/);
  assert.throws(() => sanitizer.assertArchiveSafe(duplicate), /duplicate entry/);
});

test('rejects ZIP entry names that a plain-object result cannot represent safely', () => {
  const ordinaryName = Buffer.from('safe-name');
  const specialName = Buffer.from('__proto__');
  const archive = Buffer.from(zipSync({
    'safe-name': Buffer.from(jwt),
  }));
  let replacements = 0;
  for (let offset = archive.indexOf(ordinaryName); offset >= 0;) {
    archive.set(specialName, offset);
    replacements += 1;
    offset = archive.indexOf(ordinaryName, offset + specialName.length);
  }
  assert.equal(replacements, 2);

  const sanitizer = createArtifactSanitizer([]);
  assert.throws(() => sanitizer.sanitizeArchive(archive), /entry inventory/);
  const html = Buffer.from(
    '<template id="playwrightReportBase64">data:application/zip;base64,'
    + `${archive.toString('base64')}</template>`,
  );
  assert.throws(() => sanitizer.sanitizeHtmlReport(html), /entry inventory/);
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

test('egress proof requires working controls and no workload default routes', () => {
  const valid = {
    canary: { code: 0, stderr: '', stdout: '' },
    control: { code: 0, stderr: '', stdout: '' },
    ipv4Routes: {
      code: 0,
      stderr: '',
      stdout: 'Iface Destination Gateway Flags RefCnt Use Metric Mask MTU Window IRTT\n'
        + 'eth0 000013AC 00000000 0001 0 0 0 0000FFFF 0 0 0\n',
    },
    ipv6Routes: {
      code: 0,
      stderr: '',
      stdout: '00000000000000000000000000000000 00 '
        + '00000000000000000000000000000000 00 '
        + '00000000000000000000000000000000 ffffffff 00000001 00000000 00200200 lo\n',
    },
    service: 'promptly-web',
  };
  assert.doesNotThrow(() => assertNoDefaultInternetRoute(valid));
  assert.throws(
    () => assertNoDefaultInternetRoute({ ...valid, canary: { code: 1, stderr: '', stdout: '' } }),
    /routed egress canary failed/,
  );
  assert.throws(
    () => assertNoDefaultInternetRoute({
      ...valid,
      control: { code: 127, stderr: 'nc: not found', stdout: '' },
    }),
    /control probe failed/,
  );
  assert.throws(
    () => assertNoDefaultInternetRoute({
      ...valid,
      ipv4Routes: {
        code: 0,
        stderr: '',
        stdout: valid.ipv4Routes.stdout
          + 'eth0 00000000 010013AC 0003 0 0 0 00000000 0 0 0\n',
      },
    }),
    /active default IPv4 route/,
  );
  assert.throws(
    () => assertNoDefaultInternetRoute({
      ...valid,
      ipv6Routes: {
        code: 0,
        stderr: '',
        stdout: valid.ipv6Routes.stdout
          + '00000000000000000000000000000000 00 '
          + '00000000000000000000000000000000 00 '
          + 'fe800000000000000000000000000001 00000000 00000000 00000000 00000003 eth0\n',
      },
    }),
    /default IPv6 route/,
  );
  assert.throws(
    () => assertNoDefaultInternetRoute({
      ...valid,
      ipv4Routes: {
        code: 0,
        stderr: '',
        stdout: valid.ipv4Routes.stdout.replace('0001', 'not-hex'),
      },
    }),
    /invalid entry/,
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
