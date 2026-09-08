import Editor, { loader } from '@monaco-editor/react';
import * as monaco from 'monaco-editor';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import jsonWorker from 'monaco-editor/esm/vs/language/json/json.worker?worker';

type MonacoWorkerConstructor = new () => Worker;

// The worker router is exported for direct branch coverage; the component is
// still the only UI export from this module and remains hot-reload compatible.
// eslint-disable-next-line react-refresh/only-export-components
export const getMonacoWorker = (
  label: string,
  workers: {
    editor: MonacoWorkerConstructor;
    json: MonacoWorkerConstructor;
  } = {
    editor: editorWorker,
    json: jsonWorker,
  },
): Worker => {
  if (label === 'json') {
    return new workers.json();
  }
  return new workers.editor();
};

const runtime = globalThis as typeof globalThis & {
  MonacoEnvironment?: {
    getWorker: (_moduleId: string, label: string) => Worker;
  };
};

runtime.MonacoEnvironment = {
  getWorker: (_moduleId, label) => getMonacoWorker(label),
};

loader.config({ monaco });

export default Editor;
