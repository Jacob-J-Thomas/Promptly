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

  constructor(text: string) {
    this.text = text;
    this.length = text.length;
  }

  parse(): StrictJsonIssue | null {
    this.skipWhitespace();
    if (!this.parseValue(rootPath, 0)) {
      return this.failure ?? issue('invalid_json', rootPath, 'Input JSON is malformed. Repair it before saving this test.');
    }
    this.skipWhitespace();
    return this.index === this.length
      ? null
      : issue('invalid_json', rootPath, 'Input JSON is malformed. Repair it before saving this test.');
  }

  private parseValue(path: string, depth: number): boolean {
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
      return this.parseObject(path, depth);
    }
    if (character === '[') {
      return this.parseArray(path, depth);
    }
    if (character === '"') {
      return this.parseString(path, true) !== null;
    }
    if (character === '-' || (character !== undefined && /[0-9]/.test(character))) {
      return this.parseNumber(path);
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

  private parseObject(path: string, depth: number): boolean {
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
      if (!this.parseValue(propertyPath, depth + 1)) {
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

  private parseArray(path: string, depth: number): boolean {
    this.index += 1;
    this.skipWhitespace();
    if (this.text[this.index] === ']') {
      this.index += 1;
      return true;
    }

    let itemIndex = 0;
    while (this.index < this.length) {
      if (!this.parseValue(`${path}[${itemIndex}]`, depth + 1)) {
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

  private parseNumber(path: string): boolean {
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

export const parseBoundedJson = (text: string): StrictJsonResult => {
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
    const value = JSON.parse(text) as unknown;
    return { value, issue: null };
  } catch {
    return {
      value: null,
      issue: issue('invalid_json', rootPath, 'Input JSON is malformed. Repair it before saving this test.'),
    };
  }
};
