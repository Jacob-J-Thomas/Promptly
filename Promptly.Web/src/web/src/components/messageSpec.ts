import {
  MAX_JSON_SCALAR_LENGTH,
  parseBoundedJson,
  stringifyJsonWithRawNumbers,
} from './strictJson';

export const MESSAGE_ROLES = ['system', 'user', 'assistant'] as const;
export const MAX_MESSAGE_COUNT = 100;

export type MessageRole = (typeof MESSAGE_ROLES)[number];

export interface ConversationMessage {
  role: MessageRole;
  content: string;
}

export type MessageSpecIssueCode =
  | 'invalid_json'
  | 'invalid_shape'
  | 'required'
  | 'invalid_role'
  | 'invalid_type'
  | 'unknown_property'
  | 'duplicate_property'
  | 'too_large'
  | 'invalid_number';

export interface MessageSpecIssue {
  code: MessageSpecIssueCode;
  path: string;
  message: string;
}

export interface InputSpecParseResult {
  originalText: string;
  valid: boolean;
  messages: ConversationMessage[] | null;
  metadata: Record<string, unknown>;
  errors: MessageSpecIssue[];
  warnings: MessageSpecIssue[];
}

export const isMessageRole = (value: unknown): value is MessageRole => (
  typeof value === 'string'
  && (MESSAGE_ROLES as readonly string[]).includes(value)
);

export const validateMessages = (
  messages: readonly ConversationMessage[],
): MessageSpecIssue[] => {
  const issues: MessageSpecIssue[] = [];

  if (messages.length === 0) {
    issues.push({
      code: 'required',
      path: 'messages',
      message: 'Add at least one message before saving this test.',
    });
  }
  if (messages.length > MAX_MESSAGE_COUNT) {
    issues.push({
      code: 'too_large',
      path: 'messages',
      message: `Messages cannot exceed ${MAX_MESSAGE_COUNT} items.`,
    });
  }

  messages.forEach((message, index) => {
    if (!isMessageRole(message.role)) {
      issues.push({
        code: 'invalid_role',
        path: `messages[${index}].role`,
        message: 'Choose system, user, or assistant.',
      });
    }

    if (typeof message.content !== 'string' || message.content.trim().length === 0) {
      issues.push({
        code: 'required',
        path: `messages[${index}].content`,
        message: 'Message content is required.',
      });
    } else if (message.content.length > MAX_JSON_SCALAR_LENGTH) {
      issues.push({
        code: 'too_large',
        path: `messages[${index}].content`,
        message: 'Message content cannot exceed 16384 characters.',
      });
    }
  });

  return issues;
};

const warningForMissingUser = (): MessageSpecIssue => ({
  code: 'required',
  path: 'messages',
  message: 'Add at least one user message so the endpoint receives a user turn.',
});

const isRecord = (value: unknown): value is Record<string, unknown> => (
  typeof value === 'object' && value !== null && !Array.isArray(value)
);

const invalidResult = (
  originalText: string,
  errors: MessageSpecIssue[],
  metadata: Record<string, unknown> = {},
): InputSpecParseResult => ({
  originalText,
  valid: false,
  messages: null,
  metadata,
  errors,
  warnings: [],
});

/**
 * Parse the persisted input-spec string without inventing a fallback.
 * `messages: null` and `valid: false` are deliberate signals to callers that
 * the original text must be preserved and repaired explicitly.
 */
export const parseInputSpecJson = (inputSpecJson: string): InputSpecParseResult => {
  const bounded = parseBoundedJson(inputSpecJson, { preserveRawNumbers: true });
  if (bounded.issue) {
    return invalidResult(inputSpecJson, [{
      code: bounded.issue.code,
      path: bounded.issue.path,
      message: bounded.issue.message,
    }]);
  }
  const parsed = bounded.value;

  if (!isRecord(parsed)) {
    return invalidResult(inputSpecJson, [{
      code: 'invalid_shape',
      path: 'inputSpecJson',
      message: 'Input must be a JSON object containing messages.',
    }]);
  }

  const { messages: rawMessages, ...metadata } = parsed;
  if (!Array.isArray(rawMessages)) {
    return invalidResult(inputSpecJson, [{
      code: 'required',
      path: 'inputSpecJson.messages',
      message: 'Input must contain a messages array.',
    }], metadata);
  }

  const errors: MessageSpecIssue[] = [];
  const messages: ConversationMessage[] = [];

  rawMessages.forEach((rawMessage, index) => {
    if (!isRecord(rawMessage)) {
      errors.push({
        code: 'invalid_shape',
        path: `inputSpecJson.messages[${index}]`,
        message: 'Each message must be an object.',
      });
      return;
    }

    Object.keys(rawMessage).forEach((property) => {
      if (property !== 'role' && property !== 'content') {
        errors.push({
          code: 'unknown_property',
          path: `inputSpecJson.messages[${index}].${property}`,
          message: `Message property "${property}" is not supported.`,
        });
      }
    });

    if (!isMessageRole(rawMessage.role)) {
      errors.push({
        code: 'invalid_role',
        path: `inputSpecJson.messages[${index}].role`,
        message: 'Choose system, user, or assistant.',
      });
    }

    if (typeof rawMessage.content !== 'string') {
      errors.push({
        code: 'invalid_type',
        path: `inputSpecJson.messages[${index}].content`,
        message: 'Message content must be text.',
      });
      return;
    }

    if (rawMessage.content.trim().length === 0) {
      errors.push({
        code: 'required',
        path: `inputSpecJson.messages[${index}].content`,
        message: 'Message content is required.',
      });
    }

    if (isMessageRole(rawMessage.role)) {
      messages.push({ role: rawMessage.role, content: rawMessage.content });
    }
  });

  if (rawMessages.length === 0) {
    errors.push({
      code: 'required',
      path: 'inputSpecJson.messages',
      message: 'Add at least one message before saving this test.',
    });
  } else if (rawMessages.length > MAX_MESSAGE_COUNT) {
    errors.push({
      code: 'too_large',
      path: 'inputSpecJson.messages',
      message: `Messages cannot exceed ${MAX_MESSAGE_COUNT} items.`,
    });
  }

  if (errors.length > 0) {
    return invalidResult(inputSpecJson, errors, metadata);
  }

  const warnings = messages.some((message) => message.role === 'user')
    ? []
    : [warningForMissingUser()];

  return {
    originalText: inputSpecJson,
    valid: true,
    messages,
    metadata,
    errors: [],
    warnings,
  };
};

/** Serialize without changing the caller-owned messages or metadata objects. */
export const serializeInputSpec = (
  messages: readonly ConversationMessage[],
  metadata: Readonly<Record<string, unknown>> = {},
): string => {
  const metadataWithoutMessages = { ...metadata };
  delete metadataWithoutMessages.messages;

  return stringifyJsonWithRawNumbers({
    ...metadataWithoutMessages,
    messages: messages.map((message) => ({
      role: message.role,
      content: message.content,
    })),
  });
};
