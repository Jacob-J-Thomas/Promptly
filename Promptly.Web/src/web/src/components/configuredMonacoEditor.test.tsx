import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import * as monaco from 'monaco-editor';
import ConfiguredMonacoEditor from './configuredMonacoEditor';

const { loaderConfig, editorWorker, jsonWorker } = vi.hoisted(() => ({
  loaderConfig: vi.fn(),
  editorWorker: class LocalEditorWorker {},
  jsonWorker: class LocalJsonWorker {},
}));

vi.mock('@monaco-editor/react', () => ({
  default: ({
    language,
    value,
    onChange,
    options,
  }: {
    language?: string;
    value?: string;
    onChange?: (value: string | undefined) => void;
    options?: { ariaLabel?: string };
  }) => (
    <textarea
      aria-label={options?.ariaLabel}
      data-language={language}
      value={value ?? ''}
      onChange={(event) => onChange?.(event.target.value)}
    />
  ),
  loader: { config: loaderConfig },
}));

vi.mock('monaco-editor', () => ({ editor: {} }));
vi.mock('monaco-editor/editor/editor.worker.js?worker', () => ({
  default: editorWorker,
}));
vi.mock('monaco-editor/language/json/json.worker.js?worker', () => ({
  default: jsonWorker,
}));

describe('configured Monaco adapter', () => {
  it('routes the configured global worker factory to local JSON and editor assets', () => {
    const environment = (globalThis as typeof globalThis & {
      MonacoEnvironment?: { getWorker: (_moduleId: string, label: string) => Worker };
    }).MonacoEnvironment;
    expect(environment).toBeDefined();
    expect(environment?.getWorker('', 'json')).toBeInstanceOf(jsonWorker);
    expect(environment?.getWorker('', 'yaml')).toBeInstanceOf(editorWorker);
    expect(environment?.getWorker('', 'unknown')).toBeInstanceOf(editorWorker);
  });

  it('configures the local loader and forwards YAML editor props', () => {
    const onChange = vi.fn();
    render(
      <ConfiguredMonacoEditor
        language="yaml"
        value="- id: case-1"
        onChange={onChange}
        options={{ ariaLabel: 'Test YAML' }}
      />,
    );

    const editor = screen.getByRole('textbox', { name: 'Test YAML' });
    expect(editor).toHaveAttribute('data-language', 'yaml');
    expect(editor).toHaveValue('- id: case-1');
    expect(loaderConfig).toHaveBeenCalledWith({ monaco });

    fireEvent.change(editor, { target: { value: '- id: repaired' } });
    expect(onChange).toHaveBeenCalledWith('- id: repaired');
  });
});
