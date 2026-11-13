import { useEffect, useId, useState } from 'react';
import { Link } from 'react-router';
import { MessageSquare, Power } from 'lucide-react';
import { ApiError, api } from '../api/client';
import { useApi, useApiMutation } from '../api/hooks';
import {
  AGENT_CAPABILITIES,
  type BulkResultDto,
  type EndpointDetailDto,
  type NotifyRequest,
  type RestartMode,
  type RestartRequest,
  type UserMessagePreset,
  type UserMessagePresetDto,
} from '../api/types';
import { formatDuration } from '../lib/format';
import { Dialog } from './Dialog';
import { jobIdOf } from './InstallDialog';
import { EXPIRY_OPTIONS, RestartModeFields } from './PolicySections';
import { useToast } from './Toast';
import { Alert, Button, Checkbox, cx, InlineError, Input, Select, Spinner } from './ui';

/** One endpoint (with its live state) or a bulk selection. */
export type RestartTarget = { kind: 'endpoint'; endpoint: EndpointDetailDto } | { kind: 'bulk'; ids: string[] };

/** "2026-11-14T03:00" (local) for a datetime-local input. */
function toLocalInput(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function parseLocalInput(value: string): Date | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

function targetTitle(target: RestartTarget | null): string {
  if (!target) return '';
  return target.kind === 'endpoint' ? target.endpoint.summary.hostname : `${target.ids.length} endpoint${target.ids.length === 1 ? '' : 's'}`;
}

function useJobToast() {
  const toast = useToast();
  return (label: string, result: unknown) => {
    const jobId = jobIdOf(result);
    toast.success(
      <span>
        {label}{' '}
        {jobId && (
          <Link to={`/jobs/${jobId}`} className="text-accent hover:underline">
            View job
          </Link>
        )}
      </span>,
    );
  };
}

function duplicateMessage(error: unknown): string | null {
  return error instanceof ApiError && error.code === 'duplicate_job' ? 'A restart or the same message is already queued or running for this endpoint.' : null;
}

export function RestartDialog({ target, onClose, onBulkResult }: {
  target: RestartTarget | null;
  onClose: () => void;
  onBulkResult?: (title: string, data: BulkResultDto) => void;
}) {
  const jobToast = useJobToast();
  const whenId = useId();
  const atId = useId();
  const expiryId = useId();
  const [when, setWhen] = useState<'now' | 'later'>('now');
  const [at, setAt] = useState('');
  const [mode, setMode] = useState<RestartMode>('PromptWithDeadline');
  const [countdownMinutes, setCountdownMinutes] = useState(5);
  const [deadlineMinutes, setDeadlineMinutes] = useState(240);
  const [onlyIfPending, setOnlyIfPending] = useState(false);
  const [expiry, setExpiry] = useState(120);

  useEffect(() => {
    if (!target) return;
    setWhen('now');
    const tonight = new Date();
    tonight.setDate(tonight.getDate() + (tonight.getHours() >= 22 ? 1 : 0));
    tonight.setHours(22, 0, 0, 0);
    setAt(toLocalInput(tonight));
    setMode('PromptWithDeadline');
    setOnlyIfPending(false);
    setExpiry(120);
  }, [target]);

  const restart = useApiMutation(
    (req: RestartRequest) =>
      target?.kind === 'endpoint'
        ? api.post<unknown>(`/endpoints/${target.endpoint.summary.id}/restart`, req)
        : api.post<BulkResultDto>('/endpoints/bulk/restart', { endpointIds: target?.kind === 'bulk' ? target.ids : [], restart: req }),
    {
      invalidate: ['/endpoints', '/jobs'],
      onSuccess: (result) => {
        if (target?.kind === 'bulk') onBulkResult?.('Restart', result as BulkResultDto);
        else jobToast(when === 'now' ? 'Restart requested.' : 'Restart scheduled.', result);
        onClose();
      },
    },
  );

  const scheduled = when === 'later' ? parseLocalInput(at) : null;
  const timeProblem =
    when === 'later' && (!scheduled || scheduled.getTime() < Date.now() - 60_000)
      ? 'Choose a time in the future.'
      : scheduled && scheduled.getTime() > Date.now() + 31 * 86_400_000
        ? 'A restart can be scheduled up to 31 days ahead.'
        : null;
  const endpoint = target?.kind === 'endpoint' ? target.endpoint : null;
  const unsupported = endpoint !== null && !endpoint.agentCapabilities.includes(AGENT_CAPABILITIES.restart);

  function submit() {
    restart.mutate({
      mode,
      condition: onlyIfPending ? 'OnlyIfPending' : 'Always',
      countdownMinutes: mode === 'Countdown' ? countdownMinutes : null,
      deadlineMinutes: mode === 'PromptWithDeadline' ? deadlineMinutes : null,
      scheduledAt: scheduled ? scheduled.toISOString() : null,
      expiresAfterMinutes: expiry,
      force: false,
    });
  }

  return (
    <Dialog
      open={target !== null}
      onClose={onClose}
      title={`Restart ${targetTitle(target)}`}
      footer={
        <>
          <Button onClick={onClose} disabled={restart.isPending}>
            Cancel
          </Button>
          <Button
            variant={mode === 'Immediate' && when === 'now' ? 'danger' : 'primary'}
            loading={restart.isPending}
            disabled={unsupported || !!timeProblem}
            onClick={submit}
            icon={<Power className="size-4" aria-hidden />}
          >
            {when === 'now' ? 'Restart' : 'Schedule restart'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4 text-sm">
        {endpoint && <EndpointRestartContext endpoint={endpoint} />}
        {target?.kind === 'bulk' && (
          <p className="text-muted">
            Each endpoint gets its own restart job. Endpoints whose agent is older than 1.1.0 are skipped and listed in the results.
          </p>
        )}

        <div className="flex flex-wrap gap-4">
          <div className="flex flex-col gap-1">
            <label htmlFor={whenId} className="text-xs font-medium text-muted">
              When
            </label>
            <Select id={whenId} className="w-48" value={when} onChange={(e) => setWhen(e.target.value as 'now' | 'later')}>
              <option value="now">Now</option>
              <option value="later">At a specific time</option>
            </Select>
          </div>
          {when === 'later' && (
            <div className="flex flex-col gap-1">
              <label htmlFor={atId} className="text-xs font-medium text-muted">
                Time (your browser's time zone)
              </label>
              <Input id={atId} type="datetime-local" className="w-56" value={at} onChange={(e) => setAt(e.target.value)} aria-invalid={timeProblem ? true : undefined} />
            </div>
          )}
          <div className="flex flex-col gap-1">
            <label htmlFor={expiryId} className="text-xs font-medium text-muted">
              If the computer is off, give up after
            </label>
            <Select id={expiryId} className="w-40" value={expiry} onChange={(e) => setExpiry(Number(e.target.value))}>
              {EXPIRY_OPTIONS.map((m) => (
                <option key={m} value={m}>
                  {formatDuration(m)}
                </option>
              ))}
            </Select>
          </div>
        </div>
        {timeProblem && <p className="text-xs text-danger">{timeProblem}</p>}

        <RestartModeFields
          mode={mode}
          countdownMinutes={countdownMinutes}
          deadlineMinutes={deadlineMinutes}
          onChange={(c) => {
            if (c.mode) setMode(c.mode);
            if (c.countdownMinutes) setCountdownMinutes(c.countdownMinutes);
            if (c.deadlineMinutes) setDeadlineMinutes(c.deadlineMinutes);
          }}
        />
        <Checkbox
          label="Only if Windows reports a pending restart"
          hint="The agent checks when the job arrives and completes without restarting if nothing is pending."
          checked={onlyIfPending}
          onChange={(e) => setOnlyIfPending(e.target.checked)}
        />
        <p className="text-xs text-muted">If nobody is signed in when the restart is due, the computer restarts at once in every mode.</p>
        {duplicateMessage(restart.error) ? <Alert tone="warn">{duplicateMessage(restart.error)}</Alert> : <InlineError error={restart.error} />}
      </div>
    </Dialog>
  );
}

function EndpointRestartContext({ endpoint }: { endpoint: EndpointDetailDto }) {
  const s = endpoint.summary;
  if (!endpoint.agentCapabilities.includes(AGENT_CAPABILITIES.restart)) {
    return (
      <Alert tone="danger" title="This agent cannot restart the computer">
        Agent {s.agentVersion ?? 'version unknown'} does not support restarts. Install EasyPatch agent 1.1.0 or later on this endpoint.
      </Alert>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      {!s.isOnline && <Alert tone="warn">The endpoint is offline. The restart waits until it checks in, up to the time limit below.</Alert>}
      <SessionSummary endpoint={endpoint} />
      {endpoint.activeJob && endpoint.activeJob.type !== 'NotifyUser' && (
        <p className="text-xs text-muted">Another job is active; the restart starts after it finishes.</p>
      )}
    </div>
  );
}

export function SessionSummary({ endpoint }: { endpoint: EndpointDetailDto }) {
  const users = endpoint.signedInUsers;
  const trays = endpoint.trayConnectedSessions ?? 0;
  if (users === null) return <p className="text-xs text-muted">Signed-in users: unknown until the agent reports.</p>;
  if (users === 0) return <p className="text-xs text-muted">Nobody is signed in.</p>;
  return (
    <p className={cx('text-xs', trays < users ? 'text-warn' : 'text-muted')}>
      {users} signed-in user{users === 1 ? '' : 's'}; the EasyPatch tray is running in {trays} of {users} session{users === 1 ? '' : 's'}.
      {trays === 0 &&
        ' Without the tray, countdowns and deadlines rely on the Windows restart warning, and “Ask users, no deadline” waits until the tray starts.'}
    </p>
  );
}

export function MessageDialog({ target, onClose, onBulkResult }: {
  target: RestartTarget | null;
  onClose: () => void;
  onBulkResult?: (title: string, data: BulkResultDto) => void;
}) {
  const jobToast = useJobToast();
  const presets = useApi<UserMessagePresetDto[]>(target ? '/user-messages' : null, undefined, { staleTime: 3_600_000 });
  const [preset, setPreset] = useState<UserMessagePreset>('SaveYourWork');
  const [at, setAt] = useState('');
  const atId = useId();

  useEffect(() => {
    if (!target) return;
    setPreset('SaveYourWork');
    const soon = new Date(Date.now() + 3_600_000);
    soon.setMinutes(0, 0, 0);
    setAt(toLocalInput(soon));
  }, [target]);

  const send = useApiMutation(
    (req: NotifyRequest) =>
      target?.kind === 'endpoint'
        ? api.post<unknown>(`/endpoints/${target.endpoint.summary.id}/notify`, req)
        : api.post<BulkResultDto>('/endpoints/bulk/notify', { endpointIds: target?.kind === 'bulk' ? target.ids : [], message: req }),
    {
      invalidate: ['/endpoints', '/jobs'],
      onSuccess: (result) => {
        if (target?.kind === 'bulk') onBulkResult?.('Message to users', result as BulkResultDto);
        else jobToast('Message queued.', result);
        onClose();
      },
    },
  );

  const selected = presets.data?.find((p) => p.preset === preset);
  const restartAt = selected?.needsRestartTime ? parseLocalInput(at) : null;
  const timeProblem = selected?.needsRestartTime && (!restartAt || restartAt.getTime() < Date.now()) ? 'Choose the restart time to show.' : null;
  const endpoint = target?.kind === 'endpoint' ? target.endpoint : null;
  const unsupported = endpoint !== null && !endpoint.agentCapabilities.includes(AGENT_CAPABILITIES.notifyUser);

  return (
    <Dialog
      open={target !== null}
      onClose={onClose}
      title={`Send a message to ${targetTitle(target)}`}
      description="Users see a notification from the EasyPatch icon. Only these preset messages can be sent."
      footer={
        <>
          <Button onClick={onClose} disabled={send.isPending}>
            Cancel
          </Button>
          <Button
            variant="primary"
            loading={send.isPending}
            disabled={unsupported || !selected || !!timeProblem}
            onClick={() => send.mutate({ preset, restartAt: restartAt ? restartAt.toISOString() : null })}
            icon={<MessageSquare className="size-4" aria-hidden />}
          >
            Send message
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4 text-sm">
        {unsupported && (
          <Alert tone="danger" title="This agent cannot show messages">
            Install EasyPatch agent 1.1.0 or later on this endpoint.
          </Alert>
        )}
        {endpoint && !unsupported && <SessionSummary endpoint={endpoint} />}
        {presets.isPending ? (
          <Spinner />
        ) : presets.isError ? (
          <InlineError error={presets.error} />
        ) : (
          <fieldset className="flex flex-col gap-2">
            <legend className="sr-only">Message</legend>
            {presets.data.map((p) => (
              <label
                key={p.preset}
                className={cx(
                  'flex cursor-pointer gap-3 rounded-md border px-3 py-2',
                  p.preset === preset ? 'border-accent bg-accent-soft' : 'border-line hover:bg-hover',
                )}
              >
                <input type="radio" name="preset" className="mt-1" checked={p.preset === preset} onChange={() => setPreset(p.preset)} />
                <span>
                  <span className="block font-medium">{p.title}</span>
                  <span className="block text-xs text-muted">
                    {p.needsRestartTime ? p.body.replace('{time}', restartAt ? restartAt.toLocaleString([], { weekday: 'long', hour: 'numeric', minute: '2-digit' }) : '…') : p.body}
                  </span>
                </span>
              </label>
            ))}
          </fieldset>
        )}
        {selected?.needsRestartTime && (
          <div className="flex flex-col gap-1">
            <label htmlFor={atId} className="text-xs font-medium text-muted">
              Restart time to show (your browser's time zone; users see it in theirs)
            </label>
            <Input id={atId} type="datetime-local" className="w-56" value={at} onChange={(e) => setAt(e.target.value)} />
            {timeProblem && <p className="text-xs text-danger">{timeProblem}</p>}
            <p className="text-xs text-muted">This only tells users; schedule the restart itself with Restart….</p>
          </div>
        )}
        {duplicateMessage(send.error) ? <Alert tone="warn">This message is already queued for the endpoint.</Alert> : <InlineError error={send.error} />}
      </div>
    </Dialog>
  );
}
