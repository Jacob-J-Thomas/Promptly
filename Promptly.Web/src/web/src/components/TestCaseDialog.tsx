import React, { useEffect, useRef, useState } from 'react';
import axios from 'axios';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Tab,
  Tabs,
  TextField,
  Typography,
} from '@mui/material';
import { testsApi, type CreateTestCaseRequest, type TestCase } from '../api/tests';
import { getApiErrorMessage } from '../api/errors';
import {
  parseInputSpecJson,
  serializeInputSpec,
  validateMessages,
  type MessageSpecIssue,
} from './messageSpec';
import {
  parseExpectationsJson,
  serializeExpectations,
  validateExpectations,
  type ExpectationSpecIssue,
} from './expectationSpec';
import { ExpectationListEditor } from './ExpectationListEditor';
import { MessageListEditor } from './MessageListEditor';
import {
  formToYaml,
  yamlToForm,
  type TestCaseYamlDraft,
  type YamlValidationIssue,
} from './testCaseYaml';

export interface TestCaseDialogProps {
  open: boolean;
  suiteId: string;
  testCase: TestCase | null;
  onClose: () => void;
  onSaved: (testCase: TestCase) => Promise<void> | void;
}

type TestCaseDraft = TestCaseYamlDraft;

const emptyDraft = (): TestCaseDraft => ({
  externalId: '',
  name: '',
  description: '',
  inputMetadata: {},
  messages: [],
  expectations: [],
});

const formatMessageIssues = (issues: readonly MessageSpecIssue[]) => issues.map((issue) => ({
  path: issue.path.replace(/^inputSpecJson/, 'input'),
  message: issue.message,
}));

const formatExpectationIssues = (issues: readonly ExpectationSpecIssue[]) => issues.map((issue) => ({
  path: issue.path,
  message: issue.message,
}));

const formatYamlIssues = (issues: readonly YamlValidationIssue[]) => issues.map((issue) => ({
  path: issue.path,
  message: issue.message,
}));

interface SaveIssue {
  path: string;
  message: string;
}

interface SaveErrorDetails {
  message: string;
  errors: SaveIssue[];
}

const isRecord = (value: unknown): value is Record<string, unknown> => (
  typeof value === 'object' && value !== null && !Array.isArray(value)
);

const getSaveErrorDetails = (error: unknown): SaveErrorDetails => {
  const message = getApiErrorMessage(
    error,
    'Unable to save this test. Your draft is still here.',
  );
  if (!axios.isAxiosError(error) || !isRecord(error.response?.data)) {
    return { message, errors: [] };
  }

  const rawErrors = error.response.data.errors;
  if (!Array.isArray(rawErrors)) {
    return { message, errors: [] };
  }

  const errors = rawErrors.flatMap((rawIssue): SaveIssue[] => {
    if (!isRecord(rawIssue)
      || typeof rawIssue.path !== 'string'
      || typeof rawIssue.message !== 'string') {
      return [];
    }
    const path = rawIssue.path.trim();
    const issueMessage = rawIssue.message.trim();
    return path.length > 0 && issueMessage.length > 0
      ? [{ path, message: issueMessage }]
      : [];
  });
  return { message, errors };
};

export const TestCaseDialog: React.FC<TestCaseDialogProps> = ({
  open,
  suiteId,
  testCase,
  onClose,
  onSaved,
}) => {
  const [draft, setDraft] = useState<TestCaseDraft>(emptyDraft);
  const [mode, setMode] = useState<'form' | 'yaml'>('form');
  const [yamlText, setYamlText] = useState('');
  const [yamlErrors, setYamlErrors] = useState<YamlValidationIssue[]>([]);
  const [rawInputJson, setRawInputJson] = useState<string | null>(null);
  const [rawExpectationsJson, setRawExpectationsJson] = useState<string | null>(null);
  const [inputErrors, setInputErrors] = useState<MessageSpecIssue[]>([]);
  const [expectationErrors, setExpectationErrors] = useState<ExpectationSpecIssue[]>([]);
  const [saveError, setSaveError] = useState('');
  const [saveIssues, setSaveIssues] = useState<SaveIssue[]>([]);
  const [saving, setSaving] = useState(false);
  const savingRef = useRef(false);
  const sessionRef = useRef(0);

  useEffect(() => {
    sessionRef.current += 1;
    savingRef.current = false;
    if (!open) {
      setSaving(false);
      return () => {
        sessionRef.current += 1;
      };
    }

    setMode('form');
    setYamlText('');
    setYamlErrors([]);
    setSaveError('');
    setSaveIssues([]);
    setSaving(false);

    if (!testCase) {
      setDraft(emptyDraft());
      setRawInputJson(null);
      setRawExpectationsJson(null);
      setInputErrors([]);
      setExpectationErrors([]);
      return () => {
        sessionRef.current += 1;
      };
    }

    const input = parseInputSpecJson(testCase.inputSpecJson);
    const expectations = parseExpectationsJson(testCase.expectationsJson);
    setDraft({
      externalId: testCase.externalId,
      name: testCase.name,
      description: testCase.description ?? '',
      inputMetadata: input.metadata,
      messages: input.messages ?? [],
      expectations: expectations.expectations ?? [],
    });
    setRawInputJson(input.valid ? null : input.originalText);
    setRawExpectationsJson(expectations.valid ? null : testCase.expectationsJson);
    setInputErrors(input.errors);
    setExpectationErrors(expectations.errors);

    return () => {
      sessionRef.current += 1;
    };
  }, [open, suiteId, testCase]);

  const updateDraft = (update: Partial<TestCaseDraft>) => {
    setDraft((current) => ({ ...current, ...update }));
    setSaveError('');
    setSaveIssues([]);
  };

  const handleRawInputChange = (value: string) => {
    setRawInputJson(value);
    const parsed = parseInputSpecJson(value);
    setInputErrors(parsed.errors);
    if (parsed.valid && parsed.messages) {
      setDraft((current) => ({
        ...current,
        messages: parsed.messages ?? [],
        inputMetadata: parsed.metadata,
      }));
      setRawInputJson(null);
    }
    setSaveError('');
    setSaveIssues([]);
  };

  const handleRawExpectationsChange = (value: string) => {
    setRawExpectationsJson(value);
    const parsed = parseExpectationsJson(value);
    setExpectationErrors(parsed.errors);
    if (parsed.valid && parsed.expectations) {
      setDraft((current) => ({ ...current, expectations: parsed.expectations ?? [] }));
      setRawExpectationsJson(null);
    }
    setSaveError('');
    setSaveIssues([]);
  };

  const getFormIssues = () => {
    const issues = [
      ...(draft.externalId.trim().length === 0
        ? [{ path: 'externalId', message: 'External ID is required.' }]
        : []),
      ...(draft.name.trim().length === 0
        ? [{ path: 'name', message: 'Name is required.' }]
        : []),
      ...(rawInputJson !== null
        ? formatMessageIssues(inputErrors)
        : validateMessages(draft.messages).map((issue) => ({ path: issue.path, message: issue.message }))),
      ...(rawExpectationsJson !== null
        ? formatExpectationIssues(expectationErrors)
        : formatExpectationIssues(validateExpectations(draft.expectations))),
    ];
    return issues;
  };

  const buildYaml = () => formToYaml(draft);

  const handleModeChange = (_event: React.SyntheticEvent, nextMode: 'form' | 'yaml') => {
    if (nextMode === mode || saving) {
      return;
    }
    if (nextMode === 'yaml') {
      if (rawInputJson !== null || rawExpectationsJson !== null) {
        setSaveError('Repair the existing JSON fields before opening YAML.');
        return;
      }
      const converted = buildYaml();
      if (!converted.valid || !converted.yaml) {
        setYamlErrors(converted.errors);
        return;
      }
      setYamlText(converted.yaml);
      setYamlErrors([]);
      setMode('yaml');
      return;
    }

    const parsed = yamlToForm(yamlText);
    if (!parsed.valid || !parsed.draft) {
      setYamlErrors(parsed.errors);
      setSaveError('Repair the YAML errors before returning to Form.');
      return;
    }
    setDraft(parsed.draft);
    setYamlErrors([]);
    setMode('form');
    setRawInputJson(null);
    setRawExpectationsJson(null);
    setInputErrors([]);
    setExpectationErrors([]);
    setSaveError('');
    setSaveIssues([]);
  };

  const handleSave = async () => {
    if (saving || savingRef.current) {
      return;
    }
    if (mode === 'yaml') {
      const parsed = yamlToForm(yamlText);
      if (!parsed.valid || !parsed.draft) {
        setYamlErrors(parsed.errors);
        setSaveError('Repair the YAML errors before saving this test.');
        setSaveIssues([]);
        return;
      }
      setDraft(parsed.draft);
      setYamlErrors([]);
      await saveDraft(parsed.draft);
      return;
    }

    const issues = getFormIssues();
    if (issues.length > 0) {
      setSaveError('Fix the highlighted fields before saving this test.');
      setSaveIssues([]);
      return;
    }
    await saveDraft(draft);
  };

  const saveDraft = async (currentDraft: TestCaseDraft) => {
    const inputSpecJson = serializeInputSpec(currentDraft.messages, currentDraft.inputMetadata);
    const serializedExpectations = serializeExpectations(currentDraft.expectations);
    if (!serializedExpectations.valid || !serializedExpectations.json) {
      setSaveError('Fix the highlighted expectation fields before saving this test.');
      setSaveIssues([]);
      return;
    }

    const request: CreateTestCaseRequest = {
      externalId: currentDraft.externalId,
      name: currentDraft.name,
      description: currentDraft.description,
      inputSpecJson,
      expectationsJson: serializedExpectations.json,
    };
    const session = sessionRef.current;
    savingRef.current = true;
    setSaving(true);
    setSaveError('');
    setSaveIssues([]);
    try {
      const saved = testCase
        ? await testsApi.update(testCase.id, request)
        : await testsApi.create(suiteId, request);
      if (session !== sessionRef.current) {
        return;
      }
      await onSaved(saved);
      if (session === sessionRef.current) {
        onClose();
      }
    } catch (error: unknown) {
      if (session === sessionRef.current) {
        const details = getSaveErrorDetails(error);
        setSaveError(details.message);
        setSaveIssues(details.errors);
      }
    } finally {
      if (session === sessionRef.current) {
        savingRef.current = false;
        setSaving(false);
      }
    }
  };

  const close = () => {
    if (!saving) {
      sessionRef.current += 1;
      onClose();
    }
  };

  const issueList = mode === 'yaml' ? formatYamlIssues(yamlErrors) : getFormIssues();

  return (
    <Dialog open={open} onClose={close} maxWidth="lg" fullWidth>
      <DialogTitle>{testCase ? 'Edit Test Case' : 'Create Test Case'}</DialogTitle>
      <DialogContent dividers>
        <Tabs value={mode} onChange={handleModeChange} aria-label="Test authoring mode">
          <Tab value="form" label="Form" disabled={saving} />
          <Tab value="yaml" label="YAML" disabled={saving} />
        </Tabs>

        {saveError && (
          <Alert
            severity="error"
            sx={{ mt: 2 }}
            action={(
              <Box sx={{ display: 'flex', gap: 1 }}>
                <Button color="inherit" size="small" onClick={handleSave} disabled={saving}>Retry</Button>
                <Button color="inherit" size="small" onClick={close} disabled={saving}>Cancel</Button>
              </Box>
            )}
          >
            {saveError}
            {saveIssues.map((issue, index) => (
              <Typography key={`${issue.path}-${index}`} component="div" variant="body2">
                {issue.path}: {issue.message}
              </Typography>
            ))}
          </Alert>
        )}

        {mode === 'yaml' ? (
          <Box sx={{ pt: 2, display: 'grid', gap: 2 }}>
            {issueList.length > 0 && (
              <Alert severity="error">
                <Typography component="div">Repair these YAML fields:</Typography>
                {issueList.map((issue, index) => (
                  <Typography key={`${issue.path}-${index}`} variant="body2">
                    {issue.path}: {issue.message}
                  </Typography>
                ))}
              </Alert>
            )}
            <TextField
              label="Test YAML"
              value={yamlText}
              onChange={(event) => {
                setYamlText(event.target.value);
                setYamlErrors([]);
                setSaveError('');
              }}
              multiline
              minRows={18}
              fullWidth
              disabled={saving}
              inputProps={{ 'aria-label': 'Test YAML' }}
            />
          </Box>
        ) : (
          <Box sx={{ pt: 2, display: 'grid', gap: 3 }}>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 2 }}>
              <TextField
                label="External ID"
                value={draft.externalId}
                onChange={(event) => updateDraft({ externalId: event.target.value })}
                required
                fullWidth
                disabled={saving}
                error={draft.externalId.trim().length === 0}
                helperText={draft.externalId.trim().length === 0 ? 'External ID is required.' : ' '}
              />
              <TextField
                label="Name"
                value={draft.name}
                onChange={(event) => updateDraft({ name: event.target.value })}
                required
                fullWidth
                disabled={saving}
                error={draft.name.trim().length === 0}
                helperText={draft.name.trim().length === 0 ? 'Name is required.' : ' '}
              />
              <TextField
                label="Description"
                value={draft.description}
                onChange={(event) => updateDraft({ description: event.target.value })}
                multiline
                minRows={2}
                fullWidth
                disabled={saving}
                sx={{ gridColumn: { sm: '1 / -1' } }}
              />
            </Box>

            {rawInputJson !== null && (
              <Alert severity="error">
                <Typography component="div">Existing input JSON needs repair before this test can be saved.</Typography>
                {formatMessageIssues(inputErrors).map((issue, index) => (
                  <Typography key={`${issue.path}-${index}`} variant="body2">
                    {issue.path}: {issue.message}
                  </Typography>
                ))}
                <TextField
                  label="Repair input JSON"
                  value={rawInputJson}
                  onChange={(event) => handleRawInputChange(event.target.value)}
                  multiline
                  minRows={6}
                  fullWidth
                  disabled={saving}
                  sx={{ mt: 1 }}
                  inputProps={{ 'aria-label': 'Repair input JSON' }}
                />
              </Alert>
            )}
            <MessageListEditor
              messages={draft.messages}
              onChange={(messages) => updateDraft({ messages })}
              disabled={saving || rawInputJson !== null}
            />

            {rawExpectationsJson !== null && (
              <Alert severity="error">
                <Typography component="div">Existing expectations JSON needs repair before this test can be saved.</Typography>
                {formatExpectationIssues(expectationErrors).map((issue, index) => (
                  <Typography key={`${issue.path}-${index}`} variant="body2">
                    {issue.path}: {issue.message}
                  </Typography>
                ))}
                <TextField
                  label="Repair expectations JSON"
                  value={rawExpectationsJson}
                  onChange={(event) => handleRawExpectationsChange(event.target.value)}
                  multiline
                  minRows={6}
                  fullWidth
                  disabled={saving}
                  sx={{ mt: 1 }}
                  inputProps={{ 'aria-label': 'Repair expectations JSON' }}
                />
              </Alert>
            )}
            <ExpectationListEditor
              expectations={draft.expectations}
              onChange={(expectations) => updateDraft({ expectations })}
              disabled={saving || rawExpectationsJson !== null}
            />
          </Box>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={close} disabled={saving}>Cancel</Button>
        <Button
          variant="contained"
          onClick={handleSave}
          disabled={saving || issueList.length > 0}
        >
          {saving && <CircularProgress size={18} sx={{ mr: 1 }} />}
          {testCase ? 'Save changes' : 'Create'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};
