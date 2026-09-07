import { fireEvent, render, screen } from '@testing-library/react';
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

  it('groups add choices as Text, Tools, and AI and emits explicit defaults', () => {
    const onChange = renderEditor([]);

    fireEvent.click(screen.getByRole('button', { name: 'Add expectation' }));
    expect(screen.getByText('Text')).toBeInTheDocument();
    expect(screen.getByText('Tools')).toBeInTheDocument();
    expect(screen.getByText('AI')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('menuitem', { name: 'Regex match' }));
    expect(onChange).toHaveBeenCalledWith([
      { type: 'regex_match', pattern: '', case_insensitive: true },
    ]);
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
    fireEvent.click(screen.getByRole('button', { name: 'Delete expectation 2' }));
    expect(onChange).toHaveBeenLastCalledWith([expectations[0], ...expectations.slice(2)]);
    expect(expectations[0].type).toBe('contains_text');
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
    expect(firstExpectation.getByRole('combobox')).toHaveAttribute('aria-disabled', 'true');
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
});
