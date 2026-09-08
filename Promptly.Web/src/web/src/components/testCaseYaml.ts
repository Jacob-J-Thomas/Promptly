import {
  isAlias,
  isCollection,
  isNode,
  isScalar,
  parseDocument,
  stringify,
  visit,
  type Scalar,
  type ScalarTag,
} from 'yaml';
import {
  parseInputSpecJson,
  type ConversationMessage,
  type MessageSpecIssue,
} from './messageSpec';
import {
  parseExpectationsJson,
  type ExpectationDraft,
  type ExpectationSpecIssue,
} from './expectationSpec';
import {
  isRawJsonNumber,
  normalizeYamlNumberToken,
  RawJsonNumber,
  shouldPreserveRawJsonNumber,
  stringifyJsonWithRawNumbers,
} from './strictJson';

export interface TestCaseYamlDraft {
  externalId: string;
  name: string;
  description: string;
  inputMetadata: Record<string, unknown>;
  messages: ConversationMessage[];
  expectations: ExpectationDraft[];
}

export interface YamlValidationIssue {
  code: string;
  path: string;
  message: string;
}

export interface YamlConversionResult {
  valid: boolean;
  yaml: string | null;
  errors: YamlValidationIssue[];
}

export interface TestCaseYamlParseResult {
  valid: boolean;
  draft: TestCaseYamlDraft | null;
  errors: YamlValidationIssue[];
}

const MAX_YAML_BYTES = 1_048_576;
const MAX_YAML_DEPTH = 32;
const MAX_YAML_SCALAR_LENGTH = 16_384;

const rawJsonNumberTag: ScalarTag = {
  default: true,
  identify: isRawJsonNumber,
  resolve: (value) => value,
  tag: 'tag:yaml.org,2002:float',
  stringify: (node: Scalar<unknown>) => (node.value as RawJsonNumber).raw,
};

const isRecord = (value: unknown): value is Record<string, unknown> => (
  typeof value === 'object' && value !== null && !Array.isArray(value)
);

const yamlIssue = (code: string, path: string, message: string): YamlValidationIssue => ({
  code,
  path,
  message,
});

const findNonFiniteNumber = (
  value: unknown,
  path: string,
  seen = new WeakSet<object>(),
): YamlValidationIssue | null => {
  if (typeof value === 'number' && !Number.isFinite(value)) {
    return yamlIssue(
      'invalid_number',
      path,
      'Numbers must be finite; replace this value before saving the test.',
    );
  }
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  if (seen.has(value)) {
    return null;
  }
  seen.add(value);
  if (Array.isArray(value)) {
    for (const [index, item] of value.entries()) {
      const issue = findNonFiniteNumber(item, `${path}[${index}]`, seen);
      if (issue) {
        return issue;
      }
    }
    return null;
  }
  for (const [property, item] of Object.entries(value)) {
    const issue = findNonFiniteNumber(item, `${path}.${property}`, seen);
    if (issue) {
      return issue;
    }
  }
  return null;
};

const mapInputIssue = (error: MessageSpecIssue): YamlValidationIssue => yamlIssue(
  error.code,
  error.path.replace(/^inputSpecJson/, 'rows[0].input'),
  error.message,
);

const mapExpectationIssue = (error: ExpectationSpecIssue): YamlValidationIssue => yamlIssue(
  error.code,
  error.path
    .replace(/^expectationsJson/, 'rows[0].expectations')
    .replace(/^expectations/, 'rows[0].expectations'),
  error.message,
);

const preflightYamlDocument = (document: ReturnType<typeof parseDocument>): YamlValidationIssue[] => {
  const tagDirectives = document.directives?.tags;
  if (tagDirectives && Object.keys(tagDirectives).some((handle) => handle !== '!!')) {
    return [yamlIssue('unsupported_value', 'rows', 'YAML tags are not supported')];
  }

  let issue: YamlValidationIssue | null = null;
  visit(document, {
    Node(_key, node, path) {
      const nodeMetadata = node as { anchor?: unknown; tag?: unknown };
      if (isAlias(node) || nodeMetadata.anchor !== undefined) {
        issue ??= yamlIssue('unsupported_value', 'rows', 'YAML anchors and aliases are not supported');
      }
      if (typeof nodeMetadata.tag === 'string' && nodeMetadata.tag.length > 0) {
        issue ??= yamlIssue('unsupported_value', 'rows', 'YAML tags are not supported');
      }

      const depth = path.reduce(
        (count, ancestor) => count + (isNode(ancestor) && isCollection(ancestor) ? 1 : 0),
        0,
      );
      if (depth > MAX_YAML_DEPTH) {
        issue ??= yamlIssue('too_large', 'rows', 'YAML nesting exceeds the maximum depth');
      }

      if (isScalar(node)) {
        const scalar = node as { value: unknown; source?: unknown };
        const length = typeof scalar.value === 'string'
          ? scalar.value.length
          : typeof scalar.source === 'string' ? scalar.source.length : 0;
        if (length > MAX_YAML_SCALAR_LENGTH) {
          issue ??= yamlIssue('too_large', 'rows', 'YAML scalar exceeds the maximum length');
        }
      }
      return undefined;
    },
  });

  return issue ? [issue] : [];
};

const parseYamlValue = (text: string): { value: unknown; errors: YamlValidationIssue[] } => {
  if (new TextEncoder().encode(text).length > MAX_YAML_BYTES) {
    return {
      value: null,
      errors: [yamlIssue('resource_limit', 'rows', 'YAML document exceeds the 1 MiB limit.')],
    };
  }

  try {
    const document = parseDocument(text, {
      strict: true,
      stringKeys: true,
      uniqueKeys: true,
    });
    if (document.errors.length > 0) {
      return {
        value: null,
        errors: [yamlIssue('invalid_yaml', 'rows', 'YAML is malformed. Repair the document before saving.')],
      };
    }
    if (document.directives?.yaml.explicit && document.directives.yaml.version !== '1.2') {
      return {
        value: null,
        errors: [yamlIssue(
          'unsupported_version',
          'rows',
          'Only YAML version 1.2 is supported by the test editor.',
        )],
      };
    }
    const preflightErrors = preflightYamlDocument(document);
    if (preflightErrors.length > 0) {
      return { value: null, errors: preflightErrors };
    }
    // YAML's default number resolver also materializes wide integers and long
    // decimals as JavaScript numbers. Replace only lossy scalar nodes before
    // conversion so their original numeric token reaches the JSON boundary.
    visit(document, {
      Node(_key, node) {
        if (!isScalar(node)) {
          return undefined;
        }
        const scalar = node as Scalar<unknown> & { source?: unknown };
        if (typeof scalar.source === 'string'
          && typeof scalar.value === 'number'
          && Number.isFinite(scalar.value)) {
          const normalized = normalizeYamlNumberToken(scalar.source);
          if (normalized && shouldPreserveRawJsonNumber(normalized, scalar.value)) {
            scalar.value = new RawJsonNumber(normalized);
          }
        }
        return undefined;
      },
    });
    return { value: document.toJS({ mapAsMap: false, maxAliasCount: 100 }), errors: [] };
  } catch {
    return {
      value: null,
      errors: [yamlIssue('invalid_yaml', 'rows', 'YAML could not be read. Repair the document before saving.')],
    };
  }
};

const checkOnlyKeys = (
  value: Record<string, unknown>,
  allowed: readonly string[],
  path: string,
): YamlValidationIssue[] => Object.keys(value)
  .filter((key) => !allowed.includes(key))
  .map((key) => yamlIssue(
    'unknown_property',
    `${path}.${key}`,
    `Property "${key}" is not supported here.`,
  ));

export const formToYaml = (draft: TestCaseYamlDraft): YamlConversionResult => {
  const row: Record<string, unknown> = {
    id: draft.externalId,
    name: draft.name,
  };
  if (draft.description.trim().length > 0) {
    row.description = draft.description;
  }
  row.input = {
    ...draft.inputMetadata,
    messages: draft.messages.map((message) => ({
      role: message.role,
      content: message.content,
    })),
  };
  row.expectations = draft.expectations.map((expectation) => ({ ...expectation }));

  const nonFiniteInput = findNonFiniteNumber(row.input, 'rows[0].input');
  if (nonFiniteInput) {
    return { valid: false, yaml: null, errors: [nonFiniteInput] };
  }
  const nonFiniteExpectations = findNonFiniteNumber(row.expectations, 'rows[0].expectations');
  if (nonFiniteExpectations) {
    return { valid: false, yaml: null, errors: [nonFiniteExpectations] };
  }

  try {
    return {
      valid: true,
      yaml: stringify([row], {
        customTags: [rawJsonNumberTag],
        sortMapEntries: false,
      }),
      errors: [],
    };
  } catch {
    return {
      valid: false,
      yaml: null,
      errors: [yamlIssue('invalid_yaml', 'rows', 'The current test cannot be represented as YAML.')],
    };
  }
};

export const yamlToForm = (text: string): TestCaseYamlParseResult => {
  const parsed = parseYamlValue(text);
  if (parsed.errors.length > 0) {
    return { valid: false, draft: null, errors: parsed.errors };
  }

  if (!Array.isArray(parsed.value)) {
    return {
      valid: false,
      draft: null,
      errors: [yamlIssue('invalid_shape', 'rows', 'YAML must contain a root sequence of test rows.')],
    };
  }
  if (parsed.value.length !== 1) {
    return {
      valid: false,
      draft: null,
      errors: [yamlIssue('single_row_required', 'rows', 'The editor accepts exactly one test row.')],
    };
  }

  const row = parsed.value[0];
  if (!isRecord(row)) {
    return {
      valid: false,
      draft: null,
      errors: [yamlIssue('invalid_shape', 'rows[0]', 'Each test row must be an object.')],
    };
  }

  const errors = checkOnlyKeys(row, ['id', 'name', 'description', 'input', 'expectations'], 'rows[0]');
  const externalId = row.id;
  const name = row.name;
  if (typeof externalId !== 'string' || externalId.trim().length === 0) {
    errors.push(yamlIssue('required', 'rows[0].id', 'Test id is required.'));
  }
  if (typeof name !== 'string' || name.trim().length === 0) {
    errors.push(yamlIssue('required', 'rows[0].name', 'Test name is required.'));
  }
  if (row.description !== undefined
    && row.description !== null
    && typeof row.description !== 'string') {
    errors.push(yamlIssue(
      'invalid_type',
      'rows[0].description',
      'Description must be text or null when present.',
    ));
  }

  if (!isRecord(row.input)) {
    errors.push(yamlIssue('invalid_shape', 'rows[0].input', 'Input must be an object containing messages.'));
  }
  if (row.expectations === undefined) {
    errors.push(yamlIssue('required', 'rows[0].expectations', 'Expectations are required.'));
  }
  if (errors.length > 0) {
    return { valid: false, draft: null, errors };
  }

  const nonFiniteInput = findNonFiniteNumber(row.input, 'rows[0].input');
  if (nonFiniteInput) {
    return { valid: false, draft: null, errors: [nonFiniteInput] };
  }
  const nonFiniteExpectations = findNonFiniteNumber(row.expectations, 'rows[0].expectations');
  if (nonFiniteExpectations) {
    return { valid: false, draft: null, errors: [nonFiniteExpectations] };
  }

  let inputText: string;
  try {
    inputText = stringifyJsonWithRawNumbers(row.input);
  } catch {
    return {
      valid: false,
      draft: null,
      errors: [yamlIssue(
        'invalid_shape',
        'rows[0].input',
        'Input contains recursive aliases and cannot be edited safely.',
      )],
    };
  }
  let expectationsText: string;
  try {
    expectationsText = stringifyJsonWithRawNumbers(row.expectations);
  } catch {
    return {
      valid: false,
      draft: null,
      errors: [yamlIssue(
        'invalid_shape',
        'rows[0].expectations',
        'Expectations contain recursive aliases and cannot be edited safely.',
      )],
    };
  }

  const inputResult = parseInputSpecJson(inputText);
  const expectationResult = parseExpectationsJson(expectationsText);
  const inputErrors = inputResult.errors.map(mapInputIssue);
  const expectationErrors = expectationResult.errors.map(mapExpectationIssue);
  if (inputErrors.length > 0 || expectationErrors.length > 0) {
    return {
      valid: false,
      draft: null,
      errors: [...inputErrors, ...expectationErrors],
    };
  }

  return {
    valid: true,
    errors: [],
    draft: {
      externalId: externalId as string,
      name: name as string,
      description: typeof row.description === 'string' ? row.description : '',
      inputMetadata: inputResult.metadata,
      messages: inputResult.messages ?? [],
      expectations: expectationResult.expectations ?? [],
    },
  };
};
