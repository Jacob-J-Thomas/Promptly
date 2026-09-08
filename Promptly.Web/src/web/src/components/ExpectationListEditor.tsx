import React, { useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  FormControl,
  FormControlLabel,
  FormHelperText,
  IconButton,
  InputLabel,
  ListSubheader,
  Menu,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { Add, ArrowDownward, ArrowUpward, Delete } from '@mui/icons-material';
import {
  createExpectation,
  EXPECTATION_TYPES,
  isExpectationType,
  validateExpectations,
  type ExpectationDraft,
  type ExpectationSpecIssue,
  type ExpectationType,
} from './expectationSpec';

export interface ExpectationListEditorProps {
  expectations: readonly ExpectationDraft[];
  onChange: (expectations: ExpectationDraft[]) => void;
  disabled?: boolean;
}

const TYPE_LABELS: Record<ExpectationType, string> = {
  contains_text: 'Contains text',
  banned_text: 'Banned text',
  regex_match: 'Regex match',
  link_pattern: 'Link pattern',
  tool_called: 'Tool called',
  tool_sequence: 'Tool sequence',
  llm_judge: 'LLM judge',
  groundedness: 'Groundedness',
};

const TEXT_TYPES: readonly ExpectationType[] = [
  'contains_text', 'banned_text', 'regex_match', 'link_pattern',
];
const TOOL_TYPES: readonly ExpectationType[] = ['tool_called', 'tool_sequence'];
const AI_TYPES: readonly ExpectationType[] = ['llm_judge', 'groundedness'];

const cloneExpectation = (expectation: ExpectationDraft): ExpectationDraft => ({
  ...expectation,
  ...(Array.isArray(expectation.sequence) ? { sequence: [...expectation.sequence] } : {}),
});

const fieldIssue = (
  issues: readonly ExpectationSpecIssue[],
  index: number,
  field: string,
) => issues.find((candidate) => candidate.path === `expectations[${index}].${field}`);

const sequenceIssue = (
  issues: readonly ExpectationSpecIssue[],
  index: number,
) => issues.find((candidate) => (
  candidate.path === `expectations[${index}].sequence`
  || candidate.path.startsWith(`expectations[${index}].sequence[`)
));

const selectOptions = (
  types: readonly ExpectationType[],
  onSelect?: (type: ExpectationType) => void,
) => types.map((type) => (
  <MenuItem
    key={type}
    value={type}
    onClick={onSelect ? () => onSelect(type) : undefined}
  >
    {TYPE_LABELS[type]}
  </MenuItem>
));

export const ExpectationListEditor: React.FC<ExpectationListEditorProps> = ({
  expectations,
  onChange,
  disabled = false,
}) => {
  const [nextRowId, setNextRowId] = useState(expectations.length);
  const [rowKeys, setRowKeys] = useState<string[]>(() => (
    expectations.map((_, index) => `expectation-row-${index}`)
  ));
  const renderedRowKeys = rowKeys.length >= expectations.length
    ? rowKeys.slice(0, expectations.length)
    : [
      ...rowKeys,
      ...Array.from(
        { length: expectations.length - rowKeys.length },
        (_, index) => `expectation-row-external-${rowKeys.length + index}`,
      ),
    ];
  const [addAnchor, setAddAnchor] = useState<HTMLElement | null>(null);
  const issues = validateExpectations(expectations);
  const hasAiExpectation = expectations.some((expectation) => AI_TYPES.includes(
    expectation.type as ExpectationType,
  ));

  const updateExpectation = (index: number, update: Record<string, unknown>) => {
    onChange(expectations.map((expectation, expectationIndex) => (
      expectationIndex === index
        ? { ...cloneExpectation(expectation), ...update }
        : expectation
    )));
  };

  const addExpectation = (type: ExpectationType) => {
    const rowKey = `expectation-row-${nextRowId}`;
    setNextRowId(nextRowId + 1);
    setRowKeys([...renderedRowKeys, rowKey]);
    onChange([...expectations, createExpectation(type)]);
    setAddAnchor(null);
  };

  const changeType = (index: number, type: ExpectationType) => {
    const previous = expectations[index];
    const next = createExpectation(type);
    // Stored model/provider settings are runtime metadata. Carry them only
    // across AI expectation changes and never invent replacement values.
    if (AI_TYPES.includes(type)) {
      ['model', 'provider'].forEach((property) => {
        if (previous?.[property] !== undefined) {
          next[property] = previous[property];
        }
      });
    }
    onChange(expectations.map((expectation, expectationIndex) => (
      expectationIndex === index ? next : expectation
    )));
  };

  const deleteExpectation = (index: number) => {
    setRowKeys(renderedRowKeys.filter((_, expectationIndex) => expectationIndex !== index));
    onChange(expectations.filter((_, expectationIndex) => expectationIndex !== index));
  };

  const moveExpectation = (index: number, direction: -1 | 1) => {
    const destination = index + direction;
    if (destination < 0 || destination >= expectations.length) {
      return;
    }
    const next = [...expectations];
    const nextRowKeys = [...renderedRowKeys];
    [next[index], next[destination]] = [next[destination], next[index]];
    [nextRowKeys[index], nextRowKeys[destination]] = [
      nextRowKeys[destination],
      nextRowKeys[index],
    ];
    setRowKeys(nextRowKeys);
    onChange(next);
  };

  const renderStringField = (
    expectation: ExpectationDraft,
    index: number,
    field: string,
    label: string,
    multiline = false,
  ) => {
    const error = fieldIssue(issues, index, field);
    const value = typeof expectation[field] === 'string' ? expectation[field] as string : '';
    return (
      <TextField
        fullWidth
        multiline={multiline}
        minRows={multiline ? 3 : undefined}
        label={label}
        value={value}
        onChange={(event) => updateExpectation(index, { [field]: event.target.value })}
        disabled={disabled}
        error={Boolean(error)}
        helperText={error?.message ?? ' '}
        inputProps={{ 'aria-label': label }}
      />
    );
  };

  const renderBooleanField = (
    expectation: ExpectationDraft,
    index: number,
    field: 'case_insensitive' | 'exact_sequence',
    label: string,
  ) => (
    <FormControl error={Boolean(fieldIssue(issues, index, field))} disabled={disabled}>
      <FormControlLabel
        control={(
          <Checkbox
            // Omitted optional flags use the evaluator's defaults while the
            // caller's object remains untouched until the control is edited.
            checked={expectation[field] === undefined || expectation[field] === true}
            onChange={(event) => updateExpectation(index, { [field]: event.target.checked })}
            inputProps={{ 'aria-label': label }}
          />
        )}
        label={label}
      />
      {fieldIssue(issues, index, field) && (
        <FormHelperText>{fieldIssue(issues, index, field)?.message}</FormHelperText>
      )}
    </FormControl>
  );

  const renderScoreField = (expectation: ExpectationDraft, index: number) => {
    const error = fieldIssue(issues, index, 'min_score');
    const rawValue = expectation.min_score;
    const value = rawValue === undefined
      ? 0.8
      : typeof rawValue === 'number' ? rawValue : typeof rawValue === 'string' ? rawValue : '';
    return (
      <TextField
        fullWidth
        type="number"
        label={`Minimum score for expectation ${index + 1}`}
        value={value}
        onChange={(event) => updateExpectation(index, {
          min_score: event.target.value === '' ? '' : Number(event.target.value),
        })}
        disabled={disabled}
        error={Boolean(error)}
        helperText={error?.message ?? 'Use a number from 0 to 1.'}
        inputProps={{ min: 0, max: 1, step: 0.01, 'aria-label': `Minimum score for expectation ${index + 1}` }}
      />
    );
  };

  const renderSequence = (expectation: ExpectationDraft, index: number) => {
    const sequence = Array.isArray(expectation.sequence)
      ? expectation.sequence
      : [''];
    const sequenceError = sequenceIssue(issues, index);
    return (
      <Stack spacing={1} sx={{ minWidth: 0 }}>
        {sequence.map((tool, toolIndex) => {
          const pathError = issues.find((candidate) => (
            candidate.path === `expectations[${index}].sequence[${toolIndex}]`
          ));
          return (
            <Box key={toolIndex} sx={{ display: 'flex', gap: 1, alignItems: 'flex-start', minWidth: 0 }}>
              <TextField
                fullWidth
                label={`Tool ${toolIndex + 1} for expectation ${index + 1}`}
                value={typeof tool === 'string' ? tool : ''}
                onChange={(event) => {
                  const nextSequence = sequence.map((item, itemIndex) => (
                    itemIndex === toolIndex ? event.target.value : item
                  ));
                  updateExpectation(index, { sequence: nextSequence });
                }}
                disabled={disabled}
                error={Boolean(pathError)}
                helperText={pathError?.message ?? ' '}
                inputProps={{ 'aria-label': `Tool ${toolIndex + 1} for expectation ${index + 1}` }}
              />
              <IconButton
                aria-label={`Delete tool ${toolIndex + 1} from expectation ${index + 1}`}
                title="Delete tool"
                onClick={() => updateExpectation(index, {
                  sequence: sequence.filter((_, itemIndex) => itemIndex !== toolIndex),
                })}
                disabled={disabled || sequence.length <= 1}
              >
                <Delete />
              </IconButton>
            </Box>
          );
        })}
        {sequenceError && sequenceError.path === `expectations[${index}].sequence` && (
          <FormHelperText error>{sequenceError.message}</FormHelperText>
        )}
        <Button
          variant="text"
          onClick={() => updateExpectation(index, { sequence: [...sequence, ''] })}
          disabled={disabled}
          aria-label={`Add tool to expectation ${index + 1}`}
        >
          Add tool
        </Button>
        {renderBooleanField(
          expectation,
          index,
          'exact_sequence',
          `Exact sequence for expectation ${index + 1}`,
        )}
      </Stack>
    );
  };

  const renderFields = (expectation: ExpectationDraft, index: number) => {
    switch (expectation.type) {
      case 'contains_text':
      case 'banned_text':
        return (
          <Stack spacing={1} sx={{ minWidth: 0 }}>
            {renderStringField(expectation, index, 'text', `Text for expectation ${index + 1}`, true)}
            {renderBooleanField(
              expectation,
              index,
              'case_insensitive',
              `Case-insensitive for expectation ${index + 1}`,
            )}
          </Stack>
        );
      case 'regex_match':
        return (
          <Stack spacing={1} sx={{ minWidth: 0 }}>
            {renderStringField(expectation, index, 'pattern', `Pattern for expectation ${index + 1}`, true)}
            <Typography variant="caption" color="text.secondary">
              Pattern syntax is checked by the server&apos;s bounded .NET engine.
            </Typography>
            {renderBooleanField(
              expectation,
              index,
              'case_insensitive',
              `Case-insensitive for expectation ${index + 1}`,
            )}
          </Stack>
        );
      case 'link_pattern':
        return renderStringField(expectation, index, 'pattern', `Link pattern for expectation ${index + 1}`, true);
      case 'tool_called':
        return renderStringField(expectation, index, 'tool_name', `Tool name for expectation ${index + 1}`);
      case 'tool_sequence':
        return renderSequence(expectation, index);
      case 'llm_judge':
        return (
          <Stack spacing={1} sx={{ minWidth: 0 }}>
            {renderStringField(expectation, index, 'rubric', `Rubric for expectation ${index + 1}`, true)}
            {renderScoreField(expectation, index)}
            {(typeof expectation.model === 'string' || typeof expectation.provider === 'string') && (
              <Typography variant="caption" color="text.secondary">
                Stored model/provider settings are retained while editing this expectation.
              </Typography>
            )}
          </Stack>
        );
      case 'groundedness':
        return (
          <Stack spacing={1} sx={{ minWidth: 0 }}>
            {renderScoreField(expectation, index)}
            {(typeof expectation.model === 'string' || typeof expectation.provider === 'string') && (
              <Typography variant="caption" color="text.secondary">
                Stored model/provider settings are retained while editing this expectation.
              </Typography>
            )}
          </Stack>
        );
      default:
        return (
          <Alert severity="error">
            This expectation type is not supported. Choose a supported type to continue.
          </Alert>
        );
    }
  };

  return (
    <Box role="group" aria-label="Expectations" sx={{ width: '100%', minWidth: 0 }}>
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
          <Typography variant="h6">Expectations</Typography>
          <Typography variant="body2" color="text.secondary">
            Add the ordered checks that a response must satisfy.
          </Typography>
        </Box>
        <Button
          variant="outlined"
          startIcon={<Add />}
          onClick={(event) => setAddAnchor(event.currentTarget)}
          disabled={disabled}
          aria-label="Add expectation"
          aria-haspopup="menu"
        >
          Add expectation
        </Button>
        <Menu
          anchorEl={addAnchor}
          open={Boolean(addAnchor)}
          onClose={() => setAddAnchor(null)}
          MenuListProps={{ 'aria-label': 'Expectation types' }}
        >
          <ListSubheader>Text</ListSubheader>
          {selectOptions(TEXT_TYPES, (type) => addExpectation(type))}
          <ListSubheader>Tools</ListSubheader>
          {selectOptions(TOOL_TYPES, (type) => addExpectation(type))}
          <ListSubheader>AI</ListSubheader>
          {selectOptions(AI_TYPES, (type) => addExpectation(type))}
        </Menu>
      </Box>

      {expectations.length === 0 && (
        <Alert severity="error" sx={{ mb: 2 }}>
          Add at least one expectation before saving this test.
        </Alert>
      )}
      {hasAiExpectation && (
        <Alert severity="info" sx={{ mb: 2 }}>
          AI expectations are evaluated by the configured worker when a suite runs. Editing does not call a provider.
        </Alert>
      )}

      <Box sx={{ display: 'grid', gap: 2, minWidth: 0 }}>
        {expectations.map((expectation, index) => {
          const type = isExpectationType(expectation.type) ? expectation.type : '';
          const typeError = fieldIssue(issues, index, 'type');
          const rowErrors = issues.filter((issue) => issue.path.startsWith(`expectations[${index}]`));
          const typeLabelId = `expectation-type-label-${index}`;
          return (
            <Box
              key={renderedRowKeys[index]}
              role="group"
              aria-label={`Expectation ${index + 1}`}
              sx={{
                display: 'grid',
                gridTemplateColumns: { xs: '1fr', sm: 'minmax(180px, 220px) minmax(0, 1fr) auto' },
                gap: 2,
                alignItems: 'start',
                p: 2,
                border: 1,
                borderColor: 'divider',
                borderRadius: 1,
                minWidth: 0,
              }}
            >
              <FormControl fullWidth error={Boolean(typeError)} disabled={disabled}>
                <InputLabel id={typeLabelId}>Type</InputLabel>
                <Select
                  labelId={typeLabelId}
                  label="Type"
                  value={type}
                  onChange={(event) => {
                    if (isExpectationType(event.target.value)) {
                      changeType(index, event.target.value);
                    }
                  }}
                  inputProps={{ 'aria-label': `Expectation type ${index + 1}` }}
                >
                  <MenuItem value="" disabled>Select type</MenuItem>
                  {selectOptions(EXPECTATION_TYPES)}
                </Select>
                {typeError && <FormHelperText>{typeError.message}</FormHelperText>}
              </FormControl>

              <Box sx={{ minWidth: 0 }}>
                {renderFields(expectation, index)}
                {rowErrors.some((error) => error.code === 'unknown_property') && (
                  <Alert severity="warning" sx={{ mt: 1 }}>
                    Stored fields that this editor does not use will be retained when another field changes.
                  </Alert>
                )}
              </Box>

              <Box
                sx={{
                  display: 'flex',
                  gap: 0.5,
                  flexWrap: 'wrap',
                  justifyContent: { xs: 'flex-start', sm: 'flex-end' },
                }}
              >
                <IconButton
                  aria-label={`Move expectation ${index + 1} up`}
                  title="Move expectation up"
                  onClick={() => moveExpectation(index, -1)}
                  disabled={disabled || index === 0}
                >
                  <ArrowUpward />
                </IconButton>
                <IconButton
                  aria-label={`Move expectation ${index + 1} down`}
                  title="Move expectation down"
                  onClick={() => moveExpectation(index, 1)}
                  disabled={disabled || index === expectations.length - 1}
                >
                  <ArrowDownward />
                </IconButton>
                <IconButton
                  aria-label={`Delete expectation ${index + 1}`}
                  title="Delete expectation"
                  onClick={() => deleteExpectation(index)}
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
