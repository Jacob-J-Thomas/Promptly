import Editor, { loader } from '@monaco-editor/react';
import * as monaco from 'monaco-editor';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import jsonWorker from 'monaco-editor/esm/vs/language/json/json.worker?worker';

const runtime = globalThis as typeof globalThis & {
  MonacoEnvironment?: {
    getWorker: (_moduleId: string, label: string) => Worker;
  };
};

runtime.MonacoEnvironment = {
  getWorker: (_moduleId, label) => {
    if (label === 'json') {
      return new jsonWorker();
    }
    return new editorWorker();
  },
};

loader.config({ monaco });

export default Editor;
