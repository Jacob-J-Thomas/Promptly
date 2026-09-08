import { describe, expect, it } from 'vitest';
import {
  MAX_MESSAGE_COUNT,
  parseInputSpecJson,
  serializeInputSpec,
  type ConversationMessage,
} from './messageSpec';
import {
  MAX_JSON_BYTES,
  MAX_JSON_DEPTH,
  MAX_JSON_SCALAR_LENGTH,
} from './strictJson';

describe('message input-spec helpers', () => {
  it('preserves unknown metadata and ordered messages when editing', () => {
    const original = {
      messages: [
        { role: 'system', content: 'Be concise.' },
        { role: 'user', content: 'Hello' },
      ],
      temperature: 0.5,
      enabled: true,
      metadata: {
        tags: ['smoke', 'conversation'],
        nullable: null,
      },
      options: [{ mode: 'safe', retries: 2 }],
    };
    const parsed = parseInputSpecJson(JSON.stringify(original));

    expect(parsed.valid).toBe(true);
    expect(parsed.errors).toEqual([]);
    expect(parsed.messages).toEqual(original.messages);
    expect(parsed.metadata).toEqual({
      temperature: 0.5,
      enabled: true,
      metadata: original.metadata,
      options: original.options,
    });

    const editedMessages = parsed.messages!.map((message, index) => (
      index === 1 ? { ...message, content: 'Hello again' } : { ...message }
    ));
    const serialized = serializeInputSpec(editedMessages, parsed.metadata);

    expect(JSON.parse(serialized)).toEqual({
      ...original,
      messages: [
        { role: 'system', content: 'Be concise.' },
        { role: 'user', content: 'Hello again' },
      ],
    });
  });

  it('does not mutate caller-owned messages or metadata while serializing', () => {
    const messages: ConversationMessage[] = [{ role: 'user', content: 'Hello' }];
    const metadata = { enabled: true, nested: { values: [1, null, false] } };

    serializeInputSpec(messages, metadata);

    expect(messages).toEqual([{ role: 'user', content: 'Hello' }]);
    expect(metadata).toEqual({ enabled: true, nested: { values: [1, null, false] } });
  });

  it.each([
    ['not-json', 'invalid_json'],
    ['null', 'invalid_shape'],
    ['{"prompt":"hello"}', 'required'],
    ['{"messages":[]}', 'required'],
    ['{"messages":[{"role":"developer","content":"hello"}]}', 'invalid_role'],
    ['{"messages":[{"role":"user","content":42}]}', 'invalid_type'],
  ])('rejects malformed input %s without a fallback message row', (input, code) => {
    const parsed = parseInputSpecJson(input);

    expect(parsed.valid).toBe(false);
    expect(parsed.messages).toBeNull();
    expect(parsed.originalText).toBe(input);
    expect(parsed.errors[0]?.code).toBe(code);
  });

  it('rejects unknown message properties while retaining top-level metadata and original text', () => {
    const input = JSON.stringify({
      temperature: 0.5,
      options: { retries: 2 },
      messages: [{ role: 'user', content: 'Hello', provider_hint: 'stored' }],
    });

    const parsed = parseInputSpecJson(input);

    expect(parsed.valid).toBe(false);
    expect(parsed.messages).toBeNull();
    expect(parsed.originalText).toBe(input);
    expect(parsed.metadata).toEqual({ temperature: 0.5, options: { retries: 2 } });
    expect(parsed.errors).toContainEqual({
      code: 'unknown_property',
      path: 'inputSpecJson.messages[0].provider_hint',
      message: 'Message property "provider_hint" is not supported.',
    });
  });

  it('rejects duplicate properties at every input depth and preserves the raw document', () => {
    const topLevel = '{"messages":[{"role":"user","content":"old"}],"messages":[]}';
    const messageLevel = '{"messages":[{"role":"user","content":"old","\\u0063ontent":"new"}]}';
    const metadataLevel = '{"messages":[{"role":"user","content":"hello"}],"metadata":{"tag":1,"\\u0074ag":2}}';

    [topLevel, messageLevel, metadataLevel].forEach((input) => {
      const parsed = parseInputSpecJson(input);
      expect(parsed.valid).toBe(false);
      expect(parsed.messages).toBeNull();
      expect(parsed.originalText).toBe(input);
      expect(parsed.errors[0]?.code).toBe('duplicate_property');
    });
    expect(parseInputSpecJson(messageLevel).errors[0]?.path)
      .toBe('inputSpecJson.messages[0].content');
    expect(parseInputSpecJson(metadataLevel).errors[0]?.path)
      .toBe('inputSpecJson.metadata.tag');
  });

  it('rejects oversized and overly deep legacy JSON before materializing messages', () => {
    const exactBytesBase = '{"messages":[{"role":"user","content":"hello"}]}';
    const exactPadding = ' '.repeat(MAX_JSON_BYTES - new TextEncoder().encode(exactBytesBase).length);
    expect(parseInputSpecJson(exactBytesBase + exactPadding).valid).toBe(true);

    const oversized = `${exactBytesBase}${exactPadding} `;
    const oversizedResult = parseInputSpecJson(oversized);
    expect(oversizedResult.valid).toBe(false);
    expect(oversizedResult.messages).toBeNull();
    expect(oversizedResult.originalText).toBe(oversized);
    expect(oversizedResult.errors[0]).toEqual({
      code: 'too_large',
      path: 'inputSpecJson',
      message: 'Input JSON exceeds the 262144-byte limit.',
    });

    let exactDepth = 'true';
    for (let index = 0; index < MAX_JSON_DEPTH; index += 1) {
      exactDepth = `{"next":${exactDepth}}`;
    }
    expect(parseInputSpecJson(exactDepth).valid).toBe(false);
    const deep = `{"next":${exactDepth}}`;
    const deepResult = parseInputSpecJson(deep);
    expect(deepResult.valid).toBe(false);
    expect(deepResult.messages).toBeNull();
    expect(deepResult.errors[0]?.code).toBe('too_large');
  });

  it('enforces message and scalar boundaries in controlled validation', () => {
    const exactMessages = Array.from({ length: MAX_MESSAGE_COUNT }, () => ({
      role: 'user' as const,
      content: 'x',
    }));
    expect(parseInputSpecJson(JSON.stringify({ messages: exactMessages })).valid).toBe(true);
    const tooManyMessages = parseInputSpecJson(JSON.stringify({
      messages: [...exactMessages, { role: 'user', content: 'x' }],
    }));
    expect(tooManyMessages.valid).toBe(false);
    expect(tooManyMessages.errors[0]).toEqual({
      code: 'too_large',
      path: 'inputSpecJson.messages',
      message: 'Messages cannot exceed 100 items.',
    });

    const exactScalar = { role: 'user' as const, content: 'x'.repeat(MAX_JSON_SCALAR_LENGTH) };
    expect(parseInputSpecJson(JSON.stringify({ messages: [exactScalar] })).valid).toBe(true);
    const tooLong = parseInputSpecJson(JSON.stringify({
      messages: [{ role: 'user', content: `${'x'.repeat(MAX_JSON_SCALAR_LENGTH)}x` }],
    }));
    expect(tooLong.valid).toBe(false);
    expect(tooLong.errors[0]?.code).toBe('too_large');
  });

  it('returns a warning for a valid assistant-only conversation', () => {
    const parsed = parseInputSpecJson('{"messages":[{"role":"assistant","content":"Hi"}]}');

    expect(parsed.valid).toBe(true);
    expect(parsed.warnings).toHaveLength(1);
    expect(parsed.warnings[0]?.message).toContain('user message');
  });
});
