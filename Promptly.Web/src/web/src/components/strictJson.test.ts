import { describe, expect, it } from 'vitest';
import {
  MAX_JSON_BYTES,
  MAX_JSON_DEPTH,
  MAX_JSON_SCALAR_LENGTH,
  parseBoundedJson,
} from './strictJson';

const validInput = '{"messages":[{"role":"user","content":"hello"}]}';

describe('bounded strict JSON preflight', () => {
  it('accepts empty containers, literals, nested metadata, escapes, and JSON numbers', () => {
    const validDocuments = [
      '{}',
      '[]',
      'true',
      'false',
      'null',
      '{"metadata":{"empty":{},"items":[true,false,null]},"messages":[]}',
      '{"negative":-12.5,"fraction":0.125,"exponent":6.02e-23,"positiveExponent":1E+3}',
      '{"text":"quote\\" slash\\\\ slash\\/ backspace\\b form\\f newline\\n return\\r tab\\t snowman\\u2603"}',
    ];

    validDocuments.forEach((document) => {
      expect(parseBoundedJson(document), document).toMatchObject({
        issue: null,
      });
    });
  });

  it.each([
    ['missing object colon', '{"key" 1}'],
    ['missing object comma', '{"key":1 "next":2}'],
    ['trailing object comma', '{"key":1,}'],
    ['mismatched object delimiter', '{"key":1]'],
    ['missing array comma', '[1 2]'],
    ['trailing array comma', '[1,]'],
    ['trailing data', '{"key":1} trailing'],
    ['unterminated object', '{"key":1'],
    ['unterminated array', '[1'],
    ['unterminated string', '"unterminated'],
    ['unterminated escape', '"\\'],
    ['invalid short unicode escape', '{"key":"\\u12"}'],
    ['invalid escape character', '{"key":"\\x"}'],
    ['invalid number sign', '{"number":-}'],
    ['invalid leading zero', '{"number":01}'],
    ['missing fraction digits', '{"number":1.}'],
    ['missing exponent digits', '{"number":1e}'],
    ['missing signed exponent digits', '{"number":1e+}'],
    ['invalid literal', '{"value":tru}'],
  ])('rejects %s with a safe malformed-input issue', (_name, document) => {
    const result = parseBoundedJson(document);

    expect(result.value).toBeNull();
    expect(result.issue?.code).toBe('invalid_json');
    expect(result.issue?.path).toBe('inputSpecJson');
    expect(result.issue?.message).not.toMatch(/SyntaxError|stack|line/i);
  });

  it('rejects raw control characters in strings without exposing parser details', () => {
    const controlCharacterDocument = `{"key":"before${String.fromCharCode(1)}after"}`;
    const result = parseBoundedJson(controlCharacterDocument);

    expect(result).toEqual({
      value: null,
      issue: {
        code: 'invalid_json',
        path: 'inputSpecJson',
        message: 'Input JSON is malformed. Repair it before saving this test.',
      },
    });
  });

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
