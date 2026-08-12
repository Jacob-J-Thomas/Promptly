# Promptly web client

The React client is built with TypeScript, Vite, and Material UI. Use the exact Node.js patch recorded in `.node-version` and install only the dependency graph recorded in `package-lock.json`.

```sh
npm ci
npm run dev
```

## Verification

Run the same gates used by CI from this directory:

```sh
npm ci
npm run typecheck
npm run lint -- --max-warnings=0
npm run test:coverage
npm run build
```

Vitest fails when no tests are discovered. The coverage command also measures all TypeScript and React production modules under `src` and enforces at least 90% line and branch coverage. Tests and test infrastructure are the only excluded files; new production modules enter the measured cohort automatically.

CI writes machine-readable results at the repository root:

- `artifacts/test-results/web/junit.xml`
- `artifacts/test-results/web/coverage/cobertura-coverage.xml`
- `artifacts/test-results/web/coverage/coverage-final.json`
