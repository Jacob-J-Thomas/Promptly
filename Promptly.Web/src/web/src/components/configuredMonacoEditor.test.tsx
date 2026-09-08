import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import ConfiguredMonacoEditor, { getMonacoWorker } from './configuredMonacoEditor';

const { loaderConfig } = vi.hoisted(() => ({
  loaderConfig: vi.fn(),
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

vi.mock('monaco-editor', () => ({}));
vi.mock('monaco-editor/esm/vs/editor/editor.worker?worker', () => ({
  default: class LocalEditorWorker {},
}));
vi.mock('monaco-editor/esm/vs/language/json/json.worker?worker', () => ({
  default: class LocalJsonWorker {},
}));

type WorkerConstructor = new () => Worker;

class EditorWorker {}
class JsonWorker {}

const workers = {
  editor: EditorWorker as unknown as WorkerConstructor,
  json: JsonWorker as unknown as WorkerConstructor,
};

describe('configured Monaco worker routing', () => {
  it.each([
    ['json', JsonWorker],
    ['yaml', EditorWorker],
    ['unknown', EditorWorker],
  ])('uses the local %s worker', (label, expectedWorker) => {
    expect(getMonacoWorker(label, workers)).toBeInstanceOf(expectedWorker);
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
    expect(loaderConfig).toHaveBeenCalledWith({ monaco: expect.anything() });

    fireEvent.change(editor, { target: { value: '- id: repaired' } });
    expect(onChange).toHaveBeenCalledWith('- id: repaired');
  });
});
