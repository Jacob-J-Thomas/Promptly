import { expect, test as base, type Page } from '@playwright/test';

interface ExpectedHttpFailure {
  method: string;
  path: string;
  status: number;
}

interface BrowserGuardFixtures {
  expectedHttpFailures: ExpectedHttpFailure[];
  browserGuard: void;
}

const formatHttpFailure = ({ method, path, status }: ExpectedHttpFailure): string => (
  `${method.toUpperCase()} ${path} ${status}`
);

export const test = base.extend<BrowserGuardFixtures>({
  expectedHttpFailures: async ({ browserName }, use) => {
    if (browserName !== 'chromium') {
      throw new Error(`Unsupported E2E browser: ${browserName}`);
    }
    await use([]);
  },

  browserGuard: [async ({ context, page, baseURL, expectedHttpFailures }, use, testInfo) => {
    if (!baseURL) {
      throw new Error('Playwright baseURL is required');
    }

    const allowedOrigin = new URL(baseURL).origin;
    const allowedWebSocketOrigin = allowedOrigin
      .replace(/^http:/, 'ws:')
      .replace(/^https:/, 'wss:');
    const violations: string[] = [];
    const consoleErrors: string[] = [];
    const observedHttpFailures: ExpectedHttpFailure[] = [];

    context.on('request', (request) => {
      const requestUrl = new URL(request.url());
      if (
        (requestUrl.protocol === 'http:' || requestUrl.protocol === 'https:')
        && requestUrl.origin !== allowedOrigin
      ) {
        violations.push(`Unexpected browser egress: ${request.method()} ${request.url()}`);
      }
    });

    context.on('requestfailed', (request) => {
      violations.push(
        `Browser request failed: ${request.method()} ${request.url()} `
        + `(${request.failure()?.errorText ?? 'unknown error'})`,
      );
    });

    context.on('response', (response) => {
      if (response.status() < 400) {
        return;
      }

      const responseUrl = new URL(response.url());
      observedHttpFailures.push({
        method: response.request().method(),
        path: `${responseUrl.pathname}${responseUrl.search}`,
        status: response.status(),
      });
    });

    const guardedPages = new WeakSet<Page>();
    const guardPage = (guardedPage: Page) => {
      if (guardedPages.has(guardedPage)) {
        return;
      }
      guardedPages.add(guardedPage);
      guardedPage.on('console', (message) => {
        if (message.type() === 'error') {
          consoleErrors.push(message.text());
        }
      });
      guardedPage.on('pageerror', (error) => {
        violations.push(`Uncaught page error: ${error.message}`);
      });
      guardedPage.on('websocket', (webSocket) => {
        const webSocketUrl = new URL(webSocket.url());
        if (
          (webSocketUrl.protocol !== 'ws:' && webSocketUrl.protocol !== 'wss:')
          || webSocketUrl.origin !== allowedWebSocketOrigin
        ) {
          violations.push(`Unexpected browser WebSocket egress: ${webSocket.url()}`);
        }
        webSocket.on('socketerror', (error) => {
          violations.push(`Browser WebSocket failed: ${webSocket.url()} (${error})`);
        });
      });
    };

    guardPage(page);
    context.on('page', guardPage);

    await use();

    const expected = expectedHttpFailures.map(formatHttpFailure).sort();
    const observed = observedHttpFailures.map(formatHttpFailure).sort();
    if (JSON.stringify(expected) !== JSON.stringify(observed)) {
      violations.push(
        `HTTP failure inventory mismatch: expected=${JSON.stringify(expected)} `
        + `observed=${JSON.stringify(observed)}`,
      );
    }

    let expectedUnauthorizedConsoleErrors = expectedHttpFailures.filter(
      ({ status }) => status === 401,
    ).length;
    for (const message of consoleErrors) {
      if (
        expectedUnauthorizedConsoleErrors > 0
        && message === 'Failed to load resource: the server responded with a status of 401 (Unauthorized)'
      ) {
        expectedUnauthorizedConsoleErrors -= 1;
      } else {
        violations.push(`Browser console error: ${message}`);
      }
    }

    if (violations.length > 0) {
      const diagnostic = `${violations.join('\n')}\n`;
      await testInfo.attach('browser-guard-violations', {
        body: diagnostic,
        contentType: 'text/plain',
      });
      throw new Error(diagnostic);
    }
  }, { auto: true }],
});

export { expect };
export type { ExpectedHttpFailure };
