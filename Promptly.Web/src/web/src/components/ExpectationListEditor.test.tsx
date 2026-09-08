import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ExpectationListEditor } from './ExpectationListEditor';
import type { ExpectationDraft } from './expectationSpec';

const allTypes: ExpectationDraft[] = [
  { type: 'contains_text', text: 'ready', case_insensitive: true },
  { type: 'banned_text', text: 'failure', case_insensitive: false },
  { type: 'regex_match', pattern: 'ready', case_insensitive: true },
  { type: 'link_pattern', pattern: 'https://example.test' },
  { type: 'tool_called', tool_name: 'search' },
  { type: 'tool_sequence', sequence: ['search', 'summarize'], exact_sequence: false },
  { type: 'llm_judge', rubric: 'Answer well', min_score: 0.8 },
  { type: 'groundedness', min_score: 1 },
];

const renderEditor = (
  expectations: readonly ExpectationDraft[] = allTypes,
  onChange = vi.fn(),
  disabled = false,
) => {
  render(
    <ExpectationListEditor
      expectations={expectations}
      onChange={onChange}
      disabled={disabled}
    />,
  );
  return onChange;
};

const StatefulExpectationEditor = ({
  initial = allTypes.slice(0, 3),
}: {
  initial?: readonly ExpectationDraft[];
}) => {
  const [expectations, setExpectations] = useState<ExpectationDraft[]>(() => (
    initial.map((expectation) => ({
      ...expectation,
      ...(Array.isArray(expectation.sequence) ? { sequence: [...expectation.sequence] } : {}),
    }))
  ));
  return <ExpectationListEditor expectations={expectations} onChange={setExpectations} />;
};

describe('ExpectationListEditor', () => {
  it('renders all eight type forms with ordered accessible rows', () => {
    renderEditor();

    expect(screen.getByRole('group', { name: 'Expectations' })).toBeInTheDocument();
    expect(screen.getAllByRole('group', { name: /^Expectation \d+$/ })).toHaveLength(8);
    expect(screen.getByLabelText('Text for expectation 1')).toHaveValue('ready');
    expect(screen.getByLabelText('Pattern for expectation 3')).toHaveValue('ready');
    expect(screen.getByLabelText('Tool name for expectation 5')).toHaveValue('search');
    expect(screen.getByLabelText('Tool 2 for expectation 6')).toHaveValue('summarize');
    expect(screen.getByLabelText('Rubric for expectation 7')).toHaveValue('Answer well');
    expect(screen.getByLabelText('Minimum score for expectation 8')).toHaveValue(1);
    expect(screen.getByText(/AI expectations are evaluated by the configured worker/)).toBeInTheDocument();
  });

  it('displays omitted evaluator defaults without mutating the wire values until edited', () => {
    const expectations: ExpectationDraft[] = [
      { type: 'contains_text', text: 'ready' },
      { type: 'tool_sequence', sequence: ['search'] },
      { type: 'llm_judge', rubric: 'Judge clearly' },
      { type: 'groundedness' },
      { type: 'contains_text', text: 'explicit false', case_insensitive: false },
      { type: 'tool_sequence', sequence: ['exact false'], exact_sequence: false },
      { type: 'llm_judge', rubric: 'zero', min_score: 0 },
      { type: 'groundedness', min_score: '' },
    ];
    const original = expectations.map((expectation) => ({
      ...expectation,
      ...(Array.isArray(expectation.sequence) ? { sequence: [...expectation.sequence] } : {}),
    }));
    const onChange = renderEditor(expectations);

    expect(screen.getByLabelText('Case-insensitive for expectation 1')).toBeChecked();
    expect(screen.getByLabelText('Exact sequence for expectation 2')).toBeChecked();
    expect(screen.getByLabelText('Minimum score for expectation 3')).toHaveValue(0.8);
    expect(screen.getByLabelText('Minimum score for expectation 4')).toHaveValue(0.8);
    expect(screen.getByLabelText('Case-insensitive for expectation 5')).not.toBeChecked();
    expect(screen.getByLabelText('Exact sequence for expectation 6')).not.toBeChecked();
    expect(screen.getByLabelText('Minimum score for expectation 7')).toHaveValue(0);
    expect(screen.getByLabelText('Minimum score for expectation 8')).toHaveValue(null);
    expect(expectations).toEqual(original);
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.click(screen.getByLabelText('Case-insensitive for expectation 1'));
    expect(onChange).toHaveBeenLastCalledWith([
      { type: 'contains_text', text: 'ready', case_insensitive: false },
      ...original.slice(1),
    ]);
    fireEvent.change(screen.getByLabelText('Minimum score for expectation 3'), {
      target: { value: '0.9' },
    });
    expect(onChange).toHaveBeenLastCalledWith([
      ...original.slice(0, 2),
      { type: 'llm_judge', rubric: 'Judge clearly', min_score: 0.9 },
      ...original.slice(3),
    ]);
    expect(expectations).toEqual(original);
  });

  it('groups add choices as Text, Tools, and AI and emits explicit defaults', () => {
    const onChange = renderEditor([]);

    const choices = [
      ['Contains text', { type: 'contains_text', text: '', case_insensitive: true }],
      ['Banned text', { type: 'banned_text', text: '', case_insensitive: true }],
      ['Regex match', { type: 'regex_match', pattern: '', case_insensitive: true }],
      ['Link pattern', { type: 'link_pattern', pattern: '' }],
      ['Tool called', { type: 'tool_called', tool_name: '' }],
      ['Tool sequence', { type: 'tool_sequence', sequence: [''], exact_sequence: true }],
      ['LLM judge', { type: 'llm_judge', rubric: '', min_score: 0.8 }],
      ['Groundedness', { type: 'groundedness', min_score: 0.8 }],
    ] as const;

    choices.forEach(([label]) => {
      fireEvent.click(screen.getByRole('button', { name: 'Add expectation' }));
      fireEvent.click(screen.getByRole('menuitem', { name: label }));
    });

    expect(screen.getByText('Text')).toBeInTheDocument();
    expect(screen.getByText('Tools')).toBeInTheDocument();
    expect(screen.getByText('AI')).toBeInTheDocument();
    expect(onChange).toHaveBeenCalledTimes(choices.length);
    choices.forEach(([, expected], index) => {
      expect(onChange).toHaveBeenNthCalledWith(index + 1, [expected]);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Add expectation' }));
    fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' });
    expect(screen.queryByRole('menuitem', { name: 'Contains text' })).not.toBeInTheDocument();
  });

  it('changes a row type through the select and retains stored AI metadata', () => {
    const expectations: ExpectationDraft[] = [{
      type: 'llm_judge',
      rubric: 'Answer well',
      min_score: 0.8,
      model: 'stored-model',
      provider: 'stored-provider',
    }];
    const onChange = renderEditor(expectations);
    const row = screen.getByRole('group', { name: 'Expectation 1' });

    fireEvent.mouseDown(within(row).getByRole('combobox'));
    fireEvent.click(screen.getByRole('option', { name: 'Groundedness' }));

    expect(onChange).toHaveBeenCalledWith([{
      type: 'groundedness',
      min_score: 0.8,
      model: 'stored-model',
      provider: 'stored-provider',
    }]);
  });

  it('edits fields immutably while retaining stored AI model and provider metadata', () => {
    const expectations: ExpectationDraft[] = [{
      type: 'llm_judge',
      rubric: 'old rubric',
      min_score: 0.8,
      model: 'stored-model',
      provider: 'stored-provider',
    }];
    const onChange = renderEditor(expectations);

    fireEvent.change(screen.getByLabelText('Rubric for expectation 1'), {
      target: { value: 'new rubric' },
    });

    expect(onChange).toHaveBeenCalledWith([{
      type: 'llm_judge',
      rubric: 'new rubric',
      min_score: 0.8,
      model: 'stored-model',
      provider: 'stored-provider',
    }]);
    expect(expectations[0]).toEqual({
      type: 'llm_judge',
      rubric: 'old rubric',
      min_score: 0.8,
      model: 'stored-model',
      provider: 'stored-provider',
    });
  });

  it('supports explicit boolean edits, sequence editing, deletion, and reordering', () => {
    const expectations = allTypes.map((expectation) => ({
      ...expectation,
      ...(Array.isArray(expectation.sequence) ? { sequence: [...expectation.sequence] } : {}),
    }));
    const onChange = renderEditor(expectations);

    const caseFlag = screen.getByLabelText('Case-insensitive for expectation 1');
    expect(caseFlag).toBeChecked();
    fireEvent.click(caseFlag);
    expect(onChange).toHaveBeenLastCalledWith(expect.arrayContaining([
      expect.objectContaining({ type: 'contains_text', case_insensitive: false }),
    ]));

    const exactFlag = screen.getByLabelText('Exact sequence for expectation 6');
    expect(exactFlag).not.toBeChecked();
    fireEvent.click(exactFlag);
    expect(onChange).toHaveBeenLastCalledWith(expect.arrayContaining([
      expect.objectContaining({ type: 'tool_sequence', exact_sequence: true }),
    ]));

    fireEvent.change(screen.getByLabelText('Tool 2 for expectation 6'), {
      target: { value: 'summarize-again' },
    });
    expect(onChange).toHaveBeenLastCalledWith(expect.arrayContaining([
      expect.objectContaining({ type: 'tool_sequence', sequence: ['search', 'summarize-again'] }),
    ]));

    fireEvent.click(screen.getByRole('button', { name: 'Add tool to expectation 6' }));
    expect(onChange).toHaveBeenLastCalledWith(expect.arrayContaining([
      expect.objectContaining({ type: 'tool_sequence', sequence: ['search', 'summarize', ''] }),
    ]));
    fireEvent.click(screen.getByRole('button', { name: 'Delete tool 2 from expectation 6' }));
    expect(onChange).toHaveBeenLastCalledWith(expect.arrayContaining([
      expect.objectContaining({ type: 'tool_sequence', sequence: ['search'] }),
    ]));

    fireEvent.click(screen.getByRole('button', { name: 'Move expectation 2 up' }));
    expect(onChange).toHaveBeenLastCalledWith([expectations[1], expectations[0], ...expectations.slice(2)]);
    fireEvent.click(screen.getByRole('button', { name: 'Move expectation 1 down' }));
    expect(onChange).toHaveBeenLastCalledWith([expectations[1], expectations[0], ...expectations.slice(2)]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete expectation 2' }));
    expect(onChange).toHaveBeenLastCalledWith([expectations[0], ...expectations.slice(2)]);
    expect(expectations[0].type).toBe('contains_text');
  });

  it('runs sequential stateful edits and preserves focus when ordered rows move', async () => {
    render(<StatefulExpectationEditor />);

    const firstText = screen.getByLabelText('Text for expectation 1');
    act(() => firstText.focus());
    fireEvent.change(firstText, { target: { value: 'updated first' } });
    expect(document.activeElement).toBe(firstText);

    fireEvent.click(screen.getByRole('button', { name: 'Add expectation' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Groundedness' }));
    const addedRow = screen.getByRole('group', { name: 'Expectation 4' });
    const addedType = within(addedRow).getByRole('combobox');
    act(() => addedType.focus());
    fireEvent.keyDown(addedType, { key: 'ArrowDown' });
    const selectedOption = screen.getByRole('option', { name: 'Groundedness' });
    expect(selectedOption).toHaveFocus();
    fireEvent.click(selectedOption);
    await waitFor(() => expect(addedType).toHaveFocus());

    const addedScore = within(addedRow).getByRole('spinbutton');
    act(() => addedScore.focus());
    fireEvent.change(addedScore, { target: { value: '0.5' } });
    expect(document.activeElement).toBe(addedScore);

    const moveAddedUp = within(screen.getByRole('group', { name: 'Expectation 4' }))
      .getByRole('button', { name: 'Move expectation 4 up' });
    act(() => moveAddedUp.focus());
    fireEvent.click(moveAddedUp);
    const movedRow = screen.getByRole('group', { name: 'Expectation 3' });
    expect(within(movedRow).getByRole('spinbutton')).toHaveValue(0.5);
    expect(document.activeElement).toBe(
      within(movedRow).getByRole('button', { name: 'Move expectation 3 up' }),
    );

    fireEvent.click(within(movedRow).getByRole('button', { name: 'Delete expectation 3' }));
    expect(screen.getAllByRole('group', { name: /^Expectation \d+$/ })).toHaveLength(3);
    expect(within(screen.getByRole('group', { name: 'Expectation 1' }))
      .getByRole('button', { name: 'Move expectation 1 up' })).toBeDisabled();
    expect(within(screen.getByRole('group', { name: 'Expectation 3' }))
      .getByRole('button', { name: 'Move expectation 3 down' })).toBeDisabled();
    expect(screen.getByLabelText('Text for expectation 1')).toHaveValue('updated first');
  });

  it('shows field errors for malformed rows and preserves unsupported values for repair', () => {
    renderEditor([
      { type: 'contains_text', text: 42, case_insensitive: 'yes' },
      { type: 'banned_text', text: 'failure', case_insensitive: false, retained: 'metadata' },
      { type: 'link_pattern', pattern: null },
      { type: 'tool_called', tool_name: 42 },
      { type: 'tool_sequence', sequence: 'not-a-list', exact_sequence: 'yes' },
      { type: 'llm_judge', rubric: 42, min_score: '0.5', model: 42 },
      { type: 'groundedness', min_score: '' },
      { type: 'future_type', value: true },
      { type: 'tool_sequence', sequence: [42, '  '], exact_sequence: true },
      { type: 'llm_judge', rubric: 'No score yet' },
    ]);

    expect(screen.getByText('Text must be text.')).toBeInTheDocument();
    expect(screen.getByText('Case-insensitive flag must be true or false.')).toBeInTheDocument();
    expect(screen.getByText('Link pattern must be text.')).toBeInTheDocument();
    expect(within(screen.getByRole('group', { name: 'Expectation 4' }))
      .getByText('Tool name must be text.')).toBeInTheDocument();
    expect(within(screen.getByRole('group', { name: 'Expectation 9' }))
      .getByText('Tool name must be text.')).toBeInTheDocument();
    expect(screen.getByText('Tool name is required.')).toBeInTheDocument();
    expect(screen.getByText('Add at least one tool to the sequence.')).toBeInTheDocument();
    expect(screen.getByText('Exact-sequence flag must be true or false.')).toBeInTheDocument();
    expect(screen.getByText('Rubric must be text.')).toBeInTheDocument();
    expect(screen.getByText('Score must be a finite number between 0 and 1.')).toBeInTheDocument();
    expect(screen.getByText('This expectation type is not supported. Choose a supported type to continue.'))
      .toBeInTheDocument();
    expect(screen.getByText(/Stored fields that this editor does not use will be retained/))
      .toBeInTheDocument();
    expect(screen.getByLabelText('Minimum score for expectation 7')).toHaveValue(null);
    expect(screen.getByLabelText('Minimum score for expectation 10')).toHaveValue(null);
  });

  it('repairs a sequence, clears an existing score, and changes type through controlled state', () => {
    const expectations: ExpectationDraft[] = [
      { type: 'tool_sequence', sequence: [42, '  '], exact_sequence: true },
      { type: 'llm_judge', rubric: 'Score to clear', min_score: 0.8 },
      { type: 'contains_text', text: 'keep', case_insensitive: true },
    ];
    render(<StatefulExpectationEditor initial={expectations} />);

    fireEvent.change(screen.getByLabelText('Tool 1 for expectation 1'), {
      target: { value: 'search' },
    });
    fireEvent.change(screen.getByLabelText('Tool 2 for expectation 1'), {
      target: { value: 'summarize' },
    });
    expect(screen.getByLabelText('Tool 1 for expectation 1')).toHaveValue('search');
    expect(screen.getByLabelText('Tool 2 for expectation 1')).toHaveValue('summarize');
    expect(within(screen.getByRole('group', { name: 'Expectation 1' }))
      .queryByText('Tool name is required.')).not.toBeInTheDocument();

    const score = screen.getByLabelText('Minimum score for expectation 2');
    expect(score).toHaveValue(0.8);
    fireEvent.change(score, { target: { value: '' } });
    expect(score).toHaveValue(null);
    expect(within(screen.getByRole('group', { name: 'Expectation 2' }))
      .getByText('Score is required.')).toBeInTheDocument();
    expect(screen.getByLabelText('Tool 2 for expectation 1')).toHaveValue('summarize');

    fireEvent.mouseDown(within(screen.getByRole('group', { name: 'Expectation 3' })).getByRole('combobox'));
    fireEvent.click(screen.getByRole('option', { name: 'Banned text' }));
    expect(within(screen.getByRole('group', { name: 'Expectation 3' }))
      .getByRole('combobox')).toHaveTextContent('Banned text');
    expect(screen.getByLabelText('Text for expectation 3')).toHaveValue('');
    expect(screen.getByLabelText('Case-insensitive for expectation 3')).toBeChecked();
    expect(expectations).toEqual([
      { type: 'tool_sequence', sequence: [42, '  '], exact_sequence: true },
      { type: 'llm_judge', rubric: 'Score to clear', min_score: 0.8 },
      { type: 'contains_text', text: 'keep', case_insensitive: true },
    ]);
  });

  it('shows actionable validation for empty and incomplete drafts without inventing rows', () => {
    const onChange = vi.fn();
    renderEditor([], onChange);
    expect(screen.getByText('Add at least one expectation before saving this test.'))
      .toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();

    renderEditor([{ type: 'regex_match', pattern: '', case_insensitive: true }], vi.fn());
    expect(screen.getByText('Pattern is required.')).toBeInTheDocument();
    expect(screen.getByText(/server's bounded \.NET engine/)).toBeInTheDocument();
  });

  it('keeps controls disabled and values intact', () => {
    renderEditor(allTypes, vi.fn(), true);

    expect(screen.getByRole('button', { name: 'Add expectation' })).toBeDisabled();
    const firstExpectation = screen.getByRole('group', { name: 'Expectation 1' });
    expect(within(firstExpectation).getByRole('combobox')).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByLabelText('Text for expectation 1')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Delete expectation 1' })).toBeDisabled();
  });

  it('keeps ten or more rows in the responsive editor structure', () => {
    const expectations = Array.from({ length: 12 }, (_, index) => ({
      type: 'contains_text',
      text: `Expected ${index + 1}`,
      case_insensitive: true,
    }));
    renderEditor(expectations);

    expect(screen.getAllByRole('group', { name: /^Expectation \d+$/ })).toHaveLength(12);
    expect(screen.getByLabelText('Text for expectation 12')).toHaveValue('Expected 12');
    expect(screen.getByRole('button', { name: 'Move expectation 12 down' })).toBeDisabled();
  });

  it('synchronizes stable row identities when a controlled parent changes length', () => {
    const { rerender } = render(
      <ExpectationListEditor expectations={allTypes} onChange={vi.fn()} />,
    );

    rerender(
      <ExpectationListEditor expectations={allTypes.slice(0, 2)} onChange={vi.fn()} />,
    );
    expect(screen.getAllByRole('group', { name: /^Expectation \d+$/ })).toHaveLength(2);

    rerender(
      <ExpectationListEditor
        expectations={[...allTypes, { type: 'contains_text', text: 'later', case_insensitive: true }]}
        onChange={vi.fn()}
      />,
    );
    expect(screen.getAllByRole('group', { name: /^Expectation \d+$/ })).toHaveLength(9);
    expect(screen.getByLabelText('Text for expectation 9')).toHaveValue('later');
  });
});
