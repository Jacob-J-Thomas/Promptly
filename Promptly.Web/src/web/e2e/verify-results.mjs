import { readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { resolveFixedE2EPaths } from './harness-safety.mjs';

const {
  artifactsRoot,
  e2eRoot,
  webRoot,
} = resolveFixedE2EPaths(import.meta.url);
const reportPath = path.join(artifactsRoot, 'results.json');
const verificationPath = path.join(artifactsRoot, 'verification.json');
const inventoryPath = path.join(e2eRoot, 'required-tests.json');

const normalizeFile = (file, relativeRoot = webRoot) => {
  if (!file) {
    return '';
  }
  const absolute = path.isAbsolute(file) ? path.resolve(file) : path.resolve(relativeRoot, file);
  if (absolute !== webRoot && !absolute.startsWith(`${webRoot}${path.sep}`)) {
    throw new Error(`Reported test file escapes the web project: ${file}`);
  }
  return path.relative(webRoot, absolute).split(path.sep).join('/');
};

const identity = (test) => `${test.file}|${test.project}|${test.title}`;

const collectReportedTests = (suites, reportRoot, inheritedFile = '') => {
  const reported = [];
  for (const suite of suites ?? []) {
    const suiteFile = normalizeFile(suite.file, reportRoot) || inheritedFile;
    for (const spec of suite.specs ?? []) {
      const specFile = normalizeFile(spec.file, reportRoot) || suiteFile;
      for (const test of spec.tests ?? []) {
        reported.push({
          annotations: test.annotations ?? [],
          expectedStatus: test.expectedStatus,
          file: specFile,
          ok: spec.ok,
          project: test.projectName,
          results: test.results ?? [],
          status: test.status,
          tags: spec.tags ?? [],
          title: spec.title,
        });
      }
    }
    reported.push(...collectReportedTests(suite.suites, reportRoot, suiteFile));
  }
  return reported;
};

const findSpecFiles = async (directory) => {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...await findSpecFiles(entryPath));
    } else if (entry.name.endsWith('.e2e.ts')) {
      files.push(entryPath);
    }
  }
  return files;
};

const violations = [];
let requiredTests = [];
let reportedTests = [];

try {
  const inventory = JSON.parse(await readFile(inventoryPath, 'utf8'));
  if (inventory.schema !== 1 || !Array.isArray(inventory.tests)) {
    violations.push('Required-test inventory must use schema 1 with a tests array');
  } else {
    requiredTests = inventory.tests;
  }

  if (requiredTests.length === 0) {
    violations.push('Required-test inventory is empty');
  }

  const requiredIdentities = requiredTests.map(identity);
  if (new Set(requiredIdentities).size !== requiredIdentities.length) {
    violations.push('Required-test inventory contains duplicate identities');
  }

  const report = JSON.parse(await readFile(reportPath, 'utf8'));
  const reportRoot = path.resolve(report.config?.rootDir ?? '');
  if (reportRoot !== e2eRoot) {
    violations.push(`Playwright rootDir must resolve exactly to ${e2eRoot}`);
  }
  reportedTests = collectReportedTests(report.suites, reportRoot);
  if (reportedTests.length === 0) {
    violations.push('Playwright report contains zero tests');
  }

  const reportedIdentities = reportedTests.map(identity);
  if (new Set(reportedIdentities).size !== reportedIdentities.length) {
    violations.push('Playwright report contains duplicate test identities');
  }

  const expected = [...requiredIdentities].sort();
  const actual = [...reportedIdentities].sort();
  if (JSON.stringify(expected) !== JSON.stringify(actual)) {
    violations.push(
      `Required-test inventory mismatch: expected=${JSON.stringify(expected)} `
      + `actual=${JSON.stringify(actual)}`,
    );
  }

  if ((report.errors ?? []).length > 0) {
    violations.push(`Playwright reported ${report.errors.length} top-level errors`);
  }
  if (report.config?.forbidOnly !== true) {
    violations.push('Playwright forbidOnly must be enabled');
  }
  if (report.config?.fullyParallel !== false) {
    violations.push('Playwright fullyParallel must remain disabled');
  }
  if (report.config?.workers !== 1) {
    violations.push('Playwright must use exactly one worker');
  }

  const configuredProjects = report.config?.projects ?? [];
  if (configuredProjects.length !== 1 || configuredProjects[0]?.name !== 'chromium') {
    violations.push('Exactly one chromium project must be configured');
  }
  if (configuredProjects.some((project) => project.retries !== 0)) {
    violations.push('Playwright retries must remain zero');
  }
  if (configuredProjects.some((project) => project.repeatEach !== 1)) {
    violations.push('Playwright repeatEach must remain one');
  }

  for (const reported of reportedTests) {
    const label = identity(reported);
    if (reported.expectedStatus !== 'passed') {
      violations.push(`${label} expectedStatus is ${reported.expectedStatus}`);
    }
    if (reported.status !== 'expected') {
      violations.push(`${label} outcome classification is ${reported.status}`);
    }
    if (reported.ok !== true) {
      violations.push(`${label} did not complete successfully`);
    }
    if (!Array.isArray(reported.annotations) || reported.annotations.length > 0) {
      violations.push(`${label} has skip/fixme/fail annotations`);
    }
    if (!Array.isArray(reported.tags) || reported.tags.length > 0) {
      violations.push(`${label} has unapproved tags: ${reported.tags.join(',')}`);
    }
    if (reported.results.length !== 1) {
      violations.push(`${label} executed ${reported.results.length} attempts instead of one`);
      continue;
    }
    const [result] = reported.results;
    if (result.retry !== 0) {
      violations.push(`${label} ran at unexpected retry index ${result.retry}`);
    }
    if (result.status !== 'passed') {
      violations.push(`${label} result status is ${result.status}`);
    }
  }

  const forbiddenSourcePattern = /\b(?:test|describe)\.(?:only|skip|fixme|fail)\b|@quarantine|\bretries\s*:/;
  for (const specFile of await findSpecFiles(e2eRoot)) {
    const source = await readFile(specFile, 'utf8');
    if (forbiddenSourcePattern.test(source)) {
      violations.push(`${normalizeFile(specFile)} contains focused, skipped, quarantined, or retry syntax`);
    }
  }
} catch (error) {
  violations.push(error instanceof Error ? error.message : String(error));
}

const verification = {
  schema: 1,
  requiredCount: requiredTests.length,
  reportedCount: reportedTests.length,
  status: violations.length === 0 ? 'passed' : 'failed',
  violations,
};
await writeFile(verificationPath, `${JSON.stringify(verification, null, 2)}\n`);

if (violations.length > 0) {
  throw new Error(`E2E result verification failed:\n${violations.join('\n')}`);
}

console.log(`E2E required-test inventory verified: ${reportedTests.length}/${requiredTests.length}`);
