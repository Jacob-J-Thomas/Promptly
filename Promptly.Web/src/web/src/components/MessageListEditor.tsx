import React, { useEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  FormControl,
  FormHelperText,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  TextField,
  Typography,
} from '@mui/material';
import { Add, ArrowDownward, ArrowUpward, Delete } from '@mui/icons-material';
import {
  MESSAGE_ROLES,
  type ConversationMessage,
  isMessageRole,
  validateMessages,
} from './messageSpec';

export interface MessageListEditorProps {
  messages: readonly ConversationMessage[];
  onChange: (messages: ConversationMessage[]) => void;
  disabled?: boolean;
}

export const MessageListEditor: React.FC<MessageListEditorProps> = ({
  messages,
  onChange,
  disabled = false,
}) => {
  const nextRowId = useRef(messages.length);
  const [rowKeys, setRowKeys] = useState<string[]>(() => (
    messages.map((_, index) => `message-row-${index}`)
  ));
  useEffect(() => {
    setRowKeys((current) => {
      if (current.length === messages.length) {
        return current;
      }
      if (current.length > messages.length) {
        return current.slice(0, messages.length);
      }
      return [
        ...current,
        ...Array.from(
          { length: messages.length - current.length },
          () => `message-row-${nextRowId.current++}`,
        ),
      ];
    });
  }, [messages.length]);
  const issues = validateMessages(messages);
  const messageErrors = (index: number, field: string) => issues.find(
    (issue) => issue.path === `messages[${index}].${field}`,
  );
  const hasUserMessage = messages.some((message) => message.role === 'user');

  const updateMessage = (index: number, update: Partial<ConversationMessage>) => {
    onChange(messages.map((message, messageIndex) => (
      messageIndex === index ? { ...message, ...update } : message
    )));
  };

  const addMessage = () => {
    const rowKey = `message-row-${nextRowId.current++}`;
    setRowKeys([...rowKeys, rowKey]);
    onChange([
      ...messages,
      { role: 'user', content: '' },
    ]);
  };

  const deleteMessage = (index: number) => {
    setRowKeys(rowKeys.filter((_, messageIndex) => messageIndex !== index));
    onChange(messages.filter((_, messageIndex) => messageIndex !== index));
  };

  const moveMessage = (index: number, direction: -1 | 1) => {
    const destination = index + direction;
    const nextMessages = [...messages];
    const nextRowKeys = [...rowKeys];
    [nextMessages[index], nextMessages[destination]] = [
      nextMessages[destination],
      nextMessages[index],
    ];
    [nextRowKeys[index], nextRowKeys[destination]] = [
      nextRowKeys[destination],
      nextRowKeys[index],
    ];
    setRowKeys(nextRowKeys);
    onChange(nextMessages);
  };

  return (
    <Box
      role="group"
      aria-label="Conversation messages"
      sx={{ width: '100%', minWidth: 0 }}
    >
      <Box
        sx={{
          display: 'flex',
          alignItems: { xs: 'flex-start', sm: 'center' },
          justifyContent: 'space-between',
          gap: 2,
          flexWrap: 'wrap',
          mb: 2,
        }}
      >
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="h6">Messages</Typography>
          <Typography variant="body2" color="text.secondary">
            Add the ordered conversation sent to the endpoint.
          </Typography>
        </Box>
        <Button
          variant="outlined"
          startIcon={<Add />}
          onClick={addMessage}
          disabled={disabled}
          aria-label="Add message"
        >
          Add message
        </Button>
      </Box>

      {issues.some((issue) => issue.path === 'messages') && messages.length === 0 && (
        <Alert severity="error" sx={{ mb: 2 }}>
          Add at least one message before saving this test.
        </Alert>
      )}
      {!hasUserMessage && messages.length > 0 && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          Add at least one user message so the endpoint receives a user turn.
        </Alert>
      )}

      <Box sx={{ display: 'grid', gap: 2, minWidth: 0 }}>
        {messages.map((message, index) => {
          const roleError = messageErrors(index, 'role');
          const contentError = messageErrors(index, 'content');
          const roleLabelId = `message-role-label-${index}`;

          return (
            <Box
              key={rowKeys[index] ?? `message-row-pending-${index}`}
              role="group"
              aria-label={`Message ${index + 1}`}
              sx={{
                display: 'grid',
                gridTemplateColumns: { xs: '1fr', sm: 'minmax(130px, 160px) minmax(0, 1fr) auto' },
                gap: 2,
                alignItems: 'start',
                p: 2,
                border: 1,
                borderColor: 'divider',
                borderRadius: 1,
                minWidth: 0,
              }}
            >
              <FormControl fullWidth error={Boolean(roleError)} disabled={disabled}>
                <InputLabel id={roleLabelId}>Role</InputLabel>
                <Select
                  labelId={roleLabelId}
                  label="Role"
                  value={isMessageRole(message.role) ? message.role : ''}
                  onChange={(event) => updateMessage(index, {
                    role: event.target.value as ConversationMessage['role'],
                  })}
                  inputProps={{ 'aria-label': `Role for message ${index + 1}` }}
                >
                  <MenuItem value="" disabled>Select role</MenuItem>
                  {MESSAGE_ROLES.map((role) => (
                    <MenuItem key={role} value={role}>{role}</MenuItem>
                  ))}
                </Select>
                {roleError && <FormHelperText>{roleError.message}</FormHelperText>}
              </FormControl>

              <TextField
                fullWidth
                multiline
                minRows={3}
                label={`Message ${index + 1} content`}
                value={message.content}
                onChange={(event) => updateMessage(index, { content: event.target.value })}
                disabled={disabled}
                error={Boolean(contentError)}
                helperText={contentError?.message ?? ' '}
                inputProps={{ 'aria-label': `Content for message ${index + 1}` }}
              />

              <Box
                sx={{
                  display: 'flex',
                  gap: 0.5,
                  flexWrap: 'wrap',
                  justifyContent: { xs: 'flex-start', sm: 'flex-end' },
                }}
              >
                <IconButton
                  aria-label={`Move message ${index + 1} up`}
                  title="Move message up"
                  onClick={() => moveMessage(index, -1)}
                  disabled={disabled || index === 0}
                >
                  <ArrowUpward />
                </IconButton>
                <IconButton
                  aria-label={`Move message ${index + 1} down`}
                  title="Move message down"
                  onClick={() => moveMessage(index, 1)}
                  disabled={disabled || index === messages.length - 1}
                >
                  <ArrowDownward />
                </IconButton>
                <IconButton
                  aria-label={`Delete message ${index + 1}`}
                  title="Delete message"
                  onClick={() => deleteMessage(index)}
                  disabled={disabled}
                >
                  <Delete />
                </IconButton>
              </Box>
            </Box>
          );
        })}
      </Box>
    </Box>
  );
};
