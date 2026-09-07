import { describe, expect, it } from 'vitest';
import {
  createExpectation,
  EXPECTATION_TYPES,
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

  it('accepts a 512-character pattern and rejects a 513-character pattern', () => {
    const accepted = { type: 'regex_match', pattern: 'a'.repeat(512), case_insensitive: true };
    const rejected = { type: 'regex_match', pattern: 'a'.repeat(513), case_insensitive: true };
    expect(validateExpectations([accepted])).toEqual([]);
    expect(validateExpectations([rejected])).toEqual([
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
});
