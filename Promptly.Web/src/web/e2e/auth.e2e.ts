import { createHash, createHmac, randomUUID } from 'node:crypto';
import { expect, test } from './fixtures';

const requiredEnvironment = (name: string): string => {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} is required; run npm run test:e2e`);
  }
  return value;
};

const base64UrlJson = (value: object): string => (
  Buffer.from(JSON.stringify(value)).toString('base64url')
);

const createExpiredToken = (): string => {
  const signingKey = Buffer.from(requiredEnvironment('PROMPTLY_E2E_JWT_KEY'), 'base64');
  const now = Math.floor(Date.now() / 1000);
  const userId = randomUUID();
  const header = base64UrlJson({
    alg: 'HS256',
    kid: createHash('sha256').update(signingKey).digest('hex').slice(0, 16),
    typ: 'JWT',
  });
  const payload = base64UrlJson({
    aud: 'Promptly-E2E',
    email: 'expired-session@promptly.invalid',
    exp: now - 600,
    iat: now - 1_200,
    iss: 'Promptly-E2E',
    jti: randomUUID(),
    nbf: now - 1_200,
    sub: userId,
    'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': userId,
  });
  const signature = createHmac('sha256', signingKey)
    .update(`${header}.${payload}`)
    .digest('base64url');
  return `${header}.${payload}.${signature}`;
};

test('anonymous protected routes redirect to login', async ({ page }) => {
  await page.goto('/projects/00000000-0000-0000-0000-000000000000');

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('heading', { name: 'Sign in to your account' })).toBeVisible();
  await expect.poll(() => page.evaluate(() => ({
    token: localStorage.getItem('auth_token'),
    user: localStorage.getItem('user'),
  }))).toEqual({ token: null, user: null });
});

test('registration logout and login traverse the real stack', async ({ page }) => {
  const unique = randomUUID();
  const email = `e2e-${unique}@promptly.invalid`;
  const password = `Promptly-${unique}-A1`;

  await page.goto('/register');
  await page.getByLabel('Full Name').fill('E2E User');
  await page.getByLabel('Email Address').fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByLabel('Confirm Password').fill(password);

  const registration = page.waitForResponse((response) => (
    response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/auth/register'
  ));
  await page.getByRole('button', { name: 'Sign Up' }).click();
  expect((await registration).status()).toBe(200);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole('heading', { name: 'Projects' })).toBeVisible();
  await expect(page.getByText('No projects yet')).toBeVisible();
  await expect.poll(() => page.evaluate(() => ({
    hasToken: localStorage.getItem('auth_token') !== null,
    hasUser: localStorage.getItem('user') !== null,
  }))).toEqual({ hasToken: true, hasUser: true });

  await page.getByRole('button', { name: 'account of current user' }).click();
  await page.getByRole('menuitem', { name: 'Logout' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect.poll(() => page.evaluate(() => ({
    token: localStorage.getItem('auth_token'),
    user: localStorage.getItem('user'),
  }))).toEqual({ token: null, user: null });

  await page.getByLabel('Email Address').fill(email);
  await page.getByLabel('Password').fill(password);
  const login = page.waitForResponse((response) => (
    response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/auth/login'
  ));
  await page.getByRole('button', { name: 'Sign In' }).click();
  expect((await login).status()).toBe(200);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole('heading', { name: 'Projects' })).toBeVisible();
  await expect.poll(() => page.evaluate(() => ({
    hasToken: localStorage.getItem('auth_token') !== null,
    hasUser: localStorage.getItem('user') !== null,
  }))).toEqual({ hasToken: true, hasUser: true });
});

test(
  'an expired signed session receives 401 and clears stored authentication',
  async ({ page, expectedHttpFailures }) => {
    const token = createExpiredToken();
    const storedUser = {
      id: randomUUID(),
      name: 'Expired E2E User',
      email: 'expired-session@promptly.invalid',
    };
    expectedHttpFailures.push({ method: 'GET', path: '/api/projects', status: 401 });
    await page.goto('/login');
    await expect(page.getByRole('heading', { name: 'Sign in to your account' })).toBeVisible();
    await page.evaluate(({ expiredToken, user }) => {
      localStorage.setItem('auth_token', expiredToken);
      localStorage.setItem('user', JSON.stringify(user));
    }, { expiredToken: token, user: storedUser });

    const unauthorized = page.waitForResponse((response) => (
      response.request().method() === 'GET'
        && new URL(response.url()).pathname === '/api/projects'
        && response.status() === 401
    ));
    await page.goto('/');
    const response = await unauthorized;

    expect(response.status()).toBe(401);
    expect(await response.request().headerValue('authorization')).toBe(`Bearer ${token}`);
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('heading', { name: 'Sign in to your account' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => ({
      token: localStorage.getItem('auth_token'),
      user: localStorage.getItem('user'),
    }))).toEqual({ token: null, user: null });
  },
);
