import { useEffect, useState } from 'react';
import { Tag as TagIcon } from 'lucide-react';
import { api } from '../api/client';
import { useApiMutation, useApi } from '../api/hooks';
import type { BulkEndpointTagsRequest, TagDto, TagRefDto } from '../api/types';
import { Dialog } from './Dialog';
import { useToast } from './Toast';
import { Alert, Button, cx, EmptyState, InlineError, Spinner } from './ui';

const COLOR_CLASSES: Record<string, string> = {
  gray: 'bg-neutral-soft text-muted',
  blue: 'bg-blue-100 text-blue-800 dark:bg-blue-950 dark:text-blue-300',
  green: 'bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300',
  amber: 'bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300',
  red: 'bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-300',
  purple: 'bg-purple-100 text-purple-800 dark:bg-purple-950 dark:text-purple-300',
  teal: 'bg-teal-100 text-teal-800 dark:bg-teal-950 dark:text-teal-300',
  pink: 'bg-pink-100 text-pink-800 dark:bg-pink-950 dark:text-pink-300',
};

export function TagChip({ tag, className }: { tag: TagRefDto; className?: string }) {
  return (
    <span
      className={cx(
        'inline-flex max-w-40 items-center gap-1 truncate rounded-md px-1.5 py-0.5 text-xs font-medium whitespace-nowrap',
        COLOR_CLASSES[tag.color] ?? COLOR_CLASSES.gray,
        className,
      )}
      title={tag.name}
    >
      {tag.name}
    </span>
  );
}

export function TagList({ tags, max = 3 }: { tags: TagRefDto[] | null | undefined; max?: number }) {
  if (!tags || tags.length === 0) return null;
  const shown = tags.slice(0, max);
  const rest = tags.length - shown.length;
  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      {shown.map((t) => (
        <TagChip key={t.id} tag={t} />
      ))}
      {rest > 0 && (
        <span className="text-xs text-muted" title={tags.slice(max).map((t) => t.name).join(', ')}>
          +{rest}
        </span>
      )}
    </span>
  );
}

function TagCheckboxes({ tags, selected, onChange }: { tags: TagDto[]; selected: Set<string>; onChange: (next: Set<string>) => void }) {
  return (
    <div className="flex flex-col gap-1.5">
      {tags.map((t) => (
        <label key={t.id} className="flex items-start gap-2.5 rounded-md px-2 py-1.5 hover:bg-hover">
          <input
            type="checkbox"
            className="mt-0.5 size-4"
            checked={selected.has(t.id)}
            onChange={(e) => {
              const next = new Set(selected);
              if (e.target.checked) next.add(t.id);
              else next.delete(t.id);
              onChange(next);
            }}
          />
          <span className="min-w-0">
            <TagChip tag={t} />
            {t.hasPolicy && <span className="ml-2 text-xs text-muted">has a policy</span>}
            {t.description && <span className="block text-xs text-muted">{t.description}</span>}
          </span>
        </label>
      ))}
    </div>
  );
}

/** Edits the tags of one endpoint (replaces the whole set). */
export function EndpointTagsDialog({ open, onClose, endpointId, hostname, current }: {
  open: boolean;
  onClose: () => void;
  endpointId: string;
  hostname: string;
  current: TagRefDto[];
}) {
  const toast = useToast();
  const tags = useApi<TagDto[]>(open ? '/tags' : null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // Reset only when the dialog opens: `current` is a new array on every poll of the endpoint.
  const [wasOpen, setWasOpen] = useState(false);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setSelected(new Set(current.map((t) => t.id)));
  }
  const save = useApiMutation((ids: string[]) => api.put(`/endpoints/${endpointId}/tags`, { tagIds: ids }), {
    invalidate: ['/endpoints', '/tags'],
    onSuccess: () => {
      toast.success(`Tags of ${hostname} saved.`);
      onClose();
    },
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={`Tags of ${hostname}`}
      description="Tags group endpoints across customers (for example an update ring). A tag can carry a policy."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={save.isPending} disabled={!tags.data} onClick={() => save.mutate([...selected])}>
            Save tags
          </Button>
        </>
      }
    >
      {tags.isPending ? (
        <Spinner />
      ) : (tags.data ?? []).length === 0 ? (
        <EmptyState title="No tags yet" icon={<TagIcon className="size-8" aria-hidden />}>
          A super administrator creates tags under Settings → Tags.
        </EmptyState>
      ) : (
        <TagCheckboxes tags={tags.data ?? []} selected={selected} onChange={setSelected} />
      )}
      <InlineError error={tags.error ?? save.error} />
    </Dialog>
  );
}

/** Adds or removes tags on several endpoints at once. */
export function BulkTagsDialog({ open, onClose, endpointIds, onDone }: { open: boolean; onClose: () => void; endpointIds: string[]; onDone?: () => void }) {
  const toast = useToast();
  const tags = useApi<TagDto[]>(open ? '/tags' : null);
  const [mode, setMode] = useState<'add' | 'remove'>('add');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  useEffect(() => {
    if (open) {
      setSelected(new Set());
      setMode('add');
    }
  }, [open]);
  const save = useApiMutation(
    (req: BulkEndpointTagsRequest) => api.post<{ changed: number }>('/endpoints/tags/bulk', req),
    {
      invalidate: ['/endpoints', '/tags'],
      onSuccess: (r) => {
        toast.success(`Tags changed on ${r.changed} endpoint(s).`);
        onDone?.();
        onClose();
      },
    },
  );
  const ids = [...selected];

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={`${mode === 'add' ? 'Add tags to' : 'Remove tags from'} ${endpointIds.length} endpoint(s)`}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            loading={save.isPending}
            disabled={ids.length === 0}
            onClick={() => save.mutate({ endpointIds, addTagIds: mode === 'add' ? ids : null, removeTagIds: mode === 'remove' ? ids : null })}
          >
            {mode === 'add' ? 'Add tags' : 'Remove tags'}
          </Button>
        </>
      }
    >
      <div className="mb-3 inline-flex rounded-md border border-line p-0.5 text-sm" role="group" aria-label="Add or remove">
        {(['add', 'remove'] as const).map((m) => (
          <button
            key={m}
            type="button"
            aria-pressed={mode === m}
            onClick={() => setMode(m)}
            className={cx('rounded px-3 py-1', mode === m ? 'bg-accent-soft font-medium text-accent' : 'text-muted hover:text-fg')}
          >
            {m === 'add' ? 'Add' : 'Remove'}
          </button>
        ))}
      </div>
      {tags.isPending ? (
        <Spinner />
      ) : (tags.data ?? []).length === 0 ? (
        <Alert tone="info">No tags yet. A super administrator creates tags under Settings → Tags.</Alert>
      ) : (
        <TagCheckboxes tags={tags.data ?? []} selected={selected} onChange={setSelected} />
      )}
      <InlineError error={tags.error ?? save.error} />
    </Dialog>
  );
}
