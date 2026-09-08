import { describe, expect, it } from 'vitest';
import { formToYaml, yamlToForm, type TestCaseYamlDraft } from './testCaseYaml';

const draft: TestCaseYamlDraft = {
  externalId: 'case-1',
  name: 'Greeting',
  description: 'Optional description',
  inputMetadata: {
    temperature: 0.5,
    enabled: true,
    nullable: null,
    metadata: { tags: ['demo', 'smoke'], nested: { count: 2 } },
  },
  messages: [
    { role: 'system', content: 'Be concise.' },
    { role: 'user', content: 'Hello\nworld' },
    { role: 'assistant', content: 'Hi' },
  ],
  expectations: [
    { type: 'contains_text', text: 'Hi', case_insensitive: true },
    { type: 'tool_sequence', sequence: ['search', 'summarize'], exact_sequence: false },
    { type: 'llm_judge', rubric: 'Helpful', min_score: 0.8, model: 'judge', provider: 'local' },
  ],
};

const minimalYaml = (name = 'Greeting') => `- id: case-1
  name: ${name}
  input:
    messages:
      - role: user
        content: Hello
  expectations:
    - type: contains_text
      text: Hello`;

describe('test-case YAML conversion', () => {
  it('round-trips one root row with rich input metadata and ordered options', () => {
    const written = formToYaml(draft);
    expect(written.valid).toBe(true);
    expect(written.yaml).toContain('- id: case-1');
    expect(written.yaml).not.toContain('value_kind');

    const parsed = yamlToForm(written.yaml!);
    expect(parsed).toEqual({ valid: true, errors: [], draft });
  });

  it('accepts the nullable description emitted by server YAML export', () => {
    const parsed = yamlToForm(`${minimalYaml().replace(
      '  name: Greeting',
      '  name: Greeting\n  description: null',
    )}`);

    expect(parsed).toEqual({
      valid: true,
      errors: [],
      draft: expect.objectContaining({ description: '' }),
    });
  });

  it('round-trips larger ordered drafts without collapsing rows or metadata', () => {
    const largerDraft: TestCaseYamlDraft = {
      ...draft,
      messages: Array.from({ length: 12 }, (_, index) => ({
        role: index % 3 === 0 ? 'system' : index % 3 === 1 ? 'user' : 'assistant',
        content: `message-${index}`,
      })),
      expectations: Array.from({ length: 8 }, (_, index) => ({
        type: 'contains_text',
        text: `expected-${index}`,
        case_insensitive: index % 2 === 0,
      })),
    };
    const written = formToYaml(largerDraft);
    expect(written.valid).toBe(true);
    const parsed = yamlToForm(written.yaml!);
    expect(parsed.valid).toBe(true);
    expect(parsed.draft?.messages).toHaveLength(12);
    expect(parsed.draft?.expectations).toHaveLength(8);
    expect(parsed.draft?.inputMetadata).toEqual(draft.inputMetadata);
    expect(parsed.draft?.messages[11]?.content).toBe('message-11');
    expect(parsed.draft?.expectations[7]?.text).toBe('expected-7');
  });

  it.each([
    ['', 'invalid_shape'],
    ['null', 'invalid_shape'],
    ['{}', 'invalid_shape'],
    ['- id: case-1\n  name: First\n  input: {messages: []}\n  expectations: [{type: groundedness, min_score: 0.8}]\n- id: case-2\n  name: Second\n  input: {messages: []}\n  expectations: [{type: groundedness, min_score: 0.8}]', 'single_row_required'],
    ['- id: case-1\n  name: First\n  extra: true\n  input: {messages: []}\n  expectations: [{type: groundedness, min_score: 0.8}]', 'unknown_property'],
  ])('rejects unsupported root/row shapes with safe path errors', (text, code) => {
    const parsed = yamlToForm(text);
    expect(parsed.valid).toBe(false);
    expect(parsed.draft).toBeNull();
    expect(parsed.errors[0]?.code).toBe(code);
    expect(parsed.errors[0]?.message).not.toMatch(/YAMLException|at line|stack/i);
  });

  it('rejects duplicate keys and malformed rows without leaking parser details', () => {
    const parsed = yamlToForm(`
- id: case-1
  id: duplicate
  name: Greeting
  input:
    messages:
      - role: user
        content: Hello
  expectations:
    - type: contains_text
      text: Hello
      case_insensitive: true
`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors).toEqual([
      expect.objectContaining({ code: 'invalid_yaml', path: 'rows' }),
    ]);
  });

  it('rejects explicitly unsupported YAML versions', () => {
    const parsed = yamlToForm(`%YAML 1.1
---
- id: case-1
  name: Greeting
  input: {messages: [{role: user, content: Hello}]}
  expectations: [{type: contains_text, text: Hello}]`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors).toEqual([
      {
        code: 'unsupported_version',
        path: 'rows',
        message: 'Only YAML version 1.2 is supported by the test editor.',
      },
    ]);
  });

  it('rejects anchors and aliases before materializing YAML', () => {
    const parsed = yamlToForm(`- id: case-1
  name: Greeting
  input: &input
    self: *input
    messages: [{role: user, content: Hello}]
  expectations: [{type: contains_text, text: Hello}]`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors).toEqual([
      {
        code: 'unsupported_value',
        path: 'rows',
        message: 'YAML anchors and aliases are not supported',
      },
    ]);
  });

  it('rejects explicit tags before YAML values are materialized', () => {
    const parsed = yamlToForm(`- id: case-1
  name: !!str Greeting
  input:
    messages:
      - role: user
        content: Hello
  expectations:
    - type: contains_text
      text: Hello`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors).toEqual([
      {
        code: 'unsupported_value',
        path: 'rows',
        message: 'YAML tags are not supported',
      },
    ]);
  });

  it('enforces the YAML nesting limit before converting to JavaScript', () => {
    let nested = 'true';
    for (let index = 0; index < 40; index += 1) {
      nested = `{next: ${nested}}`;
    }
    const parsed = yamlToForm(`${minimalYaml().replace(
      '    messages:',
      `    metadata: ${nested}\n    messages:`,
    )}`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors).toEqual([
      {
        code: 'too_large',
        path: 'rows',
        message: 'YAML nesting exceeds the maximum depth',
      },
    ]);
  });

  it('accepts the exact YAML scalar limit and rejects the next character', () => {
    const exact = yamlToForm(minimalYaml('x'.repeat(16_384)));
    expect(exact.valid).toBe(true);

    const over = yamlToForm(minimalYaml('x'.repeat(16_385)));
    expect(over.valid).toBe(false);
    expect(over.errors).toEqual([
      {
        code: 'too_large',
        path: 'rows',
        message: 'YAML scalar exceeds the maximum length',
      },
    ]);
  });

  it('rejects non-finite YAML numbers before JSON conversion', () => {
    const parsed = yamlToForm(`- id: case-1
  name: Greeting
  input:
    temperature: .nan
    messages: [{role: user, content: Hello}]
  expectations: [{type: contains_text, text: Hello}]`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors).toEqual([
      {
        code: 'invalid_number',
        path: 'rows[0].input.temperature',
        message: 'Numbers must be finite; replace this value before saving the test.',
      },
    ]);
  });

  it('rejects non-finite values from a form draft without silently writing null', () => {
    const written = formToYaml({
      ...draft,
      inputMetadata: { temperature: Number.POSITIVE_INFINITY },
    });
    expect(written).toEqual({
      valid: false,
      yaml: null,
      errors: [{
        code: 'invalid_number',
        path: 'rows[0].input.temperature',
        message: 'Numbers must be finite; replace this value before saving the test.',
      }],
    });
  });

  it('rejects non-finite expectation numbers with an expectation path', () => {
    const parsed = yamlToForm(`- id: case-1
  name: Greeting
  input:
    messages: [{role: user, content: Hello}]
  expectations:
    - type: groundedness
      min_score: .inf`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors[0]).toEqual({
      code: 'invalid_number',
      path: 'rows[0].expectations[0].min_score',
      message: 'Numbers must be finite; replace this value before saving the test.',
    });
  });

  it('maps nested input and expectation validation to safe row paths', () => {
    const parsed = yamlToForm(`
- id: case-1
  name: Greeting
  input:
    temperature: 0.5
    messages:
      - role: user
        content: ''
        unsupported: true
  expectations:
    - type: groundedness
      min_score: 2
`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors.map((error) => error.path)).toEqual(expect.arrayContaining([
      'rows[0].input.messages[0].unsupported',
      'rows[0].input.messages[0].content',
      'rows[0].expectations[0].min_score',
    ]));
  });

  it('rejects a document above the bounded size before parsing', () => {
    const parsed = yamlToForm(`- id: case-1\n  name: ${'a'.repeat(1_048_576)}`);
    expect(parsed.valid).toBe(false);
    expect(parsed.errors[0]).toEqual({
      code: 'resource_limit',
      path: 'rows',
      message: 'YAML document exceeds the 1 MiB limit.',
    });
  });
});
