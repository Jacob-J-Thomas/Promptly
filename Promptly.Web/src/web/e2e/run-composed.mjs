import { spawn } from 'node:child_process';
import { randomBytes, randomInt } from 'node:crypto';
import { appendFileSync } from 'node:fs';
import {
  lstat,
  mkdir,
  readFile,
  readdir,
  rename,
  rm,
  stat,
  writeFile,
} from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertBlockedEgressProbe,
  createArtifactSanitizer,
} from './harness-safety.mjs';

const e2eRoot = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.dirname(e2eRoot);
const repositoryRoot = path.resolve(webRoot, '../../..');
const composeFile = path.join(repositoryRoot, 'docker/docker-compose.e2e.yml');
const allowedArtifactsRoot = path.join(repositoryRoot, 'artifacts');
const artifactsRoot = path.join(allowedArtifactsRoot, 'test-results/e2e');

const prepareArtifactsDirectory = async () => {
  let current = repositoryRoot;
  const components = ['artifacts', 'test-results'];
  for (const component of components) {
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
  await mkdir(artifactsRoot, { mode: 0o700 });
};

await prepareArtifactsDirectory();

const startedAt = new Date();
const runLogPath = path.join(artifactsRoot, 'runner.log');
const projectName = `promptly-e2e-${process.pid}-${randomBytes(6).toString('hex')}`;
const databasePassword = randomBytes(32).toString('base64url');
const jwtKey = randomBytes(64).toString('base64');
const secrets = [databasePassword, jwtKey];
const artifactSanitizer = createArtifactSanitizer(secrets);
const workerImage = `${projectName}-worker:local`;
const projectImages = [
  `${projectName}-promptly-egress-proxy:latest`,
  `${projectName}-promptly-server:latest`,
  `${projectName}-promptly-web:latest`,
  workerImage,
];
const processAbortController = new AbortController();
let receivedSignal = null;
let logWriteError = null;

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.once(signal, () => {
    receivedSignal = signal;
    processAbortController.abort(new Error(`Received ${signal}`));
  });
}

const redact = (value) => {
  let redacted = value;
  for (const secret of secrets) {
    redacted = redacted.replaceAll(secret, '[REDACTED]');
  }
  return redacted;
};

const appendLog = (value) => {
  try {
    appendFileSync(runLogPath, redact(value));
  } catch (error) {
    logWriteError ??= error;
  }
};

const createRedactedStream = (destination) => {
  let pending = '';

  const flushSafePrefix = (final) => {
    pending = redact(pending);
    let heldLength = 0;
    if (!final) {
      for (const secret of secrets) {
        const maximum = Math.min(secret.length - 1, pending.length);
        for (let length = maximum; length > heldLength; length -= 1) {
          if (pending.endsWith(secret.slice(0, length))) {
            heldLength = length;
            break;
          }
        }
      }
    }
    const safeLength = pending.length - heldLength;
    const safe = pending.slice(0, safeLength);
    pending = pending.slice(safeLength);
    if (safe.length > 0) {
      destination.write(safe);
      appendLog(safe);
    }
  };

  return {
    push(value) {
      pending += value;
      flushSafePrefix(false);
    },
    flush() {
      flushSafePrefix(true);
    },
  };
};

const run = async (command, args, options = {}) => {
  const {
    allowFailure = false,
    captureOnly = false,
    cwd = repositoryRoot,
    env = process.env,
    ignoreAbort = false,
    timeoutMs = 120_000,
  } = options;
  const commandLabel = `${command} ${args.join(' ')}`;
  if (!captureOnly) {
    appendLog(`\n$ ${commandLabel}\n`);
  }

  return await new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd,
      env,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    let settled = false;
    let terminationReason = null;
    let forceKillTimer = null;
    const stdoutStream = createRedactedStream(process.stdout);
    const stderrStream = createRedactedStream(process.stderr);

    const terminate = (reason) => {
      if (terminationReason) {
        return;
      }
      terminationReason = reason;
      child.kill('SIGTERM');
      forceKillTimer = setTimeout(() => child.kill('SIGKILL'), 5_000);
      forceKillTimer.unref();
    };

    const timeout = setTimeout(
      () => terminate(`${commandLabel} exceeded ${timeoutMs}ms`),
      timeoutMs,
    );
    timeout.unref();
    const onAbort = () => terminate(`Aborted ${commandLabel} after ${receivedSignal ?? 'cancellation'}`);
    if (!ignoreAbort) {
      if (processAbortController.signal.aborted) {
        onAbort();
      } else {
        processAbortController.signal.addEventListener('abort', onAbort, { once: true });
      }
    }

    const finishStreams = () => {
      if (!captureOnly) {
        stdoutStream.flush();
        stderrStream.flush();
      }
    };

    const cleanListeners = () => {
      clearTimeout(timeout);
      if (forceKillTimer) {
        clearTimeout(forceKillTimer);
      }
      processAbortController.signal.removeEventListener('abort', onAbort);
    };

    child.stdout.on('data', (chunk) => {
      const text = chunk.toString();
      stdout += text;
      if (!captureOnly) {
        stdoutStream.push(text);
      }
    });
    child.stderr.on('data', (chunk) => {
      const text = chunk.toString();
      stderr += text;
      if (!captureOnly) {
        stderrStream.push(text);
      }
    });
    child.on('error', (error) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanListeners();
      finishStreams();
      reject(error);
    });
    child.on('close', (code) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanListeners();
      finishStreams();
      if (terminationReason) {
        reject(new Error(terminationReason));
        return;
      }
      const result = { code: code ?? -1, stderr, stdout };
      if (result.code !== 0 && !allowFailure) {
        reject(new Error(`${commandLabel} exited with code ${result.code}`));
      } else {
        resolve(result);
      }
    });
  });
};

const composeEnvironment = {
  ...process.env,
  COMPOSE_PROJECT_NAME: projectName,
  PROMPTLY_E2E_DATABASE_PASSWORD: databasePassword,
  PROMPTLY_E2E_JWT_KEY: jwtKey,
  PROMPTLY_E2E_WORKER_IMAGE: workerImage,
};
const composeArguments = ['compose', '--file', composeFile, '--project-name', projectName];
const errors = [];
let stackStarted = false;
let apiOrigin = null;
let apiPort = null;
let webOrigin = null;
let webPort = null;

const configureCandidatePorts = () => {
  apiPort = randomInt(49_152, 65_536);
  do {
    webPort = randomInt(49_152, 65_536);
  } while (webPort === apiPort);
  composeEnvironment.PROMPTLY_E2E_API_PORT = String(apiPort);
  composeEnvironment.PROMPTLY_E2E_WEB_PORT = String(webPort);
};

const recordError = (label, error) => {
  const message = error instanceof Error ? error.message : String(error);
  errors.push(`${label}: ${redact(message)}`);
};

const waitForHttp = async (url, attempts = 60) => {
  let lastError = 'no response';
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(3_000) });
      if (response.ok) {
        return;
      }
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }
    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
  throw new Error(`${url} did not become ready: ${lastError}`);
};

const resolvePublishedPort = async (service, target) => {
  const binding = await run(
    'docker',
    [...composeArguments, 'port', service, String(target)],
    { captureOnly: true, env: composeEnvironment },
  );
  const bindings = binding.stdout.split('\n').map((line) => line.trim()).filter(Boolean);
  if (bindings.length !== 1) {
    throw new Error(`${service} must have exactly one published ${target}/tcp binding`);
  }
  const match = /^127\.0\.0\.1:(\d+)$/.exec(bindings[0]);
  const port = Number(match?.[1]);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`${service} runtime port is not loopback-only`);
  }
  return port;
};

const writeTopologyAttestation = async () => {
  const configured = await run(
    'docker',
    [...composeArguments, 'config', '--format', 'json'],
    { captureOnly: true, env: composeEnvironment },
  );
  const topology = JSON.parse(configured.stdout);
  const services = topology.services ?? {};
  const expectedServiceNames = [
    'postgres',
    'promptly-egress-proxy',
    'promptly-eval',
    'promptly-server',
    'promptly-web',
    'provider-stub',
  ];
  const actualServiceNames = Object.keys(services).sort();
  if (JSON.stringify(actualServiceNames) !== JSON.stringify(expectedServiceNames)) {
    throw new Error(`Unexpected Compose service inventory: ${actualServiceNames.join(',')}`);
  }
  const publicServices = new Map([
    ['promptly-server', { port: apiPort, target: 5000 }],
    ['promptly-web', { port: webPort, target: 8080 }],
  ]);
  for (const [name, service] of Object.entries(services)) {
    if (service.container_name != null) {
      throw new Error(`${name} must not use a fixed container_name`);
    }
    const ports = service.ports ?? [];
    const expected = publicServices.get(name);
    if (!expected && ports.length > 0) {
      throw new Error(`${name} unexpectedly publishes a host port`);
    }
    if (expected) {
      if (ports.length !== 1) {
        throw new Error(`${name} must publish exactly one port`);
      }
      const [published] = ports;
      if (
        published.host_ip !== '127.0.0.1'
        || Number(published.published) !== expected.port
        || published.mode !== 'ingress'
        || published.protocol !== 'tcp'
        || Number(published.target) !== expected.target
      ) {
        throw new Error(`${name} has an unexpected port binding`);
      }
    }
  }
  const expectedServiceNetworks = {
    postgres: ['promptly-control'],
    'promptly-egress-proxy': ['promptly-control', 'promptly-egress'],
    'promptly-eval': ['promptly-control'],
    'promptly-server': ['promptly-control', 'promptly-ingress'],
    'promptly-web': ['promptly-control', 'promptly-ingress'],
    'provider-stub': ['promptly-control'],
  };
  for (const [serviceName, expectedNetworksForService] of Object.entries(expectedServiceNetworks)) {
    const actualNetworksForService = Object.keys(services[serviceName].networks ?? {}).sort();
    if (JSON.stringify(actualNetworksForService) !== JSON.stringify(expectedNetworksForService)) {
      throw new Error(`${serviceName} has unexpected network membership`);
    }
  }
  if (topology.networks?.['promptly-control']?.internal !== true) {
    throw new Error('promptly-control must be an internal network');
  }
  const expectedNetworks = ['promptly-control', 'promptly-egress', 'promptly-ingress'];
  if (JSON.stringify(Object.keys(topology.networks ?? {}).sort()) !== JSON.stringify(expectedNetworks)) {
    throw new Error('Compose network inventory drifted');
  }
  for (const networkName of expectedNetworks) {
    const network = topology.networks[networkName];
    if (network.external === true || network.name !== `${projectName}_${networkName}`) {
      throw new Error(`${networkName} must be project-scoped and non-external`);
    }
  }
  const ingressNetwork = topology.networks['promptly-ingress'];
  if (
    ingressNetwork.driver !== 'bridge'
    || ingressNetwork.internal === true
    || ingressNetwork.enable_ipv6 !== false
    || ingressNetwork.driver_opts?.['com.docker.network.bridge.enable_ip_masquerade'] !== 'false'
  ) {
    throw new Error(
      'promptly-ingress must be an IPv4-only non-masqueraded bridge used only for host ingress',
    );
  }
  const expectedVolumes = ['dataprotection-keys', 'postgres-data'];
  if (JSON.stringify(Object.keys(topology.volumes ?? {}).sort()) !== JSON.stringify(expectedVolumes)) {
    throw new Error('Compose volume inventory drifted');
  }
  for (const volumeName of expectedVolumes) {
    const volume = topology.volumes[volumeName];
    if (volume.external === true || volume.name !== `${projectName}_${volumeName}`) {
      throw new Error(`${volumeName} must be project-scoped and non-external`);
    }
  }
  const stateMounts = {
    postgres: ['volume', 'postgres-data', '/var/lib/postgresql', false],
    'promptly-server': ['volume', 'dataprotection-keys', '/app/dataprotection-keys', false],
    'provider-stub': [
      'bind',
      path.join(repositoryRoot, 'Promptly.Worker/scripts/provider_stub.py'),
      '/provider_stub.py',
      true,
    ],
  };
  for (const [serviceName, expectedMount] of Object.entries(stateMounts)) {
    const mounts = services[serviceName]?.volumes ?? [];
    if (
      mounts.length !== 1
      || mounts[0].type !== expectedMount[0]
      || mounts[0].source !== expectedMount[1]
      || mounts[0].target !== expectedMount[2]
      || Boolean(mounts[0].read_only) !== expectedMount[3]
    ) {
      throw new Error(`${serviceName} has unexpected mounts`);
    }
  }
  for (const serviceName of ['promptly-egress-proxy', 'promptly-eval', 'promptly-web']) {
    if ((services[serviceName]?.volumes ?? []).length !== 0) {
      throw new Error(`${serviceName} must not have persistent or host mounts`);
    }
  }
  const egressMembers = Object.entries(services)
    .filter(([, service]) => Object.hasOwn(service.networks ?? {}, 'promptly-egress'))
    .map(([name]) => name);
  if (JSON.stringify(egressMembers) !== JSON.stringify(['promptly-egress-proxy'])) {
    throw new Error(`Unexpected egress-network membership: ${egressMembers.join(',')}`);
  }
  if (services['promptly-server']?.environment?.ASPNETCORE_ENVIRONMENT !== 'Production') {
    throw new Error('E2E server must run in Production');
  }
  if (services['promptly-server']?.environment?.JWT__ExpiryMinutes !== '1') {
    throw new Error('E2E JWT lifetime must remain one minute');
  }
  if (Buffer.from(jwtKey, 'base64').length !== 64 || databasePassword.length < 40) {
    throw new Error('Generated E2E secrets do not meet the entropy-size contract');
  }

  await writeFile(path.join(artifactsRoot, 'topology-attestation.json'), `${JSON.stringify({
    schema: 1,
    checks: {
      exactServiceInventory: true,
      noFixedContainerNames: true,
      disposableNamedVolumes: true,
      ephemeralDatabaseSecret: true,
      ephemeralJwtSigningKey: true,
      internalControlNetwork: true,
      ingressIpv6Disabled: true,
      nonMasqueradedIngressNetwork: true,
      onlyProxyHasEgressNetwork: true,
      productionServerEnvironment: true,
      shortJwtLifetime: true,
      stateMountsAreProjectScoped: true,
    },
    publishedPorts: {
      api: { host: '127.0.0.1', port: apiPort },
      web: { host: '127.0.0.1', port: webPort },
    },
    privateServices: ['postgres', 'provider-stub', 'promptly-egress-proxy', 'promptly-eval'],
  }, null, 2)}\n`);
};

const writeIngressEgressAttestation = async () => {
  const blockedMarker = 'PROMPTLY_EGRESS_BLOCKED';
  const routedCanary = await run(
    'docker',
    [
      ...composeArguments,
      'exec',
      '--no-TTY',
      'promptly-web',
      'sh',
      '-c',
      `response="$(printf 'CONNECT 1.1.1.1:443 HTTP/1.1\\r\\nHost: 1.1.1.1:443\\r\\n\\r\\n' `
        + `| nc -w 8 promptly-egress-proxy 4750 | head -n 1)"; `
        + `case "$response" in 'HTTP/1.'*' 200 '*) exit 0 ;; `
        + `*) printf '%s\\n' "$response" >&2; exit 1 ;; esac`,
    ],
    {
      allowFailure: true,
      captureOnly: true,
      env: composeEnvironment,
      timeoutMs: 15_000,
    },
  );
  const probes = [
    {
      service: 'promptly-server',
      controlCommand: [
        'bash',
        '-c',
        'exec 3<>/dev/tcp/promptly-web/8080',
      ],
      blockedCommand: [
        'bash',
        '-c',
        `set -e; status=0; timeout 3 bash -c 'exec 3<>/dev/tcp/1.1.1.1/443' `
          + `>/dev/null 2>&1 || status=$?; [ "$status" -eq 124 ]; printf '%s\\n' ${blockedMarker}; exit 86`,
      ],
    },
    {
      service: 'promptly-web',
      controlCommand: ['nc', '-w', '2', 'promptly-server', '5000'],
      blockedCommand: [
        'sh',
        '-c',
        `set -e; status=0; timeout 3 nc 1.1.1.1 443 </dev/null >/dev/null 2>&1 `
          + `|| status=$?; [ "$status" -eq 143 ]; printf '%s\\n' ${blockedMarker}; exit 86`,
      ],
    },
  ];
  const results = [];
  for (const probe of probes) {
    const control = await run(
      'docker',
      [...composeArguments, 'exec', '--no-TTY', probe.service, ...probe.controlCommand],
      {
        allowFailure: true,
        captureOnly: true,
        env: composeEnvironment,
        timeoutMs: 10_000,
      },
    );
    const blocked = await run(
      'docker',
      [...composeArguments, 'exec', '--no-TTY', probe.service, ...probe.blockedCommand],
      {
        allowFailure: true,
        captureOnly: true,
        env: composeEnvironment,
        timeoutMs: 10_000,
      },
    );
    assertBlockedEgressProbe({
      blocked,
      canary: routedCanary,
      control,
      marker: blockedMarker,
      service: probe.service,
    });
    results.push({
      service: probe.service,
      controlNetworkConnectionSucceeded: true,
      directInternetConnectionRejected: true,
      rejectionContract: 'bounded-timeout',
    });
  }
  await writeFile(path.join(artifactsRoot, 'ingress-egress-attestation.json'), `${JSON.stringify({
    schema: 1,
    network: 'promptly-ingress',
    ipMasqueradeDisabled: true,
    ipv6Disabled: true,
    routedCanary: {
      target: '1.1.1.1:443',
      viaEgressProxySucceeded: true,
    },
    results,
  }, null, 2)}\n`);
};

const captureProviderEvidence = async (required) => {
  const destination = path.join(artifactsRoot, 'provider-requests.jsonl');
  await rm(destination, { force: true });
  const captured = await run(
    'docker',
    [
      ...composeArguments,
      'exec',
      '--no-TTY',
      'provider-stub',
      'cat',
      '/tmp/provider-requests.jsonl',
    ],
    {
      allowFailure: true,
      captureOnly: true,
      env: composeEnvironment,
      timeoutMs: 10_000,
    },
  );
  if (required && captured.code !== 0) {
    throw new Error('Provider evidence could not be captured');
  }
  if (!required && captured.code !== 0) {
    await writeFile(destination, '{"status":"unavailable-before-provider-start"}\n');
    return;
  }
  await writeFile(destination, redact(captured.stdout));
};

const verifyProviderEvidence = async () => {
  const evidencePath = path.join(artifactsRoot, 'provider-requests.jsonl');
  const lines = (await readFile(evidencePath, 'utf8'))
    .split('\n')
    .filter((line) => line.trim().length > 0);
  if (lines.length === 0) {
    throw new Error('Provider evidence is empty');
  }
  const records = lines.map((line) => JSON.parse(line));
  const sequences = new Set();
  for (const record of records) {
    const keys = Object.keys(record).sort();
    const expectedKeys = ['authorized', 'kind', 'method', 'path', 'sequence'];
    if (JSON.stringify(keys) !== JSON.stringify(expectedKeys)) {
      throw new Error(`Provider evidence has an unexpected schema: ${keys.join(',')}`);
    }
    if (
      record.authorized !== true
      || record.kind !== 'models'
      || record.method !== 'GET'
      || record.path !== '/v1/models'
      || !Number.isInteger(record.sequence)
      || record.sequence < 1
      || sequences.has(record.sequence)
    ) {
      throw new Error('Provider evidence contains an unexpected request');
    }
    sequences.add(record.sequence);
  }
};

const collectDiagnostics = async () => {
  const diagnostics = [
    ['compose-ps.json', [...composeArguments, 'ps', '--all', '--format', 'json']],
    ['compose-images.json', [...composeArguments, 'images', '--format', 'json']],
    ['compose.log', [...composeArguments, 'logs', '--no-color', '--timestamps']],
  ];
  const failures = [];
  for (const [fileName, args] of diagnostics) {
    const result = await run('docker', args, {
      allowFailure: true,
      captureOnly: true,
      env: composeEnvironment,
      ignoreAbort: true,
      timeoutMs: 30_000,
    });
    if (result.code !== 0) {
      failures.push(`${fileName} command exited with code ${result.code}`);
    }
    const rawContent = result.stdout || result.stderr;
    if (stackStarted && rawContent.trim().length === 0) {
      failures.push(`${fileName} was empty after the Compose stack started`);
    }
    const content = redact(
      rawContent || (fileName.endsWith('.json') ? '[]\n' : 'No Compose output was available.\n'),
    );
    await writeFile(path.join(artifactsRoot, fileName), content);
  }
  if (failures.length > 0) {
    throw new Error(failures.join('; '));
  }
};

const findArtifactFiles = async (directory) => {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...await findArtifactFiles(entryPath));
    } else if (entry.isFile()) {
      files.push(entryPath);
    }
  }
  return files;
};

const sanitizeArtifacts = async () => {
  const sanitized = [];
  const textPattern = /\.(?:css|html|js|json|jsonl|log|md|txt|xml)$/;
  for (const artifactPath of await findArtifactFiles(artifactsRoot)) {
    const content = await readFile(artifactPath);
    const relative = path.relative(artifactsRoot, artifactPath);
    if (relative === path.join('html', 'index.html')) {
      const result = artifactSanitizer.sanitizeHtmlReport(content);
      if (result.changed) {
        const temporaryPath = `${artifactPath}.sanitized-${process.pid}`;
        await writeFile(temporaryPath, result.content, { mode: 0o600 });
        await rename(temporaryPath, artifactPath);
        sanitized.push(`scrubbed-html-archive:${relative}`);
      }
      continue;
    }
    if (artifactPath.toLowerCase().endsWith('.zip')) {
      const result = artifactSanitizer.sanitizeArchive(content);
      if (result.changed) {
        const temporaryPath = `${artifactPath}.sanitized-${process.pid}`;
        await writeFile(temporaryPath, result.content, { mode: 0o600 });
        await rename(temporaryPath, artifactPath);
        sanitized.push(`scrubbed-archive:${relative}`);
      }
      continue;
    }
    const result = artifactSanitizer.sanitizeBuffer(content);
    if (!result.changed) {
      continue;
    }
    if (textPattern.test(artifactPath)) {
      const temporaryPath = `${artifactPath}.sanitized-${process.pid}`;
      await writeFile(temporaryPath, result.content, { mode: 0o600 });
      await rename(temporaryPath, artifactPath);
      sanitized.push(`scrubbed:${relative}`);
    } else {
      await rm(artifactPath);
      sanitized.push(`removed:${relative}`);
    }
  }
  return sanitized;
};

const assertArtifactsContainNoSecrets = async () => {
  for (const artifactPath of await findArtifactFiles(artifactsRoot)) {
    const content = await readFile(artifactPath);
    const relative = path.relative(artifactsRoot, artifactPath);
    try {
      if (relative === path.join('html', 'index.html')) {
        artifactSanitizer.assertHtmlReportSafe(content);
      } else if (artifactPath.toLowerCase().endsWith('.zip')) {
        artifactSanitizer.assertArchiveSafe(content);
      } else {
        artifactSanitizer.assertBufferSafe(content);
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      throw new Error(`Artifact sanitization failed for ${relative}: ${message}`);
    }
  }
};

const requireNonemptyArtifact = async (relativePath) => {
  const information = await stat(path.join(artifactsRoot, relativePath));
  if (!information.isFile() || information.size === 0) {
    throw new Error(`Required artifact is empty: ${relativePath}`);
  }
};

const verifyEvidenceArtifacts = async (browserAttempted, browserFailed) => {
  const required = ['cleanup-attestation.json', 'runner.log'];
  if (stackStarted) {
    required.push('compose-images.json', 'compose-ps.json', 'compose.log');
  }
  if (browserAttempted) {
    required.push(
      'html/index.html',
      'ingress-egress-attestation.json',
      'junit.xml',
      'provider-requests.jsonl',
      'results.json',
      'topology-attestation.json',
      'verification.json',
    );
  }
  for (const relativePath of required) {
    await requireNonemptyArtifact(relativePath);
  }

  if (browserFailed) {
    const report = JSON.parse(await readFile(path.join(artifactsRoot, 'results.json'), 'utf8'));
    const serialized = JSON.stringify(report.suites ?? []);
    const hasExecutedFailure = /"status":"(?:failed|timedOut|interrupted)"/.test(serialized);
    if (hasExecutedFailure) {
      const files = await findArtifactFiles(path.join(artifactsRoot, 'playwright-output'));
      for (const extension of ['.png', '.webm', '.zip']) {
        const matching = files.filter((file) => file.endsWith(extension));
        if (matching.length === 0) {
          throw new Error(`Failed browser execution did not retain a ${extension} artifact`);
        }
        for (const artifactPath of matching) {
          const information = await stat(artifactPath);
          if (information.size === 0) {
            throw new Error(`Failure artifact is empty: ${artifactPath}`);
          }
        }
      }
    }
  }

};

const writeArtifactManifest = async () => {
  const manifest = [];
  for (const artifactPath of await findArtifactFiles(artifactsRoot)) {
    if (artifactPath.endsWith(`${path.sep}upload-safe.json`)) {
      continue;
    }
    const information = await stat(artifactPath);
    manifest.push({
      path: path.relative(artifactsRoot, artifactPath).split(path.sep).join('/'),
      size: information.size,
    });
  }
  manifest.sort((left, right) => left.path.localeCompare(right.path));
  await writeFile(path.join(artifactsRoot, 'artifact-manifest.json'), `${JSON.stringify({
    schema: 1,
    artifacts: manifest,
  }, null, 2)}\n`);
};

const writeRunMetadata = async () => {
  await writeFile(path.join(artifactsRoot, 'run-metadata.json'), `${JSON.stringify({
    schema: 1,
    startedAt: startedAt.toISOString(),
    completedAt: new Date().toISOString(),
    status: errors.length === 0 ? 'passed' : 'failed',
    webOrigin,
    apiOrigin,
    errors,
  }, null, 2)}\n`);
};

let browserAttempted = false;
let playwrightCode = null;

try {
  await run('docker', ['version', '--format', '{{.Server.Version}}'], { timeoutMs: 30_000 });
  await run('docker', ['compose', 'version'], { timeoutMs: 30_000 });
  configureCandidatePorts();
  await run('docker', [...composeArguments, 'config', '--quiet'], {
    env: composeEnvironment,
    timeoutMs: 30_000,
  });
  await run('docker', [...composeArguments, 'build', '--force-rm'], {
    env: composeEnvironment,
    timeoutMs: 900_000,
  });
  let composeStarted = false;
  for (let attempt = 1; attempt <= 5; attempt += 1) {
    const up = await run(
      'docker',
      [...composeArguments, 'up', '--detach', '--wait', '--wait-timeout', '180'],
      {
        allowFailure: true,
        env: composeEnvironment,
        timeoutMs: 240_000,
      },
    );
    if (up.code === 0) {
      composeStarted = true;
      break;
    }
    const output = `${up.stdout}\n${up.stderr}`;
    const portCollision = /address already in use|bind:.*failed|port is already allocated/i.test(output);
    if (!portCollision || attempt === 5) {
      throw new Error(`docker compose up exited with code ${up.code}`);
    }
    const reset = await run(
      'docker',
      [...composeArguments, 'down', '--volumes', '--remove-orphans', '--timeout', '10'],
      {
        allowFailure: true,
        env: composeEnvironment,
        timeoutMs: 60_000,
      },
    );
    if (reset.code !== 0) {
      throw new Error('Could not reset the Compose project after a host-port collision');
    }
    configureCandidatePorts();
    await run('docker', [...composeArguments, 'config', '--quiet'], {
      env: composeEnvironment,
      timeoutMs: 30_000,
    });
  }
  if (!composeStarted) {
    throw new Error('Compose did not start after bounded host-port retries');
  }
  stackStarted = true;
  const [resolvedApiPort, resolvedWebPort] = await Promise.all([
    resolvePublishedPort('promptly-server', 5000),
    resolvePublishedPort('promptly-web', 8080),
  ]);
  if (resolvedApiPort !== apiPort || resolvedWebPort !== webPort) {
    throw new Error('Resolved Compose ports do not match the selected loopback bindings');
  }
  if (resolvedApiPort === resolvedWebPort) {
    throw new Error('API and web services unexpectedly share a published port');
  }
  apiOrigin = `http://127.0.0.1:${apiPort}`;
  webOrigin = `http://127.0.0.1:${webPort}`;
  await Promise.all([
    waitForHttp(`${apiOrigin}/health`),
    waitForHttp(`${webOrigin}/healthz`),
  ]);
  await writeTopologyAttestation();
  await writeIngressEgressAttestation();

  const playwrightEnvironment = {
    ...process.env,
    CI: process.env.CI ?? 'true',
    PROMPTLY_E2E_API_ORIGIN: apiOrigin,
    PROMPTLY_E2E_ARTIFACT_DIR: artifactsRoot,
    PROMPTLY_E2E_JWT_KEY: jwtKey,
    PROMPTLY_E2E_WEB_ORIGIN: webOrigin,
  };

  browserAttempted = true;
  playwrightCode = -1;
  const playwright = await run(
    path.join(webRoot, 'node_modules/.bin/playwright'),
    ['test', '--config', 'playwright.config.ts'],
    {
      allowFailure: true,
      cwd: webRoot,
      env: playwrightEnvironment,
      timeoutMs: 240_000,
    },
  );
  playwrightCode = playwright.code;
  const verification = await run(
    process.execPath,
    [path.join(e2eRoot, 'verify-results.mjs')],
    {
      allowFailure: true,
      cwd: webRoot,
      env: playwrightEnvironment,
      timeoutMs: 30_000,
    },
  );
  const browserErrors = [];
  try {
    await captureProviderEvidence(true);
    await verifyProviderEvidence();
  } catch (error) {
    browserErrors.push(error instanceof Error ? error.message : String(error));
  }
  if (playwright.code !== 0) {
    browserErrors.push(`Playwright exited with code ${playwright.code}`);
  }
  if (verification.code !== 0) {
    browserErrors.push(`Required-test verification exited with code ${verification.code}`);
  }
  if (browserErrors.length > 0) {
    throw new Error(browserErrors.join('; '));
  }
} catch (error) {
  recordError('verification', error);
} finally {
  try {
    await collectDiagnostics();
  } catch (error) {
    recordError('diagnostics', error);
  }
  if (stackStarted) {
    try {
      const providerPath = path.join(artifactsRoot, 'provider-requests.jsonl');
      try {
        await stat(providerPath);
      } catch {
        await captureProviderEvidence(false);
      }
    } catch (error) {
      recordError('provider diagnostics', error);
    }
  }

  let cleanupCommandPassed = false;
  let cleanupVerificationPassed = true;
  try {
    const cleanup = await run(
      'docker',
      [...composeArguments, 'down', '--volumes', '--remove-orphans', '--rmi', 'local', '--timeout', '20'],
      {
        allowFailure: true,
        env: composeEnvironment,
        ignoreAbort: true,
        timeoutMs: 60_000,
      },
    );
    cleanupCommandPassed = cleanup.code === 0;
    if (!cleanupCommandPassed) {
      cleanupVerificationPassed = false;
      recordError('cleanup', new Error(`docker compose down exited with code ${cleanup.code}`));
    }
  } catch (error) {
    cleanupVerificationPassed = false;
    recordError('cleanup', error);
  }

  for (const image of projectImages) {
    try {
      const inspection = await run('docker', ['image', 'inspect', image], {
        allowFailure: true,
        captureOnly: true,
        ignoreAbort: true,
        timeoutMs: 30_000,
      });
      if (inspection.code === 0) {
        const removal = await run('docker', ['image', 'rm', image], {
          allowFailure: true,
          captureOnly: true,
          ignoreAbort: true,
          timeoutMs: 30_000,
        });
        if (removal.code !== 0) {
          cleanupVerificationPassed = false;
          recordError('cleanup', new Error(`Per-run image could not be removed: ${image}`));
        }
      } else if (!/No such (?:image|object)/i.test(inspection.stderr)) {
        cleanupVerificationPassed = false;
        recordError('cleanup', new Error(`Could not inspect per-run image: ${image}`));
      }
    } catch (error) {
      cleanupVerificationPassed = false;
      recordError('cleanup', error);
    }
  }

  const leftovers = { containers: [], images: [], networks: [], volumes: [] };
  const resourceQueries = [
    ['containers', ['ps', '--all', '--quiet', '--filter', `label=com.docker.compose.project=${projectName}`]],
    ['networks', ['network', 'ls', '--quiet', '--filter', `label=com.docker.compose.project=${projectName}`]],
    ['volumes', ['volume', 'ls', '--quiet', '--filter', `label=com.docker.compose.project=${projectName}`]],
  ];
  for (const [resource, args] of resourceQueries) {
    try {
      const result = await run('docker', args, {
        allowFailure: true,
        captureOnly: true,
        ignoreAbort: true,
        timeoutMs: 30_000,
      });
      if (result.code !== 0) {
        cleanupVerificationPassed = false;
        recordError('cleanup', new Error(`Could not verify ${resource} cleanup`));
      } else {
        leftovers[resource] = result.stdout.trim().split('\n').filter(Boolean);
      }
    } catch (error) {
      cleanupVerificationPassed = false;
      recordError('cleanup', error);
    }
  }
  for (const image of projectImages) {
    try {
      const result = await run('docker', ['image', 'inspect', image], {
        allowFailure: true,
        captureOnly: true,
        ignoreAbort: true,
        timeoutMs: 30_000,
      });
      if (result.code === 0) {
        leftovers.images.push(image);
      } else if (!/No such (?:image|object)/i.test(result.stderr)) {
        cleanupVerificationPassed = false;
        recordError('cleanup', new Error(`Could not verify image cleanup: ${image}`));
      }
    } catch (error) {
      cleanupVerificationPassed = false;
      recordError('cleanup', error);
    }
  }
  if (Object.values(leftovers).some((items) => items.length > 0)) {
    cleanupVerificationPassed = false;
    recordError('cleanup', new Error(`Compose resources remain: ${JSON.stringify(leftovers)}`));
  }
  try {
    await writeFile(path.join(artifactsRoot, 'cleanup-attestation.json'), `${JSON.stringify({
      schema: 1,
      cleanupCommandPassed,
      cleanupVerificationPassed,
      leftovers,
    }, null, 2)}\n`);
  } catch (error) {
    recordError('cleanup evidence', error);
  }
}

if (receivedSignal) {
  recordError('termination', new Error(`Run received ${receivedSignal}`));
}
if (logWriteError) {
  recordError('runner log', logWriteError);
}

try {
  const sanitized = await sanitizeArtifacts();
  if (sanitized.some((entry) => entry.startsWith('removed:'))) {
    recordError(
      'artifact sanitization',
      new Error(`Removed unsafe binary material from: ${sanitized.join(',')}`),
    );
  }
} catch (error) {
  recordError('artifact sanitization', error);
}

try {
  await verifyEvidenceArtifacts(browserAttempted, playwrightCode !== null && playwrightCode !== 0);
} catch (error) {
  recordError('artifact verification', error);
}

let uploadIsSafe = false;
try {
  await writeRunMetadata();
  const sanitized = await sanitizeArtifacts();
  if (sanitized.some((entry) => entry.startsWith('removed:'))) {
    recordError(
      'artifact sanitization',
      new Error(`Removed unsafe binary material from: ${sanitized.join(',')}`),
    );
    await writeRunMetadata();
  }
  await assertArtifactsContainNoSecrets();
  await writeArtifactManifest();
  await assertArtifactsContainNoSecrets();
  await writeFile(path.join(artifactsRoot, 'upload-safe.json'), '{"schema":1,"safe":true}\n');
  uploadIsSafe = true;
} catch (error) {
  recordError('artifact safety', error);
  try {
    await sanitizeArtifacts();
    await writeRunMetadata();
  } catch {
    // If safe finalization itself fails, leave no upload marker.
  }
}

if (!uploadIsSafe) {
  throw new Error(`Composed E2E artifacts could not be made safe for upload:\n${errors.join('\n')}`);
}
if (errors.length > 0) {
  throw new Error(`Composed E2E verification failed:\n${errors.join('\n')}`);
}

console.log(`Composed E2E verification passed; artifacts: ${artifactsRoot}`);
