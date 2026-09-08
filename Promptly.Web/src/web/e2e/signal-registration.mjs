const supportedSignals = ['SIGINT', 'SIGTERM'];

/**
 * Registers persistent, idempotent process signal handlers. The first signal
 * remains authoritative while cleanup is in progress; later deliveries are
 * intentionally ignored until the caller disposes the registration.
 */
export const createSignalRegistration = ({
  signalSource = process,
  onSignal,
  signals = supportedSignals,
} = {}) => {
  let firstSignal = null;
  const handlers = new Map();

  const handleSignal = (signal) => {
    if (firstSignal !== null) {
      return;
    }
    firstSignal = signal;
    onSignal?.(signal);
  };

  for (const signal of signals) {
    const handler = () => handleSignal(signal);
    handlers.set(signal, handler);
    signalSource.on(signal, handler);
  }

  return {
    get signal() {
      return firstSignal;
    },
    dispose() {
      for (const [signal, handler] of handlers) {
        signalSource.removeListener(signal, handler);
      }
      handlers.clear();
    },
  };
};
