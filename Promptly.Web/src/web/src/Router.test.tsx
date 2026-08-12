import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { Router } from './Router';

vi.mock('./components/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: ReactNode }) => (
    <section data-testid="protected-route">{children}</section>
  ),
}));
vi.mock('./pages/Login', () => ({ Login: () => <div>Login route</div> }));
vi.mock('./pages/Register', () => ({ Register: () => <div>Register route</div> }));
vi.mock('./pages/ProjectsList', () => ({ ProjectsList: () => <div>Projects route</div> }));
vi.mock('./pages/ProjectDetail', () => ({ ProjectDetail: () => <div>Project detail route</div> }));
vi.mock('./pages/EnvironmentDetail', () => ({ EnvironmentDetail: () => <div>Environment detail route</div> }));
vi.mock('./pages/EnvironmentForm', () => ({ EnvironmentForm: () => <div>Environment form route</div> }));
vi.mock('./pages/SuiteDetail', () => ({ SuiteDetail: () => <div>Suite detail route</div> }));
vi.mock('./pages/SuiteForm', () => ({ SuiteForm: () => <div>Suite form route</div> }));
vi.mock('./pages/RunDetail', () => ({ RunDetail: () => <div>Run detail route</div> }));
vi.mock('./pages/RunsList', () => ({ RunsList: () => <div>Runs list route</div> }));

describe('Router', () => {
  it.each([
    ['/login', 'Login route', false],
    ['/register', 'Register route', false],
    ['/', 'Projects route', true],
    ['/projects/project-1', 'Project detail route', true],
    ['/projects/project-1/environments/new', 'Environment form route', true],
    ['/projects/project-1/suites/new', 'Suite form route', true],
    ['/environments/environment-1', 'Environment detail route', true],
    ['/suites/suite-1', 'Suite detail route', true],
    ['/suites/suite-1/runs', 'Runs list route', true],
    ['/runs/run-1', 'Run detail route', true],
  ])('routes %s to its expected page', (path, expected, isProtected) => {
    window.history.pushState({}, '', path);
    render(<Router />);

    const page = screen.getByText(expected);
    expect(page).toBeInTheDocument();
    if (isProtected) {
      expect(screen.getByTestId('protected-route')).toContainElement(page);
    } else {
      expect(screen.queryByTestId('protected-route')).not.toBeInTheDocument();
    }
  });

  it('redirects unknown paths to the protected projects route', async () => {
    window.history.pushState({}, '', '/not-a-route');
    render(<Router />);

    expect(await screen.findByText('Projects route')).toBeInTheDocument();
    expect(window.location.pathname).toBe('/');
    expect(screen.getByTestId('protected-route')).toBeInTheDocument();
  });
});
