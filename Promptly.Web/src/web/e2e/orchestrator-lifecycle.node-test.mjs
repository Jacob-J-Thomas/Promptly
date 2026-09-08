import assert from 'node:assert/strict';
import { EventEmitter, once } from 'node:events';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createPhaseProcessRunner } from './orchestrator-lifecycle.mjs';

const createChild = () => {
  const child = new EventEmitter();
  child.killSignals = [];
  child.kill = (signal) => {
    child.killSignals.push(signal);
    return true;
  };
  return child;
};

for (const signal of ['SIGINT', 'SIGTERM']) {
  test(`forwards ${signal} exactly once and waits for child close`, async () => {
    const signalSource = new EventEmitter();
    const child = createChild();
    let spawnCount = 0;
    const runner = createPhaseProcessRunner({
      baseEnvironment: {},
      nodePath: '/usr/bin/node',
      phaseRunner: '/tmp/run-composed.mjs',
      repositoryRoot: '/tmp/repository',
      signalSource,
      spawnProcess: () => {
        spawnCount += 1;
        return child;
      },
    });

    let settled = false;
    const phase = runner.runPhase('proxy').then((result) => {
      settled = true;
      return result;
    });
    signalSource.emit(signal);
    signalSource.emit(signal);
    signalSource.emit(signal === 'SIGINT' ? 'SIGTERM' : 'SIGINT');
    await Promise.resolve();
    assert.equal(settled, false, 'the parent must wait for child cleanup/close');
    assert.deepEqual(child.killSignals, [signal]);

    child.emit('close', signal === 'SIGINT' ? 130 : 143);
    const result = await phase;
    assert.equal(spawnCount, 1);
    assert.equal(result.cancelled, true);
    assert.equal(result.signal, signal);
    assert.equal(result.code, signal === 'SIGINT' ? 130 : 143);
    runner.dispose();
  });
}

test('does not start the next phase after cancellation', async () => {
  const signalSource = new EventEmitter();
  const children = [];
  const runner = createPhaseProcessRunner({
    baseEnvironment: {},
    nodePath: '/usr/bin/node',
    phaseRunner: '/tmp/run-composed.mjs',
    repositoryRoot: '/tmp/repository',
    signalSource,
    spawnProcess: () => {
      const child = createChild();
      children.push(child);
      return child;
    },
  });

  const firstPhase = runner.runPhase('proxy');
  signalSource.emit('SIGTERM');
  children[0].emit('close', 143);
  const firstResult = await firstPhase;
  assert.equal(firstResult.cancelled, true);

  const nextResult = await runner.runPhase('direct');
  assert.equal(nextResult.started, false);
  assert.equal(nextResult.cancelled, true);
  assert.equal(children.length, 1);
  runner.dispose();
});

test('hands one runner-generated correlation to direct Playwright only', async () => {
  const signalSource = new EventEmitter();
  const environments = [];
  const runner = createPhaseProcessRunner({
    baseEnvironment: { PROMPTLY_E2E_EXPECTED_CORRELATION: 'stale-value' },
    nodePath: '/usr/bin/node',
    phaseRunner: '/tmp/run-composed.mjs',
    repositoryRoot: '/tmp/repository',
    signalSource,
    spawnProcess: (node, args, options) => {
      assert.equal(node, '/usr/bin/node');
      assert.deepEqual(args, ['/tmp/run-composed.mjs']);
      environments.push(options.env);
      const child = createChild();
      queueMicrotask(() => child.emit('close', 0));
      return child;
    },
  });

  await runner.runPhase('direct');
  await runner.runPhase('proxy');
  assert.match(
    environments[0].PROMPTLY_E2E_EXPECTED_CORRELATION,
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
  );
  assert.equal(environments[0].PROMPTLY_E2E_PHASE, 'direct');
  assert.equal(Object.hasOwn(environments[1], 'PROMPTLY_E2E_EXPECTED_CORRELATION'), false);
  assert.equal(environments[1].PROMPTLY_E2E_PHASE, 'proxy');
  runner.dispose();
});

test('reports a spawn failure without inventing a cancellation or success', async () => {
  const runner = createPhaseProcessRunner({
    baseEnvironment: {},
    nodePath: '/usr/bin/node',
    phaseRunner: '/tmp/run-composed.mjs',
    repositoryRoot: '/tmp/repository',
    signalSource: new EventEmitter(),
    spawnProcess: () => {
      throw new Error('spawn failed');
    },
  });

  const result = await runner.runPhase('proxy');
  assert.equal(result.started, false);
  assert.equal(result.cancelled, false);
  assert.equal(result.code, -1);
  assert.equal(result.error, 'spawn failed');
  runner.dispose();
});

test('waits for child close after a child error', async () => {
  const signalSource = new EventEmitter();
  const child = createChild();
  const runner = createPhaseProcessRunner({
    baseEnvironment: {},
    nodePath: '/usr/bin/node',
    phaseRunner: '/tmp/run-composed.mjs',
    repositoryRoot: '/tmp/repository',
    signalSource,
    spawnProcess: () => child,
  });

  let settled = false;
  const phase = runner.runPhase('proxy').then((result) => {
    settled = true;
    return result;
  });
  child.emit('error', new Error('child failed'));
  await Promise.resolve();
  assert.equal(settled, false, 'the parent must wait for close after child error');

  child.emit('close', -1);
  const result = await phase;
  assert.equal(result.code, -1);
  assert.equal(result.error, 'child failed');
  assert.equal(result.cancelled, false);
  runner.dispose();
});

const processIsAlive = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};

const waitForFile = async (filePath) => {
  const deadline = Date.now() + 2_000;
  while (Date.now() < deadline) {
    try {
      await readFile(filePath, 'utf8');
      return;
    } catch {
      await delay(10);
    }
  }
  throw new Error(`Timed out waiting for ${filePath}`);
};

for (const [signal, exitCode, otherSignal] of [
  ['SIGINT', 130, 'SIGTERM'],
  ['SIGTERM', 143, 'SIGINT'],
]) {
  test(`real child survives repeated ${signal} while cleanup completes`, async () => {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'promptly-phase-runner-'));
    const readyFile = path.join(directory, 'ready');
    const cleanupFile = path.join(directory, 'cleanup');
    const childScript = path.join(directory, 'child.mjs');
    await writeFile(childScript, `
import { writeFileSync } from 'node:fs';

let handled = false;
const stop = (code) => {
  if (handled) return;
  handled = true;
  writeFileSync(process.env.PROMPTLY_TEST_CLEANUP_FILE, 'started');
  setTimeout(() => {
    writeFileSync(process.env.PROMPTLY_TEST_CLEANUP_FILE, 'finished');
    process.exit(code);
  }, 80);
};
process.on('SIGINT', () => stop(130));
process.on('SIGTERM', () => stop(143));
writeFileSync(process.env.PROMPTLY_TEST_READY_FILE, 'ready');
setInterval(() => {}, 1_000);
`);

    const signalSource = new EventEmitter();
    let child = null;
    const runner = createPhaseProcessRunner({
      baseEnvironment: {
        PROMPTLY_TEST_READY_FILE: readyFile,
        PROMPTLY_TEST_CLEANUP_FILE: cleanupFile,
      },
      nodePath: process.execPath,
      phaseRunner: childScript,
      repositoryRoot: directory,
      signalSource,
      spawnProcess: (...args) => {
        child = spawn(...args);
        return child;
      },
    });

    try {
      let settled = false;
      const phase = runner.runPhase('proxy').then((result) => {
        settled = true;
        return result;
      });
      await waitForFile(readyFile);
      signalSource.emit(signal);
      signalSource.emit(signal);
      signalSource.emit(otherSignal);
      await delay(20);
      assert.equal(settled, false, 'the parent must wait for child cleanup');
      assert.equal(await readFile(cleanupFile, 'utf8'), 'started');

      const result = await phase;
      assert.equal(result.cancelled, true);
      assert.equal(result.signal, signal);
      assert.equal(result.code, exitCode);
      assert.equal(await readFile(cleanupFile, 'utf8'), 'finished');
      assert.equal(child.exitCode, exitCode);
      assert.equal(child.signalCode, null);
    } finally {
      runner.dispose();
      if (child && child.exitCode === null) {
        const closed = once(child, 'close');
        child.kill('SIGKILL');
        await closed;
      }
      await rm(directory, { recursive: true, force: true });
    }
  });
}

for (const [signal, exitCode, otherSignal] of [
  ['SIGINT', 130, 'SIGTERM'],
  ['SIGTERM', 143, 'SIGINT'],
]) {
  test('real parent survives repeated OS ' + signal + ' during child cleanup', async () => {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'promptly-parent-runner-'));
    const readyFile = path.join(directory, 'ready');
    const cleanupFile = path.join(directory, 'cleanup');
    const releaseFile = path.join(directory, 'release');
    const finishedFile = path.join(directory, 'finished');
    const childPidFile = path.join(directory, 'child-pid');
    const resultFile = path.join(directory, 'result.json');
    const childScript = path.join(directory, 'child.mjs');
    const lifecycleModule = new URL('./orchestrator-lifecycle.mjs', import.meta.url).href;
    await writeFile(childScript, [
      "import { existsSync, writeFileSync } from 'node:fs';",
      '',
      'let handled = false;',
      'const stop = (code) => {',
      '  if (handled) return;',
      '  handled = true;',
      "  writeFileSync(process.env.PROMPTLY_TEST_CLEANUP_FILE, 'started');",
      '  const finishWhenReleased = () => {',
      '    if (existsSync(process.env.PROMPTLY_TEST_RELEASE_FILE)) {',
      "      writeFileSync(process.env.PROMPTLY_TEST_FINISHED_FILE, 'finished');",
      '      process.exit(code);',
      '      return;',
      '    }',
      '    setTimeout(finishWhenReleased, 10);',
      '  };',
      '  finishWhenReleased();',
      '};',
      "process.on('SIGINT', () => stop(130));",
      "process.on('SIGTERM', () => stop(143));",
      'writeFileSync(process.env.PROMPTLY_TEST_CHILD_PID_FILE, String(process.pid));',
      "writeFileSync(process.env.PROMPTLY_TEST_READY_FILE, 'ready');",
      'setInterval(() => {}, 1_000);',
    ].join('\n'));
    const parentSource = [
      "import { writeFileSync } from 'node:fs';",
      "const { createPhaseProcessRunner } = await import(process.env.PROMPTLY_TEST_LIFECYCLE_MODULE);",
      'const runner = createPhaseProcessRunner({',
      '  phaseRunner: process.env.PROMPTLY_TEST_CHILD_SCRIPT,',
      '  repositoryRoot: process.env.PROMPTLY_TEST_ROOT,',
      '});',
      "const result = await runner.runPhase('proxy');",
      'writeFileSync(process.env.PROMPTLY_TEST_RESULT_FILE, JSON.stringify(result));',
      'runner.dispose();',
    ].join('\n');

    let parent = null;
    let childPid = null;
    let parentStderr = '';
    const parentEnvironment = {
      ...process.env,
      PROMPTLY_TEST_READY_FILE: readyFile,
      PROMPTLY_TEST_CLEANUP_FILE: cleanupFile,
      PROMPTLY_TEST_RELEASE_FILE: releaseFile,
      PROMPTLY_TEST_FINISHED_FILE: finishedFile,
      PROMPTLY_TEST_CHILD_PID_FILE: childPidFile,
      PROMPTLY_TEST_RESULT_FILE: resultFile,
      PROMPTLY_TEST_CHILD_SCRIPT: childScript,
      PROMPTLY_TEST_LIFECYCLE_MODULE: lifecycleModule,
      PROMPTLY_TEST_ROOT: directory,
    };

    try {
      parent = spawn(process.execPath, ['--input-type=module', '--eval', parentSource], {
        cwd: directory,
        env: parentEnvironment,
        stdio: ['ignore', 'ignore', 'pipe'],
      });
      parent.stderr.setEncoding('utf8');
      parent.stderr.on('data', (chunk) => { parentStderr += chunk; });
      const parentClosed = once(parent, 'close');
      await waitForFile(readyFile);
      childPid = Number.parseInt(await readFile(childPidFile, 'utf8'), 10);
      assert.equal(processIsAlive(parent.pid), true);
      assert.equal(processIsAlive(childPid), true);

      process.kill(parent.pid, signal);
      await waitForFile(cleanupFile);
      process.kill(parent.pid, signal);
      process.kill(parent.pid, otherSignal);
      await delay(30);
      assert.equal(processIsAlive(parent.pid), true, 'repeated OS signals must not kill the orchestrator');
      assert.equal(await readFile(finishedFile, 'utf8').catch(() => null), null);

      await writeFile(releaseFile, 'release');
      const [parentExit, parentSignal] = await parentClosed;
      assert.equal(parentExit, 0, parentStderr);
      assert.equal(parentSignal, null, parentStderr);
      const result = JSON.parse(await readFile(resultFile, 'utf8'));
      assert.equal(result.cancelled, true);
      assert.equal(result.signal, signal);
      assert.equal(result.code, exitCode);
      assert.equal(await readFile(finishedFile, 'utf8'), 'finished');
      assert.equal(processIsAlive(childPid), false, 'the child must be reaped after cleanup');
    } finally {
      if (parent && parent.exitCode === null) {
        parent.kill('SIGKILL');
        await once(parent, 'close');
      }
      if (childPid && processIsAlive(childPid)) {
        process.kill(childPid, 'SIGKILL');
      }
      await rm(directory, { recursive: true, force: true });
    }
  });
}
