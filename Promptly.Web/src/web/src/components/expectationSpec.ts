import { parseBoundedJson, type StrictJsonIssue } from './strictJson';

export const EXPECTATION_TYPES = [
  'contains_text',
  'banned_text',
  'regex_match',
  'link_pattern',
  'tool_called',
  'tool_sequence',
  'llm_judge',
  'groundedness',
] as const;

export type ExpectationType = (typeof EXPECTATION_TYPES)[number];

export const MAX_EXPECTATION_COUNT = 100;
const MAX_PATTERN_LENGTH = 512;

/**
 * An expectation is intentionally open ended at this UI boundary. The server
 * remains authoritative for its persisted contract and may add fields that a
 * client from an older version does not understand. Keeping the index open
 * lets editing preserve those fields instead of silently dropping them.
 */
export interface ExpectationDraft {
  type: string;
  [key: string]: unknown;
}

export type ExpectationSpecIssueCode =
  | 'invalid_json'
  | 'duplicate_property'
  | 'too_large'
  | 'invalid_number'
  | 'invalid_shape'
  | 'invalid_type'
  | 'unknown_type'
  | 'unknown_property'
  | 'required'
  | 'invalid_boolean'
  | 'invalid_score'
  | 'invalid_sequence'
  | 'pattern_too_long';

export interface ExpectationSpecIssue {
  code: ExpectationSpecIssueCode;
  path: string;
  message: string;
}

export interface ExpectationParseResult {
  originalText: string;
  valid: boolean;
  expectations: ExpectationDraft[] | null;
  errors: ExpectationSpecIssue[];
}

export interface ExpectationSerializationResult {
  valid: boolean;
  json: string | null;
  errors: ExpectationSpecIssue[];
}

const isRecord = (value: unknown): value is Record<string, unknown> => (
  typeof value === 'object' && value !== null && !Array.isArray(value)
);

export const isExpectationType = (value: unknown): value is ExpectationType => (
  typeof value === 'string'
  && (EXPECTATION_TYPES as readonly string[]).includes(value)
);

const issue = (
  code: ExpectationSpecIssueCode,
  path: string,
  message: string,
): ExpectationSpecIssue => ({ code, path, message });

const mapStrictJsonIssue = (
  strictIssue: StrictJsonIssue,
  rootPath = 'expectationsJson',
): ExpectationSpecIssue => issue(
  strictIssue.code,
  strictIssue.path.replace(/^inputSpecJson/, rootPath),
  strictIssue.message.replaceAll('Input JSON', 'Expectation JSON'),
);

/** New rows always carry the evaluator's explicit, authoritative defaults. */
export const createExpectation = (type: ExpectationType): ExpectationDraft => {
  switch (type) {
    case 'contains_text':
    case 'banned_text':
      return { type, text: '', case_insensitive: true };
    case 'regex_match':
      return { type, pattern: '', case_insensitive: true };
    case 'link_pattern':
      return { type, pattern: '' };
    case 'tool_called':
      return { type, tool_name: '' };
    case 'tool_sequence':
      return { type, sequence: [''], exact_sequence: true };
    case 'llm_judge':
      return { type, rubric: '', min_score: 0.8 };
    case 'groundedness':
      return { type, min_score: 0.8 };
  }
};

const allowedProperties: Record<ExpectationType, readonly string[]> = {
  contains_text: ['type', 'text', 'case_insensitive'],
  banned_text: ['type', 'text', 'case_insensitive'],
  regex_match: ['type', 'pattern', 'case_insensitive'],
  link_pattern: ['type', 'pattern'],
  tool_called: ['type', 'tool_name'],
  tool_sequence: ['type', 'sequence', 'exact_sequence'],
  llm_judge: ['type', 'rubric', 'min_score', 'model', 'provider'],
  groundedness: ['type', 'min_score', 'model', 'provider'],
};

const requireText = (
  value: unknown,
  path: string,
  label: string,
  errors: ExpectationSpecIssue[],
) => {
  if (typeof value !== 'string') {
    errors.push(issue('invalid_type', path, `${label} must be text.`));
  } else if (value.trim().length === 0) {
    errors.push(issue('required', path, `${label} is required.`));
  }
};

const requirePattern = (
  value: unknown,
  path: string,
  label: string,
  errors: ExpectationSpecIssue[],
) => {
  requireText(value, path, label, errors);
  if (typeof value === 'string' && value.length > MAX_PATTERN_LENGTH) {
    errors.push(issue(
      'pattern_too_long',
      path,
      'Pattern must be 512 characters or fewer.',
    ));
  }
};

const validateBoolean = (
  value: unknown,
  path: string,
  label: string,
  errors: ExpectationSpecIssue[],
) => {
  // Older saved specs may omit optional/defaultable flags. New rows emitted by
  // createExpectation always include them explicitly.
  if (value !== undefined && typeof value !== 'boolean') {
    errors.push(issue('invalid_boolean', path, `${label} must be true or false.`));
  }
};

const validateScore = (
  value: unknown,
  path: string,
  errors: ExpectationSpecIssue[],
) => {
  if (typeof value === 'string' && value.trim().length === 0) {
    errors.push(issue('required', path, 'Score is required.'));
    return;
  }
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1) {
    errors.push(issue('invalid_score', path, 'Score must be a finite number between 0 and 1.'));
  }
};

const validateAllowedProperties = (
  expectation: Record<string, unknown>,
  type: ExpectationType,
  path: string,
  errors: ExpectationSpecIssue[],
) => {
  const allowed = allowedProperties[type];
  Object.keys(expectation).forEach((property) => {
    if (!allowed.includes(property)) {
      errors.push(issue(
        'unknown_property',
        `${path}.${property}`,
        `Property "${property}" is not supported for ${type}.`,
      ));
    }
  });
};

const validateExpectation = (
  value: unknown,
  index: number,
  errors: ExpectationSpecIssue[],
) => {
  const path = `expectations[${index}]`;
  if (!isRecord(value)) {
    errors.push(issue('invalid_shape', path, 'Each expectation must be an object.'));
    return;
  }

  if (!isExpectationType(value.type)) {
    errors.push(issue(
      typeof value.type === 'string' ? 'unknown_type' : 'invalid_type',
      `${path}.type`,
      'Choose one of the supported expectation types.',
    ));
    return;
  }

  validateAllowedProperties(value, value.type, path, errors);

  switch (value.type) {
    case 'contains_text':
    case 'banned_text':
      requireText(value.text, `${path}.text`, 'Text', errors);
      validateBoolean(value.case_insensitive, `${path}.case_insensitive`, 'Case-insensitive flag', errors);
      break;
    case 'regex_match':
      requirePattern(value.pattern, `${path}.pattern`, 'Pattern', errors);
      // Do not instantiate RegExp here. Patterns are .NET syntax and the
      // server's bounded engine is the authority for syntax and capabilities.
      validateBoolean(value.case_insensitive, `${path}.case_insensitive`, 'Case-insensitive flag', errors);
      break;
    case 'link_pattern':
      requirePattern(value.pattern, `${path}.pattern`, 'Link pattern', errors);
      break;
    case 'tool_called':
      requireText(value.tool_name, `${path}.tool_name`, 'Tool name', errors);
      break;
    case 'tool_sequence':
      if (!Array.isArray(value.sequence) || value.sequence.length === 0) {
        errors.push(issue(
          'invalid_sequence',
          `${path}.sequence`,
          'Add at least one tool to the sequence.',
        ));
      } else {
        value.sequence.forEach((tool, toolIndex) => {
          requireText(
            tool,
            `${path}.sequence[${toolIndex}]`,
            'Tool name',
            errors,
          );
        });
      }
      validateBoolean(value.exact_sequence, `${path}.exact_sequence`, 'Exact-sequence flag', errors);
      break;
    case 'llm_judge':
      requireText(value.rubric, `${path}.rubric`, 'Rubric', errors);
      if (value.min_score !== undefined) {
        validateScore(value.min_score, `${path}.min_score`, errors);
      }
      break;
    case 'groundedness':
      if (value.min_score !== undefined) {
        validateScore(value.min_score, `${path}.min_score`, errors);
      }
      break;
  }

  ['model', 'provider'].forEach((property) => {
    const metadata = value[property];
    if (metadata === undefined || metadata === null) {
      return;
    }
    if (typeof metadata !== 'string') {
      errors.push(issue(
        'invalid_type',
        `${path}.${property}`,
        `${property} must be text when present.`,
      ));
    } else if (metadata.trim().length === 0) {
      errors.push(issue(
        'required',
        `${path}.${property}`,
        `${property} must be a nonblank string when present.`,
      ));
    }
  });
};

export const validateExpectations = (expectations: readonly unknown[]): ExpectationSpecIssue[] => {
  const errors: ExpectationSpecIssue[] = [];
  if (expectations.length === 0) {
    errors.push(issue(
      'required',
      'expectations',
      'Add at least one expectation before saving this test.',
    ));
  }
  if (expectations.length > MAX_EXPECTATION_COUNT) {
    errors.push(issue(
      'too_large',
      'expectations',
      'No more than 100 expectations are allowed.',
    ));
  }
  expectations.forEach((expectation, index) => validateExpectation(expectation, index, errors));
  return errors;
};

export const parseExpectationsJson = (originalText: string): ExpectationParseResult => {
  const bounded = parseBoundedJson(originalText);
  if (bounded.issue) {
    return {
      originalText,
      valid: false,
      expectations: null,
      errors: [mapStrictJsonIssue(bounded.issue)],
    };
  }
  const parsed = bounded.value;

  if (!Array.isArray(parsed)) {
    return {
      originalText,
      valid: false,
      expectations: null,
      errors: [issue(
        'invalid_shape',
        'expectationsJson',
        'Expectations must be a JSON array.',
      )],
    };
  }

  const errors = validateExpectations(parsed);
  return errors.length > 0
    ? { originalText, valid: false, expectations: null, errors }
    : { originalText, valid: true, expectations: parsed as ExpectationDraft[], errors: [] };
};

/**
 * Serialize only an explicitly valid, non-empty list. Object spread is left
 * to callers editing a row, so unknown persisted fields survive round trips.
 */
export const serializeExpectations = (
  expectations: readonly ExpectationDraft[],
): ExpectationSerializationResult => {
  const errors = validateExpectations(expectations);
  if (errors.length > 0) {
    return { valid: false, json: null, errors };
  }

  let json: string | undefined;
  try {
    json = JSON.stringify(expectations);
  } catch {
    return {
      valid: false,
      json: null,
      errors: [issue(
        'invalid_json',
        'expectations',
        'Expectations contain values that cannot be serialized.',
      )],
    };
  }
  if (typeof json !== 'string') {
    return {
      valid: false,
      json: null,
      errors: [issue(
        'invalid_json',
        'expectations',
        'Expectations contain values that cannot be serialized.',
      )],
    };
  }

  // Reuse the same strict preflight as imported JSON so form-created output
  // cannot bypass the byte, depth, scalar, and finite-number bounds.
  const bounded = parseBoundedJson(json);
  if (bounded.issue) {
    return { valid: false, json: null, errors: [mapStrictJsonIssue(bounded.issue, 'expectations')] };
  }
  return { valid: true, json, errors: [] };
};
