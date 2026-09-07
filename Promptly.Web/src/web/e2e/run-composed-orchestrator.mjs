import { spawn } from 'node:child_process';
import {
  lstat,
  mkdir,
  readFile,
  readdir,
  rm,
  stat,
  writeFile,
} from 'node:fs/promises';
import path from 'node:path';
import { resolveFixedE2EPaths } from './harness-safety.mjs';
import { evaluatePhaseReceipts } from './phase-verification.mjs';

const {
  artifactsRoot,
  repositoryRoot,
  e2eRoot,
} = resolveFixedE2EPaths(import.meta.url);
const phaseRunner = path.join(e2eRoot, 'run-composed.mjs');
const phases = ['proxy', 'direct'];

const prepareArtifactsDirectory = async () => {
  let current = repositoryRoot;
  for (const component of ['artifacts', 'test-results']) {
    current = path.join(current, component);
    try {
      const information = await lstat(current);
      if (information.isSymbolicLink() || !information.isDirectory()) {
        throw new Error(`Artifact path component must be a real directory: ${current}`);
      }
    } catch (error) {
      if (error?.code !== 'ENOENT') {
        throw error;
      }
      await mkdir(current, { mode: 0o700 });
    }
  }

  try {
    const information = await lstat(artifactsRoot);
    if (information.isSymbolicLink() || !information.isDirectory()) {
      throw new Error(`Artifact output must be a real directory: ${artifactsRoot}`);
    }
    await rm(artifactsRoot, { recursive: true });
  } catch (error) {
    if (error?.code !== 'ENOENT') {
      throw error;
    }
  }
  await mkdir(artifactsRoot, { recursive: true, mode: 0o700 });
};

const runPhase = (phase) => new Promise((resolve) => {
  const child = spawn(process.execPath, [phaseRunner], {
    cwd: repositoryRoot,
    env: { ...process.env, PROMPTLY_E2E_PHASE: phase },
    stdio: 'inherit',
  });
  child.once('error', (error) => resolve({ code: -1, error: error.message }));
  child.once('close', (code) => resolve({ code: code ?? -1 }));
});

const readPhaseReceipt = async (phase) => {
  const receiptPath = path.join(artifactsRoot, phase, 'phase-receipt.json');
  try {
    const receipt = JSON.parse(await readFile(receiptPath, 'utf8'));
    if (receipt.schema !== 1 || receipt.phase !== phase) {
      throw new Error(`Invalid ${phase} phase receipt`);
    }
    return receipt;
  } catch (error) {
    return {
      schema: 1,
      phase,
      status: 'failed',
      error: error instanceof Error ? error.message : String(error),
    };
  }
};

const collectFiles = async (directory, relative = '') => {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const absolute = path.join(directory, entry.name);
    const childRelative = path.join(relative, entry.name);
    if (entry.isDirectory()) {
      files.push(...await collectFiles(absolute, childRelative));
    } else {
      const information = await stat(absolute);
      files.push({ path: childRelative.split(path.sep).join('/'), size: information.size });
    }
  }
  return files;
};

await prepareArtifactsDirectory();

const results = [];
for (const phase of phases) {
  const result = await runPhase(phase);
  const receipt = await readPhaseReceipt(phase);
  results.push({ phase, exitCode: result.code, receipt, error: result.error ?? null });
}

const phaseVerification = evaluatePhaseReceipts(results, phases);
const passedPhaseCount = results.filter(({ exitCode, receipt }) => (
  exitCode === 0
  && receipt.schema === 1
  && receipt.status === 'passed'
  && receipt.uploadIsSafe === true
  && receipt.cleanupCommandPassed === true
  && receipt.cleanupVerificationPassed === true
)).length;
await writeFile(
  path.join(artifactsRoot, 'phase-receipts.json'),
  `${JSON.stringify({ schema: 1, requiredPhases: phases, phases: results }, null, 2)}\n`,
  { mode: 0o600 },
);
await writeFile(
  path.join(artifactsRoot, 'run-metadata.json'),
  `${JSON.stringify({
    schema: 1,
    status: phaseVerification.passed ? 'passed' : 'failed',
    requiredPhaseCount: phases.length,
    passedPhaseCount,
    errorCount: phaseVerification.failures.length,
    errorCategories: phaseVerification.failures.map((failure) => (
      failure.split(' ', 1)[0] === 'proxy' || failure.split(' ', 1)[0] === 'direct'
        ? `${failure.split(' ', 1)[0]}-phase`
        : 'phase-verification'
    )),
  }, null, 2)}\n`,
  { mode: 0o600 },
);
await writeFile(
  path.join(artifactsRoot, 'cleanup-attestation.json'),
  `${JSON.stringify({
    schema: 1,
    allRequiredPhasesCompleted: phaseVerification.passed,
    phases: results.map(({ phase, receipt }) => ({
      phase,
      cleanupCommandPassed: receipt.cleanupCommandPassed ?? null,
      cleanupVerificationPassed: receipt.cleanupVerificationPassed ?? null,
      status: receipt.status,
    })),
  }, null, 2)}\n`,
  { mode: 0o600 },
);
await writeFile(
  path.join(artifactsRoot, 'runner.log'),
  `${results.map(({ phase, exitCode, receipt, error }) => (
    `${phase}: exit=${exitCode} status=${receipt.status}${error ? ` error=${error}` : ''}`
  )).join('\n')}\n`,
  { mode: 0o600 },
);
await writeFile(
  path.join(artifactsRoot, 'artifact-manifest.json'),
  `${JSON.stringify({ schema: 1, artifacts: await collectFiles(artifactsRoot) }, null, 2)}\n`,
  { mode: 0o600 },
);

if (!phaseVerification.passed) {
  throw new Error(`Composed E2E phase verification failed: ${phaseVerification.failures.join('; ')}`);
}

await writeFile(path.join(artifactsRoot, 'upload-safe.json'), '{"schema":1,"safe":true}\n', { mode: 0o600 });
console.log(`Composed E2E verification passed; artifacts: ${artifactsRoot}`);
