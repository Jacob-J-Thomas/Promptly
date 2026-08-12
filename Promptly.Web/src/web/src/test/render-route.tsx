import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

export const renderAtRoute = (
  element: ReactElement,
  initialEntry: string,
  routePath: string,
) => render(
  <MemoryRouter initialEntries={[initialEntry]}>
    <Routes>
      <Route path={routePath} element={element} />
    </Routes>
  </MemoryRouter>,
);

export interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
}

export const deferred = <T,>(): Deferred<T> => {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((promiseResolve, promiseReject) => {
    resolve = promiseResolve;
    reject = promiseReject;
  });

  return { promise, resolve, reject };
};
