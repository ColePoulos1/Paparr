import { Banner } from "@astryxdesign/core/Banner";
import { Heading } from "@astryxdesign/core/Heading";
import { Text } from "@astryxdesign/core/Text";
import { Button } from "@astryxdesign/core/Button";
import { useEffect, useRef, useState } from "react";
import { request } from "../api";
import type { ImportJob } from "../api";
import type { UndoPreviewDto } from "../generated/types.gen";
import "./UndoImport.css";

export default function UndoImport({
  job,
  onChange,
  disabled = false,
}: {
  job: ImportJob;
  onChange: (job: ImportJob) => void;
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(job.status === "Undoing");
  const [snapshot, setSnapshot] = useState<{
    revision: string;
    data: UndoPreviewDto;
  } | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [operationId] = useState(() => crypto.randomUUID());
  const mutation = useRef<AbortController | null>(null);
  const preview = snapshot?.revision === job.revision ? snapshot.data : null;
  const expanded = open || job.status === "Undoing";
  const operationError =
    preview?.error ?? (job.status === "Undoing" ? job.lastError : null);

  useEffect(() => () => mutation.current?.abort(), []);
  useEffect(() => {
    if (!expanded) return;
    const controller = new AbortController();
    setLoading(true);
    setError("");
    request<UndoPreviewDto>(`/api/imports/${job.id}/undo-preview`, {
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted)
          setSnapshot({ revision: job.revision, data });
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted)
          setError(
            reason instanceof Error
              ? reason.message
              : "Undo status is unavailable.",
          );
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [expanded, job.id, job.revision, refresh]);

  async function undo(retry: boolean) {
    if (!preview || busy) return;
    const controller = new AbortController();
    mutation.current = controller;
    setBusy(true);
    setError("");
    try {
      const updated = await request<ImportJob>(
        `/api/imports/${job.id}/undo${retry ? "/retry" : ""}`,
        {
          method: "POST",
          signal: controller.signal,
          body: JSON.stringify(
            retry
              ? { operationId: preview.operationId }
              : { revision: preview.revision, operationId },
          ),
        },
      );
      if (!controller.signal.aborted) {
        onChange(updated);
        setRefresh((value) => value + 1);
      }
    } catch (reason) {
      if (!controller.signal.aborted)
        setError(
          reason instanceof Error
            ? reason.message
            : "Undo could not be completed. Refresh its status before trying again.",
        );
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  return (
    <section className="undo-import" aria-label="Undo import">
      {!expanded ? (
        <Button
          variant="ghost"
          size="sm"
          label="Undo import…"
          isDisabled={disabled}
          onClick={() => setOpen(true)}
        />
      ) : (
        <>
          <Heading level={3}>
            {job.status === "Undoing"
              ? "Returning to review"
              : "Return this import to review?"}
          </Heading>
          <p>
            Paparr will remove the verified library copy and keep the original
            in your watch folder. You can choose metadata and approve it again.
          </p>
          {loading && <p role="status">Checking both copies…</p>}
          {error && <Banner status="error" role="alert" title={error} />}
          {operationError && (
            <Banner status="error" role="alert" title={operationError} />
          )}
          {preview && (
            <>
              <dl>
                <div>
                  <dt>Keep original</dt>
                  <dd>
                    {preview.sourceRelativePath ?? "Original path unavailable"}
                  </dd>
                </div>
                <div>
                  <dt>Remove library copy</dt>
                  <dd>
                    {preview.libraryRelativePath ?? "Library path unavailable"}
                  </dd>
                </div>
              </dl>
              {!preview.canUndo && preview.reason && (
                <Banner status="warning" role="status" title={preview.reason} />
              )}
              {job.status === "Undoing" && (
                <Text color="secondary">
                  This request is saved. Background work can resume it after a
                  restart. When it finishes, find this file under Needs review.
                </Text>
              )}
            </>
          )}
          <div className="undo-actions">
            {job.status === "Completed" && (
              <Button
                variant="destructive"
                size="sm"
                label={busy ? "Returning to review…" : "Undo import"}
                isDisabled={disabled || loading || busy || !preview?.canUndo}
                onClick={() => void undo(false)}
              />
            )}
            {job.status === "Undoing" && preview?.canRetry && (
              <Button
                variant="secondary"
                size="sm"
                label={busy ? "Retrying…" : "Retry undo"}
                isDisabled={disabled || loading || busy}
                onClick={() => void undo(true)}
              />
            )}
            <Button
              variant="ghost"
              size="sm"
              label="Refresh undo status"
              isDisabled={busy || loading}
              onClick={() => setRefresh((value) => value + 1)}
            />
            {job.status === "Completed" && (
              <Button
                variant="ghost"
                size="sm"
                label="Cancel"
                isDisabled={busy}
                onClick={() => setOpen(false)}
              />
            )}
          </div>
        </>
      )}
    </section>
  );
}
