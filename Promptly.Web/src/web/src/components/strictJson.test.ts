import { describe, expect, it } from 'vitest';
import {
  MAX_JSON_BYTES,
  MAX_JSON_DEPTH,
  MAX_JSON_SCALAR_LENGTH,
  parseBoundedJson,
} from './strictJson';

const validInput = '{"messages":[{"role":"user","content":"hello"}]}';

describe('bounded strict JSON preflight', () => {
  it('rejects duplicate top-level and nested keys, including escaped equivalents', () => {
    const topLevel = parseBoundedJson(
      '{"messages":[{"role":"user","content":"hello"}],"enabled":true,"\\u0065nabled":false}',
    );
    expect(topLevel).toEqual({
      value: null,
      issue: {
        code: 'duplicate_property',
        path: 'inputSpecJson.enabled',
        message: 'Duplicate JSON property is not allowed.',
      },
    });

    const nested = parseBoundedJson(
      '{"messages":[{"role":"user","content":"hello","\\u0072ole":"assistant"}]}',
    );
    expect(nested.issue).toEqual({
      code: 'duplicate_property',
      path: 'inputSpecJson.messages[0].role',
      message: 'Duplicate JSON property is not allowed.',
    });
  });

  it('accepts the exact byte limit and rejects the next byte without parsing', () => {
    const padding = ' '.repeat(MAX_JSON_BYTES - new TextEncoder().encode(validInput).length);
    const exact = parseBoundedJson(validInput + padding);
    expect(exact.issue).toBeNull();
    expect(exact.value).toEqual({ messages: [{ role: 'user', content: 'hello' }] });

    const over = parseBoundedJson(`${validInput}${padding} `);
    expect(over).toEqual({
      value: null,
      issue: {
        code: 'too_large',
        path: 'inputSpecJson',
        message: 'Input JSON exceeds the 262144-byte limit.',
      },
    });
  });

  it('accepts depth N and rejects depth N+1 with a stable safe issue', () => {
    let exact = 'true';
    for (let index = 0; index < MAX_JSON_DEPTH; index += 1) {
      exact = `{"next":${exact}}`;
    }
    expect(parseBoundedJson(exact).issue).toBeNull();

    const over = `{"next":${exact}}`;
    expect(parseBoundedJson(over)).toEqual({
      value: null,
      issue: {
        code: 'too_large',
        path: `inputSpecJson${'.next'.repeat(MAX_JSON_DEPTH + 1)}`,
        message: 'Input JSON nesting cannot exceed 32 levels.',
      },
    });
  });

  it('rejects the exact next scalar character and non-finite numeric results', () => {
    const exactScalar = `{"text":"${'x'.repeat(MAX_JSON_SCALAR_LENGTH)}"}`;
    expect(parseBoundedJson(exactScalar).issue).toBeNull();
    expect(parseBoundedJson(`{"text":"${'x'.repeat(MAX_JSON_SCALAR_LENGTH + 1)}"}`).issue)
      .toEqual({
        code: 'too_large',
        path: 'inputSpecJson.text',
        message: 'User-authored JSON strings cannot exceed 16384 characters.',
      });

    expect(parseBoundedJson('{"value":1e400}').issue).toEqual({
      code: 'invalid_number',
      path: 'inputSpecJson.value',
      message: 'JSON numbers must be finite.',
    });
  });

  it('maps malformed JSON to a safe issue without exposing parser internals', () => {
    const result = parseBoundedJson('{"messages":[}');
    expect(result.value).toBeNull();
    expect(result.issue?.code).toBe('invalid_json');
    expect(result.issue?.message).not.toMatch(/SyntaxError|stack|line/i);
  });
});
