import { beforeEach, describe, expect, it, vi } from 'vitest';

const rootRender = vi.hoisted(() => vi.fn());
const createRoot = vi.hoisted(() => vi.fn(() => ({ render: rootRender })));

vi.mock('react-dom/client', () => ({ createRoot }));
vi.mock('./App.tsx', () => ({ default: () => <div>Promptly application</div> }));

describe('application bootstrap', () => {
  beforeEach(() => {
    vi.resetModules();
    createRoot.mockClear();
    rootRender.mockClear();
    document.body.replaceChildren();
  });

  it('mounts the application into the root element', async () => {
    const rootElement = document.createElement('div');
    rootElement.id = 'root';
    document.body.append(rootElement);

    await import('./main');

    expect(createRoot).toHaveBeenCalledWith(rootElement);
    expect(rootRender).toHaveBeenCalledOnce();
  });

  it('reports a missing root element without trying to render', async () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);

    await import('./main');

    expect(createRoot).not.toHaveBeenCalled();
    expect(rootRender).not.toHaveBeenCalled();
    expect(consoleError).toHaveBeenCalledWith('Main.tsx: Root element not found!');
    consoleError.mockRestore();
  });
});
