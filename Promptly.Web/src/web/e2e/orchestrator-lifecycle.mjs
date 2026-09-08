import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';

/**
 * Runs the serial phase children and owns cancellation propagation. The child
 * is allowed to finish its own signal-aware cleanup before this promise
 * resolves on its close event.
 */
export const createPhaseProcessRunner = ({
  baseEnvironment = process.env,
  nodePath = process.execPath,
  phaseRunner,
  repositoryRoot,
  signalSource = process,
  spawnProcess = spawn,
} = {}) => {
  let cancellationSignal = null;
  let active = null;
  const handlers = new Map();

  const handleSignal = (signal) => {
    cancellationSignal ??= signal;
    if (active && !active.forwarded) {
      active.forwarded = true;
      active.child.kill(signal);
    }
  };

  for (const signal of ['SIGINT', 'SIGTERM']) {
    const handler = () => handleSignal(signal);
    handlers.set(signal, handler);
    signalSource.on(signal, handler);
  }

  const runPhase = (phase) => {
    if (cancellationSignal) {
      return Promise.resolve({
        code: -1,
        cancelled: true,
        signal: cancellationSignal,
        started: false,
      });
    }

    const environment = {
      ...baseEnvironment,
      PROMPTLY_E2E_PHASE: phase,
    };
    if (phase === 'direct') {
      environment.PROMPTLY_E2E_EXPECTED_CORRELATION = randomUUID();
    } else {
      delete environment.PROMPTLY_E2E_EXPECTED_CORRELATION;
    }

    return new Promise((resolve) => {
      let child;
      try {
        child = spawnProcess(nodePath, [phaseRunner], {
          cwd: repositoryRoot,
          env: environment,
          stdio: 'inherit',
        });
      } catch (error) {
        resolve({
          code: -1,
          cancelled: false,
          signal: null,
          started: false,
          error: error instanceof Error ? error.message : String(error),
        });
        return;
      }

      const state = { child, forwarded: false };
      active = state;
      let settled = false;
      let childError = null;
      const finish = (result) => {
        if (settled) {
          return;
        }
        settled = true;
        if (active === state) {
          active = null;
        }
        resolve({
          ...result,
          cancelled: cancellationSignal !== null,
          signal: cancellationSignal,
          started: true,
        });
      };

      child.once('error', (error) => {
        childError = error instanceof Error ? error.message : String(error);
      });
      child.once('close', (code) => finish({
        code: code ?? -1,
        ...(childError ? { error: childError } : {}),
      }));

      // A signal can arrive between the initial guard and spawn; forward it
      // after the child is registered so it is still delivered exactly once.
      if (cancellationSignal) {
        handleSignal(cancellationSignal);
      }
    });
  };

  const dispose = () => {
    for (const [signal, handler] of handlers) {
      signalSource.removeListener(signal, handler);
    }
    handlers.clear();
  };

  return {
    dispose,
    runPhase,
    get cancellationSignal() {
      return cancellationSignal;
    },
  };
};
