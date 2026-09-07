export const MESSAGE_ROLES = ['system', 'user', 'assistant'] as const;

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
  | 'unknown_property';

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
  let parsed: unknown;
  try {
    parsed = JSON.parse(inputSpecJson) as unknown;
  } catch {
    return invalidResult(inputSpecJson, [{
      code: 'invalid_json',
      path: 'inputSpecJson',
      message: 'Input JSON is malformed. Repair it before saving this test.',
    }]);
  }

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

  return JSON.stringify({
    ...metadataWithoutMessages,
    messages: messages.map((message) => ({
      role: message.role,
      content: message.content,
    })),
  });
};
