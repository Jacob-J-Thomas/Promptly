import { fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { MessageListEditor } from './MessageListEditor';
import type { ConversationMessage } from './messageSpec';

const initialMessages: ConversationMessage[] = [
  { role: 'system', content: 'Be concise.' },
  { role: 'user', content: 'Hello' },
  { role: 'assistant', content: 'Hi there.' },
];

const renderEditor = (
  messages: readonly ConversationMessage[] = initialMessages,
  onChange = vi.fn(),
  disabled = false,
) => {
  render(<MessageListEditor messages={messages} onChange={onChange} disabled={disabled} />);
  return onChange;
};

const StatefulMessageEditor = ({
  initial = initialMessages,
}: {
  initial?: readonly ConversationMessage[];
}) => {
  const [messages, setMessages] = useState<ConversationMessage[]>(() => (
    initial.map((message) => ({ ...message }))
  ));
  return <MessageListEditor messages={messages} onChange={setMessages} />;
};

describe('MessageListEditor', () => {
  it('renders accessible ordered rows and edits role and multiline content', () => {
    const onChange = renderEditor();

    expect(screen.getByRole('group', { name: 'Conversation messages' })).toBeInTheDocument();
    expect(screen.getAllByRole('group', { name: /^Message \d+$/ })).toHaveLength(3);

    const secondMessage = screen.getByRole('group', { name: 'Message 2' });
    fireEvent.mouseDown(within(secondMessage).getByRole('combobox'));
    fireEvent.click(screen.getByRole('option', { name: 'assistant' }));
    fireEvent.change(screen.getByLabelText('Content for message 2'), {
      target: { value: 'Hello\nwith a second line' },
    });

    expect(onChange).toHaveBeenNthCalledWith(1, [
      initialMessages[0],
      { role: 'assistant', content: 'Hello' },
      initialMessages[2],
    ]);
    expect(onChange).toHaveBeenNthCalledWith(2, [
      initialMessages[0],
      { role: 'user', content: 'Hello\nwith a second line' },
      initialMessages[2],
    ]);
    const changedMessages = onChange.mock.calls[1]?.[0];
    expect(changedMessages?.[0]).toBe(initialMessages[0]);
    expect(changedMessages?.[1]).not.toBe(initialMessages[1]);
    expect(changedMessages?.[2]).toBe(initialMessages[2]);
    expect(initialMessages[1]).toEqual({ role: 'user', content: 'Hello' });
  });

  it('adds a user row, deletes a row, and reorders without mutating the prop array', () => {
    const messages = initialMessages.map((message) => ({ ...message }));
    const onChange = renderEditor(messages);

    fireEvent.click(screen.getByRole('button', { name: 'Add message' }));
    expect(onChange).toHaveBeenLastCalledWith([...messages, { role: 'user', content: '' }]);

    fireEvent.click(screen.getByRole('button', { name: 'Delete message 2' }));
    expect(onChange).toHaveBeenLastCalledWith([messages[0], messages[2]]);

    fireEvent.click(screen.getByRole('button', { name: 'Move message 2 up' }));
    expect(onChange).toHaveBeenLastCalledWith([messages[1], messages[0], messages[2]]);
    fireEvent.click(screen.getByRole('button', { name: 'Move message 1 down' }));
    expect(onChange).toHaveBeenLastCalledWith([messages[1], messages[0], messages[2]]);
    expect(messages).toEqual(initialMessages);
  });

  it('runs sequential edits through a stateful parent and keeps focus with reordered rows', () => {
    render(<StatefulMessageEditor />);

    const firstContent = screen.getByLabelText('Content for message 1');
    firstContent.focus();
    fireEvent.change(firstContent, { target: { value: 'System guidance' } });
    expect(document.activeElement).toBe(firstContent);

    fireEvent.click(screen.getByRole('button', { name: 'Add message' }));
    expect(screen.getAllByRole('group', { name: /^Message \d+$/ })).toHaveLength(4);
    const addedRow = screen.getByRole('group', { name: 'Message 4' });
    const addedRole = within(addedRow).getByRole('combobox');
    addedRole.focus();
    fireEvent.keyDown(addedRole, { key: 'ArrowDown' });
    fireEvent.click(screen.getByRole('option', { name: 'assistant' }));
    fireEvent.change(screen.getByLabelText('Content for message 4'), {
      target: { value: 'Follow-up' },
    });

    const moveAddedUp = within(screen.getByRole('group', { name: 'Message 4' }))
      .getByRole('button', { name: 'Move message 4 up' });
    moveAddedUp.focus();
    fireEvent.click(moveAddedUp);
    const movedRow = screen.getByRole('group', { name: 'Message 3' });
    expect(within(movedRow).getByLabelText('Content for message 3')).toHaveValue('Follow-up');
    expect(document.activeElement).toBe(
      within(movedRow).getByRole('button', { name: 'Move message 3 up' }),
    );

    fireEvent.click(within(movedRow).getByRole('button', { name: 'Delete message 3' }));
    expect(screen.getAllByRole('group', { name: /^Message \d+$/ })).toHaveLength(3);
    expect(screen.getByLabelText('Content for message 1')).toHaveValue('System guidance');
    expect(within(screen.getByRole('group', { name: 'Message 1' }))
      .getByRole('button', { name: 'Move message 1 up' })).toBeDisabled();
    expect(within(screen.getByRole('group', { name: 'Message 3' }))
      .getByRole('button', { name: 'Move message 3 down' })).toBeDisabled();
  });

  it('shows validation and user-turn guidance without inventing content', () => {
    const onChange = renderEditor([{ role: 'assistant', content: '' }]);

    expect(screen.getByText('Message content is required.')).toBeInTheDocument();
    expect(screen.getByText(/Add at least one user message/)).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();

    const emptyChange = vi.fn();
    renderEditor([], emptyChange);
    expect(screen.getByText('Add at least one message before saving this test.'))
      .toBeInTheDocument();
    expect(emptyChange).not.toHaveBeenCalled();
  });

  it('keeps the full editor disabled while preserving its values', () => {
    renderEditor(initialMessages, vi.fn(), true);

    expect(screen.getByRole('button', { name: 'Add message' })).toBeDisabled();
    const firstMessage = screen.getByRole('group', { name: 'Message 1' });
    expect(within(firstMessage).getByRole('combobox')).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByLabelText('Content for message 1')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Delete message 1' })).toBeDisabled();
  });

  it('keeps ten or more rows within the responsive editor structure', () => {
    const messages = Array.from({ length: 12 }, (_, index) => ({
      role: index % 2 === 0 ? 'user' : 'assistant',
      content: `Message ${index + 1}`,
    })) as ConversationMessage[];
    renderEditor(messages);

    expect(screen.getAllByRole('group', { name: /^Message \d+$/ })).toHaveLength(12);
    expect(screen.getByLabelText('Content for message 12')).toHaveValue('Message 12');
  });

  it('synchronizes stable row identities when a controlled parent changes length', () => {
    const { rerender } = render(
      <MessageListEditor messages={initialMessages} onChange={vi.fn()} />,
    );

    rerender(
      <MessageListEditor messages={initialMessages.slice(0, 2)} onChange={vi.fn()} />,
    );
    expect(screen.getAllByRole('group', { name: /^Message \d+$/ })).toHaveLength(2);

    rerender(
      <MessageListEditor
        messages={[...initialMessages, { role: 'user', content: 'Later' }]}
        onChange={vi.fn()}
      />,
    );
    expect(screen.getAllByRole('group', { name: /^Message \d+$/ })).toHaveLength(4);
    expect(screen.getByLabelText('Content for message 4')).toHaveValue('Later');
  });
});
