import { describe, expect, it } from 'vitest';
import {
  parseInputSpecJson,
  serializeInputSpec,
  type ConversationMessage,
} from './messageSpec';

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

  it('returns a warning for a valid assistant-only conversation', () => {
    const parsed = parseInputSpecJson('{"messages":[{"role":"assistant","content":"Hi"}]}');

    expect(parsed.valid).toBe(true);
    expect(parsed.warnings).toHaveLength(1);
    expect(parsed.warnings[0]?.message).toContain('user message');
  });
});
