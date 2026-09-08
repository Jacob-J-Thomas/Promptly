export const MAX_JSON_BYTES = 262_144;
export const MAX_JSON_DEPTH = 32;
export const MAX_JSON_SCALAR_LENGTH = 16_384;

export type StrictJsonIssueCode =
  | 'invalid_json'
  | 'duplicate_property'
  | 'too_large'
  | 'invalid_number';

export interface StrictJsonIssue {
  code: StrictJsonIssueCode;
  path: string;
  message: string;
}

export interface StrictJsonResult {
  value: unknown | null;
  issue: StrictJsonIssue | null;
}

const strictJsonNumber = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$/;
const minimumNormalNumber = Number.MIN_VALUE * 2 ** 52;

/**
 * A number whose JSON spelling cannot be represented exactly by an IEEE-754
 * number. The wrapper is only used for those values; ordinary JSON numbers
 * keep their normal JavaScript representation for callers.
 */
export class RawJsonNumber {
  readonly raw: string;

  readonly numericValue: number;

  constructor(raw: string) {
    if (!strictJsonNumber.test(raw) || !Number.isFinite(Number(raw))) {
      throw new TypeError('Raw JSON number token is invalid.');
    }
    this.raw = raw;
    this.numericValue = Number(raw);
  }

  /** Generic JSON.stringify remains compatible; exact editor paths use the raw-aware serializer. */
  toJSON(): number {
    return this.numericValue;
  }
}

export const isRawJsonNumber = (value: unknown): value is RawJsonNumber => (
  value instanceof RawJsonNumber
);

/**
 * Keep a raw token when its significant digits exceed the precision normally
 * available to a JavaScript number, or when an integer is outside the safe
 * integer range. This intentionally leaves common values such as 0.5 and
 * 1e100 as ordinary numbers.
 */
export const shouldPreserveRawJsonNumber = (token: string, value: number): boolean => {
  if (!Number.isFinite(value) || !strictJsonNumber.test(token)) {
    return false;
  }
  const nonZeroToken = /[1-9]/.test(token.replace(/[.eE+-]/g, ''));
  if (value === 0 && nonZeroToken) {
    return true;
  }
  if (value !== 0 && Math.abs(value) < minimumNormalNumber) {
    return true;
  }
  if (!token.includes('.') && !/[eE]/.test(token)) {
    return !Number.isSafeInteger(value);
  }

  const significantDigits = token
    .replace(/^[+-]?0+(?=[1-9])/, '')
    .replace(/[.eE+-]/g, '')
    .replace(/^0+/, '')
    .length;
  return significantDigits > 15;
};

/** Normalize YAML core numeric spellings to a finite JSON numeric token. */
export const normalizeYamlNumberToken = (token: string): string | null => {
  let source = token;
  let sign = '';
  if (source.startsWith('-') || source.startsWith('+')) {
    sign = source[0] === '-' ? '-' : '';
    source = source.slice(1);
  }

  if (/^0x[0-9a-fA-F]+$/.test(source)) {
    const decimal = BigInt(`0x${source.slice(2)}`).toString();
    const normalized = `${sign}${decimal}`;
    return Number.isFinite(Number(normalized)) && strictJsonNumber.test(normalized)
      ? normalized
      : null;
  }
  if (/^0o[0-7]+$/.test(source)) {
    const decimal = BigInt(`0o${source.slice(2)}`).toString();
    const normalized = `${sign}${decimal}`;
    return Number.isFinite(Number(normalized)) && strictJsonNumber.test(normalized)
      ? normalized
      : null;
  }

  const exponentIndex = source.search(/[eE]/);
  const mantissa = exponentIndex >= 0 ? source.slice(0, exponentIndex) : source;
  const exponent = exponentIndex >= 0 ? source.slice(exponentIndex) : '';
  if (!/^(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)$/.test(mantissa)
    || (exponent.length > 0 && !/^e[+-]?[0-9]+$/i.test(exponent))) {
    return null;
  }

  const dotIndex = mantissa.indexOf('.');
  let whole = dotIndex >= 0 ? mantissa.slice(0, dotIndex) : mantissa;
  const fraction = dotIndex >= 0 ? mantissa.slice(dotIndex + 1) : '';
  whole = whole.replace(/^0+(?=[0-9])/, '');
  if (whole.length === 0) {
    whole = '0';
  }
  const normalizedMantissa = fraction.length > 0 ? `${whole}.${fraction}` : whole;
  const normalized = `${sign}${normalizedMantissa}${exponent}`;
  return Number.isFinite(Number(normalized)) && strictJsonNumber.test(normalized)
    ? normalized
    : null;
};

const stringifyJsonValue = (
  value: unknown,
  ancestors: Set<object>,
  inArray = false,
): string | undefined => {
  if (isRawJsonNumber(value)) {
    return value.raw;
  }
  if (value === null) {
    return 'null';
  }
  switch (typeof value) {
    case 'string':
      return JSON.stringify(value);
    case 'boolean':
      return value ? 'true' : 'false';
    case 'number':
      if (!Number.isFinite(value)) {
        return 'null';
      }
      return JSON.stringify(value);
    case 'undefined':
    case 'function':
    case 'symbol':
      return inArray ? 'null' : undefined;
    case 'bigint':
      throw new TypeError('BigInt values cannot be serialized as JSON.');
    default:
      break;
  }

  if (typeof value !== 'object') {
    return undefined;
  }
  const toJSON = (value as { toJSON?: unknown }).toJSON;
  if (typeof toJSON === 'function') {
    const jsonValue = toJSON.call(value);
    if (jsonValue !== value) {
      return stringifyJsonValue(jsonValue, ancestors, inArray);
    }
  }
  if (ancestors.has(value)) {
    throw new TypeError('Cannot serialize a cyclic JSON value.');
  }
  ancestors.add(value);
  try {
    if (Array.isArray(value)) {
      return `[${value.map((item) => stringifyJsonValue(item, ancestors, true)).join(',')}]`;
    }
    const entries = Object.keys(value).flatMap((key) => {
      const serialized = stringifyJsonValue(
        (value as Record<string, unknown>)[key],
        ancestors,
      );
      return serialized === undefined ? [] : [[JSON.stringify(key), serialized] as const];
    });
    return `{${entries.map(([key, item]) => `${key}:${item}`).join(',')}}`;
  } finally {
    ancestors.delete(value);
  }
};

/** Serialize JSON-compatible values while retaining selected raw number tokens. */
export const stringifyJsonWithRawNumbers = (value: unknown): string => {
  const serialized = stringifyJsonValue(value, new Set<object>());
  if (serialized === undefined) {
    throw new TypeError('Value cannot be serialized as JSON.');
  }
  return serialized;
};

const rootPath = 'inputSpecJson';

const issue = (
  code: StrictJsonIssueCode,
  path: string,
  message: string,
): StrictJsonIssue => ({ code, path, message });

const isWhitespace = (character: string | undefined) => (
  character === ' ' || character === '\n' || character === '\r' || character === '\t'
);

class JsonPreflight {
  private index = 0;

  private readonly text: string;

  private readonly length: number;

  private failure: StrictJsonIssue | null = null;

  private readonly numberTokens: { path: (string | number)[]; token: string }[] = [];

  constructor(text: string) {
    this.text = text;
    this.length = text.length;
  }

  parse(): StrictJsonIssue | null {
    this.skipWhitespace();
    if (!this.parseValue(rootPath, [], 0)) {
      return this.failure ?? issue('invalid_json', rootPath, 'Input JSON is malformed. Repair it before saving this test.');
    }
    this.skipWhitespace();
    return this.index === this.length
      ? null
      : issue('invalid_json', rootPath, 'Input JSON is malformed. Repair it before saving this test.');
  }

  getRawNumberTokens() {
    return this.numberTokens;
  }

  private parseValue(path: string, segments: (string | number)[], depth: number): boolean {
    this.skipWhitespace();
    if (depth > MAX_JSON_DEPTH) {
      this.failure = issue(
        'too_large',
        path,
        'Input JSON nesting cannot exceed 32 levels.',
      );
      this.index = this.length;
      return false;
    }

    const character = this.text[this.index];
    if (character === '{') {
      return this.parseObject(path, segments, depth);
    }
    if (character === '[') {
      return this.parseArray(path, segments, depth);
    }
    if (character === '"') {
      return this.parseString(path, true) !== null;
    }
    if (character === '-' || (character !== undefined && /[0-9]/.test(character))) {
      return this.parseNumber(path, segments);
    }
    if (this.text.startsWith('true', this.index)) {
      this.index += 4;
      return true;
    }
    if (this.text.startsWith('false', this.index)) {
      this.index += 5;
      return true;
    }
    if (this.text.startsWith('null', this.index)) {
      this.index += 4;
      return true;
    }
    return false;
  }

  private parseObject(path: string, segments: (string | number)[], depth: number): boolean {
    this.index += 1;
    this.skipWhitespace();
    const names = new Set<string>();
    if (this.text[this.index] === '}') {
      this.index += 1;
      return true;
    }

    while (this.index < this.length) {
      this.skipWhitespace();
      const name = this.parseString(path, false);
      if (name === null) {
        return false;
      }
      const propertyPath = `${path}.${name}`;
      if (names.has(name)) {
        this.failure = issue(
          'duplicate_property',
          propertyPath,
          'Duplicate JSON property is not allowed.',
        );
        this.index = this.length;
        return false;
      }
      names.add(name);
      this.skipWhitespace();
      if (this.text[this.index] !== ':') {
        return false;
      }
      this.index += 1;
      if (!this.parseValue(propertyPath, [...segments, name], depth + 1)) {
        return false;
      }
      this.skipWhitespace();
      if (this.text[this.index] === '}') {
        this.index += 1;
        return true;
      }
      if (this.text[this.index] !== ',') {
        return false;
      }
      this.index += 1;
    }
    return false;
  }

  private parseArray(path: string, segments: (string | number)[], depth: number): boolean {
    this.index += 1;
    this.skipWhitespace();
    if (this.text[this.index] === ']') {
      this.index += 1;
      return true;
    }

    let itemIndex = 0;
    while (this.index < this.length) {
      if (!this.parseValue(`${path}[${itemIndex}]`, [...segments, itemIndex], depth + 1)) {
        return false;
      }
      itemIndex += 1;
      this.skipWhitespace();
      if (this.text[this.index] === ']') {
        this.index += 1;
        return true;
      }
      if (this.text[this.index] !== ',') {
        return false;
      }
      this.index += 1;
      this.skipWhitespace();
    }
    return false;
  }

  private parseString(path: string, checkScalarLimit: boolean): string | null {
    const start = this.index;
    if (this.text[this.index] !== '"') {
      return null;
    }
    this.index += 1;
    while (this.index < this.length) {
      const character = this.text[this.index];
      if (character === '"') {
        this.index += 1;
        try {
          const value = JSON.parse(this.text.slice(start, this.index)) as unknown;
          if (typeof value !== 'string') {
            return null;
          }
          if (checkScalarLimit && value.length > MAX_JSON_SCALAR_LENGTH) {
            this.failure = issue(
              'too_large',
              path,
              'User-authored JSON strings cannot exceed 16384 characters.',
            );
            this.index = this.length;
            return null;
          }
          return value;
        } catch {
          return null;
        }
      }
      if (character === '\\') {
        this.index += 1;
        if (this.index >= this.length) {
          return null;
        }
        if (this.text[this.index] === 'u') {
          if (this.index + 4 >= this.length || !/^[0-9a-fA-F]{4}$/.test(this.text.slice(this.index + 1, this.index + 5))) {
            return null;
          }
          this.index += 5;
        } else {
          this.index += 1;
        }
        continue;
      }
      if (character !== undefined && character.charCodeAt(0) < 0x20) {
        return null;
      }
      this.index += 1;
    }
    return null;
  }

  private parseNumber(path: string, segments: (string | number)[]): boolean {
    const start = this.index;
    if (this.text[this.index] === '-') {
      this.index += 1;
    }
    if (this.text[this.index] === '0') {
      this.index += 1;
    } else if (this.text[this.index] !== undefined && /[1-9]/.test(this.text[this.index])) {
      while (this.index < this.length && /[0-9]/.test(this.text[this.index] as string)) {
        this.index += 1;
      }
    } else {
      return false;
    }
    if (this.text[this.index] === '.') {
      this.index += 1;
      const fractionStart = this.index;
      while (this.index < this.length && /[0-9]/.test(this.text[this.index] as string)) {
        this.index += 1;
      }
      if (this.index === fractionStart) {
        return false;
      }
    }
    if (this.text[this.index] === 'e' || this.text[this.index] === 'E') {
      this.index += 1;
      if (this.text[this.index] === '+' || this.text[this.index] === '-') {
        this.index += 1;
      }
      const exponentStart = this.index;
      while (this.index < this.length && /[0-9]/.test(this.text[this.index] as string)) {
        this.index += 1;
      }
      if (this.index === exponentStart) {
        return false;
      }
    }
    const value = Number(this.text.slice(start, this.index));
    if (Number.isFinite(value)) {
      const token = this.text.slice(start, this.index);
      if (shouldPreserveRawJsonNumber(token, value)) {
        this.numberTokens.push({ path: segments, token });
      }
      return true;
    }
    this.failure = issue(
      'invalid_number',
      path,
      'JSON numbers must be finite.',
    );
    this.index = this.length;
    return false;
  }

  private skipWhitespace() {
    while (isWhitespace(this.text[this.index])) {
      this.index += 1;
    }
  }
}

export interface ParseBoundedJsonOptions {
  preserveRawNumbers?: boolean;
}

export const parseBoundedJson = (
  text: string,
  options: ParseBoundedJsonOptions = {},
): StrictJsonResult => {
  if (new TextEncoder().encode(text).length > MAX_JSON_BYTES) {
    return {
      value: null,
      issue: issue('too_large', rootPath, 'Input JSON exceeds the 262144-byte limit.'),
    };
  }

  const preflight = new JsonPreflight(text);
  const preflightIssue = preflight.parse();
  if (preflightIssue) {
    return { value: null, issue: preflightIssue };
  }

  try {
    let value = JSON.parse(text) as unknown;
    if (options.preserveRawNumbers) {
      for (const token of preflight.getRawNumberTokens()) {
        const raw = new RawJsonNumber(token.token);
        if (token.path.length === 0) {
          value = raw;
          continue;
        }
        let current = value;
        for (const segment of token.path.slice(0, -1)) {
          if (typeof current !== 'object' || current === null) {
            break;
          }
          current = (current as Record<string | number, unknown>)[segment];
        }
        if (typeof current === 'object' && current !== null) {
          const last = token.path[token.path.length - 1];
          Object.defineProperty(current, last, {
            configurable: true,
            enumerable: true,
            value: raw,
            writable: true,
          });
        }
      }
    }
    return { value, issue: null };
  } catch {
    return {
      value: null,
      issue: issue('invalid_json', rootPath, 'Input JSON is malformed. Repair it before saving this test.'),
    };
  }
};
