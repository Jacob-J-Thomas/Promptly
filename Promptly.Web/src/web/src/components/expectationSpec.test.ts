import { describe, expect, it } from 'vitest';
import {
  MAX_JSON_BYTES,
  MAX_JSON_DEPTH,
  MAX_JSON_SCALAR_LENGTH,
} from './strictJson';
import {
  createExpectation,
  EXPECTATION_TYPES,
  MAX_EXPECTATION_COUNT,
  parseExpectationsJson,
  serializeExpectations,
  validateExpectations,
  type ExpectationDraft,
} from './expectationSpec';

const validExpectations: ExpectationDraft[] = [
  { type: 'contains_text', text: 'ready', case_insensitive: true },
  { type: 'banned_text', text: 'failure', case_insensitive: false },
  { type: 'regex_match', pattern: String.raw`(?<name>\p{L}+)`, case_insensitive: true },
  { type: 'link_pattern', pattern: 'https://example.test/[id]' },
  { type: 'tool_called', tool_name: 'search' },
  { type: 'tool_sequence', sequence: ['search', 'summarize'], exact_sequence: false },
  { type: 'llm_judge', rubric: 'Answer the question', min_score: 0, model: 'judge-a', provider: 'local' },
  { type: 'groundedness', min_score: 1, model: 'ground-a', provider: 'local' },
];

describe('expectation spec helpers', () => {
  it('creates all supported types with explicit evaluator defaults', () => {
    expect(EXPECTATION_TYPES).toHaveLength(8);
    expect(createExpectation('contains_text')).toEqual({
      type: 'contains_text', text: '', case_insensitive: true,
    });
    expect(createExpectation('banned_text')).toEqual({
      type: 'banned_text', text: '', case_insensitive: true,
    });
    expect(createExpectation('regex_match')).toEqual({
      type: 'regex_match', pattern: '', case_insensitive: true,
    });
    expect(createExpectation('link_pattern')).toEqual({ type: 'link_pattern', pattern: '' });
    expect(createExpectation('tool_called')).toEqual({ type: 'tool_called', tool_name: '' });
    expect(createExpectation('tool_sequence')).toEqual({
      type: 'tool_sequence', sequence: [''], exact_sequence: true,
    });
    expect(createExpectation('llm_judge')).toEqual({ type: 'llm_judge', rubric: '', min_score: 0.8 });
    expect(createExpectation('groundedness')).toEqual({ type: 'groundedness', min_score: 0.8 });
  });

  it('accepts every type, boundaries, explicit ordered subsequence, and optional metadata', () => {
    expect(validateExpectations(validExpectations)).toEqual([]);
    const parsed = parseExpectationsJson(JSON.stringify(validExpectations));
    expect(parsed.valid).toBe(true);
    expect(parsed.expectations).toEqual(validExpectations);
  });

  it('preserves exact pattern text without applying an incompatible JavaScript regex parser', () => {
    const pattern = String.raw`(?<capture>\p{L}{1,3})\k<capture>`;
    const expectation: ExpectationDraft = {
      type: 'regex_match', pattern, case_insensitive: false,
    };
    const result = serializeExpectations([expectation]);
    expect(result.valid).toBe(true);
    expect(JSON.parse(result.json!)).toEqual([expectation]);
  });

  it('accepts 512-character patterns and rejects 513-character patterns', () => {
    const accepted = { type: 'regex_match', pattern: 'a'.repeat(512), case_insensitive: true };
    const rejected = { type: 'regex_match', pattern: 'a'.repeat(513), case_insensitive: true };
    expect(validateExpectations([accepted])).toEqual([]);
    expect(validateExpectations([rejected])).toEqual([
      expect.objectContaining({ code: 'pattern_too_long', path: 'expectations[0].pattern' }),
    ]);

    const acceptedLink = { type: 'link_pattern', pattern: 'a'.repeat(512) };
    const rejectedLink = { type: 'link_pattern', pattern: 'a'.repeat(513) };
    expect(validateExpectations([acceptedLink])).toEqual([]);
    expect(validateExpectations([rejectedLink])).toEqual([
      expect.objectContaining({ code: 'pattern_too_long', path: 'expectations[0].pattern' }),
    ]);
  });

  it.each([0, 1])('accepts score boundary %s', (score) => {
    expect(validateExpectations([{ type: 'groundedness', min_score: score }])).toEqual([]);
    expect(validateExpectations([{ type: 'llm_judge', rubric: 'clear', min_score: score }])).toEqual([]);
  });

  it.each([
    ['', 'required'],
    [null, 'invalid_score'],
    ['0.5', 'invalid_score'],
    [Number.NaN, 'invalid_score'],
    [Number.POSITIVE_INFINITY, 'invalid_score'],
    [-0.01, 'invalid_score'],
    [1.01, 'invalid_score'],
  ] as const)('rejects invalid score %s', (score, code) => {
    const errors = validateExpectations([{ type: 'groundedness', min_score: score }]);
    expect(errors[0]?.code).toBe(code);
  });

  it('requires meaningful strings and tool sequence items', () => {
    const errors = validateExpectations([
      { type: 'contains_text', text: '  ', case_insensitive: true },
      { type: 'tool_called', tool_name: '' },
      { type: 'tool_sequence', sequence: ['search', '  '], exact_sequence: false },
      { type: 'llm_judge', rubric: ' ', min_score: 0.8 },
    ]);
    expect(errors.map((error) => error.path)).toEqual([
      'expectations[0].text',
      'expectations[1].tool_name',
      'expectations[2].sequence[1]',
      'expectations[3].rubric',
    ]);
  });

  it('rejects unsupported properties, types, flags, and malformed sequence shapes', () => {
    const errors = validateExpectations([
      { type: 'contains_text', text: 'ok', case_insensitive: 'yes' },
      { type: 'tool_sequence', sequence: [], exact_sequence: true, unexpected: true },
      { type: 'groundedness', min_score: 0.5, case_insensitive: true },
      { type: 'future_expectation', value: true },
      { type: 'tool_called', tool_name: 42 },
    ]);
    expect(errors.map((error) => error.code)).toEqual(expect.arrayContaining([
      'invalid_boolean', 'invalid_sequence', 'unknown_property', 'unknown_type', 'invalid_type',
    ]));
  });

  it('allows omitted or null AI metadata and rejects blank metadata without trimming it', () => {
    expect(validateExpectations([
      { type: 'groundedness', min_score: 0.8, model: undefined, provider: null },
      { type: 'llm_judge', rubric: 'clear', model: ' stored-model ', provider: 'stored-provider' },
    ])).toEqual([]);

    const errors = validateExpectations([
      { type: 'groundedness', model: '   ', provider: '\t' },
      { type: 'llm_judge', rubric: 'clear', model: 42, provider: {} },
    ]);
    expect(errors).toEqual(expect.arrayContaining([
      expect.objectContaining({ code: 'required', path: 'expectations[0].model' }),
      expect.objectContaining({ code: 'required', path: 'expectations[0].provider' }),
      expect.objectContaining({ code: 'invalid_type', path: 'expectations[1].model' }),
      expect.objectContaining({ code: 'invalid_type', path: 'expectations[1].provider' }),
    ]));
  });

  it.each([
    'not-json',
    '{}',
    'null',
    '[]',
    '[{"type":"contains_text","text":""}]',
  ])('retains malformed legacy text and never falls back to an empty list for %s', (text) => {
    const parsed = parseExpectationsJson(text);
    expect(parsed.valid).toBe(false);
    expect(parsed.expectations).toBeNull();
    expect(parsed.originalText).toBe(text);
    expect(parsed.errors.length).toBeGreaterThan(0);
  });

  it('rejects duplicate expectation properties before normalization and translates safe paths', () => {
    const text = '[{"type":"contains_text","text":"old","\\u0074ext":"new"}]';
    const parsed = parseExpectationsJson(text);

    expect(parsed).toEqual({
      originalText: text,
      valid: false,
      expectations: null,
      errors: [{
        code: 'duplicate_property',
        path: 'expectationsJson[0].text',
        message: 'Duplicate JSON property is not allowed.',
      }],
    });
  });

  it('rejects expectation JSON at the exact byte, depth, scalar, and numeric boundaries', () => {
    const validBase = '[{"type":"contains_text","text":"x"}]';
    const padding = ' '.repeat(MAX_JSON_BYTES - new TextEncoder().encode(validBase).length);
    expect(parseExpectationsJson(validBase + padding).valid).toBe(true);

    const oversized = parseExpectationsJson(`${validBase}${padding} `);
    expect(oversized.valid).toBe(false);
    expect(oversized.expectations).toBeNull();
    expect(oversized.errors[0]).toEqual({
      code: 'too_large',
      path: 'expectationsJson',
      message: 'Expectation JSON exceeds the 262144-byte limit.',
    });

    let exactDepth = 'null';
    for (let index = 0; index < MAX_JSON_DEPTH - 1; index += 1) {
      exactDepth = `{"nested":${exactDepth}}`;
    }
    expect(parseExpectationsJson(`[${exactDepth}]`).errors[0]?.code).not.toBe('too_large');

    const tooDeep = parseExpectationsJson(`[${JSON.stringify({ nested: JSON.parse(exactDepth) })}]`);
    expect(tooDeep.valid).toBe(false);
    expect(tooDeep.expectations).toBeNull();
    expect(tooDeep.errors[0]?.code).toBe('too_large');
    expect(tooDeep.errors[0]?.path).toBe(`expectationsJson[0]${'.nested'.repeat(MAX_JSON_DEPTH)}`);

    const exactScalar = `[${JSON.stringify({
      type: 'contains_text',
      text: 'x'.repeat(MAX_JSON_SCALAR_LENGTH),
    })}]`;
    expect(parseExpectationsJson(exactScalar).valid).toBe(true);

    const tooLong = parseExpectationsJson(`[${JSON.stringify({
      type: 'contains_text',
      text: `${'x'.repeat(MAX_JSON_SCALAR_LENGTH)}x`,
    })}]`);
    expect(tooLong.errors[0]?.code).toBe('too_large');
    expect(tooLong.errors[0]?.path).toBe('expectationsJson[0].text');

    const invalidNumber = parseExpectationsJson('[{"type":"groundedness","min_score":1e400}]');
    expect(invalidNumber.errors[0]).toEqual({
      code: 'invalid_number',
      path: 'expectationsJson[0].min_score',
      message: 'JSON numbers must be finite.',
    });
  });

  it('accepts at most 100 expectations and rejects the 101st before serialization', () => {
    const atLimit = Array.from({ length: MAX_EXPECTATION_COUNT }, () => ({
      type: 'contains_text', text: 'ok', case_insensitive: true,
    }));
    const overLimit = [...atLimit, { type: 'contains_text', text: 'too many', case_insensitive: true }];

    expect(parseExpectationsJson(JSON.stringify(atLimit)).valid).toBe(true);
    expect(parseExpectationsJson(JSON.stringify(overLimit))).toMatchObject({
      valid: false,
      expectations: null,
      errors: [expect.objectContaining({ code: 'too_large', path: 'expectations' })],
    });
    expect(serializeExpectations(atLimit).valid).toBe(true);
    expect(serializeExpectations(overLimit)).toMatchObject({
      valid: false,
      json: null,
      errors: [expect.objectContaining({ code: 'too_large', path: 'expectations' })],
    });
  });

  it('serializes only valid non-empty lists and does not mutate inputs', () => {
    const original = validExpectations.map((expectation) => ({
      ...expectation,
      ...(Array.isArray(expectation.sequence) ? { sequence: [...expectation.sequence] } : {}),
    }));
    const result = serializeExpectations(original);
    expect(result).toMatchObject({ valid: true, errors: [] });
    expect(JSON.parse(result.json!)).toEqual(original);
    expect(original).toEqual(validExpectations);

    const invalid = serializeExpectations([]);
    expect(invalid.valid).toBe(false);
    expect(invalid.json).toBeNull();
    expect(invalid.errors[0]?.path).toBe('expectations');
  });

  it('applies byte and scalar bounds to form-created output and reports cycles safely', () => {
    const tooLong = serializeExpectations([{
      type: 'contains_text', text: 'x'.repeat(MAX_JSON_SCALAR_LENGTH + 1), case_insensitive: true,
    }]);
    expect(tooLong).toMatchObject({
      valid: false,
      json: null,
      errors: [expect.objectContaining({ code: 'too_large', path: 'expectations[0].text' })],
    });

    const tooManyBytes = Array.from({ length: MAX_EXPECTATION_COUNT }, () => ({
      type: 'contains_text', text: 'x'.repeat(2_600), case_insensitive: true,
    }));
    expect(serializeExpectations(tooManyBytes)).toMatchObject({
      valid: false,
      json: null,
      errors: [expect.objectContaining({ code: 'too_large', path: 'expectations' })],
    });

    let tooDeep: unknown = null;
    for (let index = 0; index <= 32; index += 1) {
      tooDeep = { nested: tooDeep };
    }
    const deepExpectation: ExpectationDraft = {
      type: 'contains_text', text: 'ok', case_insensitive: true,
    };
    Object.defineProperty(deepExpectation, 'toJSON', { value: () => tooDeep });
    expect(serializeExpectations([deepExpectation])).toMatchObject({
      valid: false,
      json: null,
      errors: [expect.objectContaining({ code: 'too_large' })],
    });

    const cycle: Record<string, unknown> = {};
    cycle.self = cycle;
    const cyclicExpectation: ExpectationDraft = {
      type: 'contains_text', text: 'ok', case_insensitive: true,
    };
    Object.defineProperty(cyclicExpectation, 'toJSON', { value: () => cycle });
    expect(serializeExpectations([cyclicExpectation])).toMatchObject({
      valid: false,
      json: null,
      errors: [expect.objectContaining({ code: 'invalid_json', path: 'expectations' })],
    });
  });
});
