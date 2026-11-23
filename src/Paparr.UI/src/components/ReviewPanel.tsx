import { Badge } from "@astryxdesign/core/Badge";
import { Banner } from "@astryxdesign/core/Banner";
import { Button } from "@astryxdesign/core/Button";
import { Field } from "@astryxdesign/core/Field";
import { Heading } from "@astryxdesign/core/Heading";
import { Icon } from "@astryxdesign/core/Icon";
import { RadioList, RadioListItem } from "@astryxdesign/core/RadioList";
import { Text } from "@astryxdesign/core/Text";
import { TextInput } from "@astryxdesign/core/TextInput";
import "./ReviewPanel.css";
import { useEffect, useState } from "react";
import { labels, request } from "../api";
import type { ImportJob, Preview } from "../api";
import DuplicateSources from "./DuplicateSources";
import EditionDetails from "./EditionDetails";
import UndoImport from "./UndoImport";
import FolderProfile from "./FolderProfile";
import { hasEditionDetails } from "../edition";

export default function ReviewPanel({
  job,
  onChange,
  onDraftChange,
  reviewUnavailable = false,
}: {
  job: ImportJob;
  onChange: (job: ImportJob) => void;
  onDraftChange?: (job: ImportJob, dirty: boolean) => void;
  reviewUnavailable?: boolean;
}) {
  const [candidateId, setCandidateId] = useState<number | undefined>(
    job.candidates[0]?.id,
  );
  const [preview, setPreview] = useState<{
    candidateId: number;
    data: Preview;
  } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(job.candidates[0]?.title ?? "");
  const [author, setAuthor] = useState(job.candidates[0]?.author ?? "");
  const [editionDraft, setEditionDraft] = useState({
    isbn: "",
    publisher: "",
    year: "",
    language: "",
  });
  const hasEditionDraft = Object.values(editionDraft).some(
    (value) => value !== "",
  );
  const candidate =
    job.candidates.find((item) => item.id === candidateId) ?? job.candidates[0];
  const hasDraft = Boolean(
    editing &&
    (title !== (candidate?.title ?? "") ||
      author !== (candidate?.author ?? "") ||
      hasEditionDraft),
  );
  const canReview = job.status === "AwaitingApproval" && !reviewUnavailable;
  const showChoices = canReview || hasDraft;
  const choice = candidate?.id;
  const currentPreview =
    preview && preview.candidateId === choice ? preview.data : null;

  useEffect(() => {
    setPreview(null);
    if (!choice || !canReview) return;
    const controller = new AbortController();
    request<Preview>(`/api/imports/${job.id}/preview/${choice}`, {
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted)
          setPreview({ candidateId: choice, data });
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setError(reason.message);
      });
    return () => controller.abort();
  }, [job.id, choice, canReview]);

  function setDraft(nextTitle: string, nextAuthor: string) {
    setTitle(nextTitle);
    setAuthor(nextAuthor);
    onDraftChange?.(
      job,
      editing &&
        (nextTitle !== (candidate?.title ?? "") ||
          nextAuthor !== (candidate?.author ?? "") ||
          hasEditionDraft),
    );
  }

  function updateEdition(field: keyof typeof editionDraft, value: string) {
    const updated = { ...editionDraft, [field]: value };
    setEditionDraft(updated);
    onDraftChange?.(
      job,
      editing &&
        (title !== (candidate?.title ?? "") ||
          author !== (candidate?.author ?? "") ||
          Object.values(updated).some((item) => item !== "")),
    );
  }

  function clearEdition() {
    setEditionDraft({ isbn: "", publisher: "", year: "", language: "" });
  }

  async function mutate(path: string, body?: unknown) {
    setBusy(true);
    setError("");
    try {
      const updated = await request<ImportJob>(path, {
        method: "POST",
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (body) {
        const manual = updated.candidates.find(
          (item) =>
            item.source === "manual" &&
            !job.candidates.some((old) => old.id === item.id),
        );
        if (manual) setCandidateId(manual.id);
        clearEdition();
        setEditing(false);
        onDraftChange?.(job, false);
      }
      onChange(updated);
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "This action could not be completed.",
      );
    } finally {
      setBusy(false);
    }
  }

  function cancelEdit() {
    setTitle(candidate?.title ?? "");
    setAuthor(candidate?.author ?? "");
    setEditing(false);
    clearEdition();
    onDraftChange?.(job, false);
  }

  const fieldState = { isReadOnly: !canReview, isDisabled: busy };
  const requiredName = { ...fieldState, required: true, maxLength: 300 };

  return (
    <section className="review-panel" aria-label="Import details">
      <header className="review-intro">
        <div className="review-kicker">
          <Text size="sm" color="secondary">
            Import #{job.id}
          </Text>
          <Badge
            label={labels[job.status]}
            variant={
              job.status === "Completed"
                ? "success"
                : job.status === "Failed"
                  ? "error"
                  : job.status === "AwaitingApproval"
                    ? "warning"
                    : "info"
            }
          />
        </div>
        <Heading level={2}>
          {job.acceptedBook?.title ??
            candidate?.title ??
            "Give this book a name"}
        </Heading>
        <Text size="sm" color="secondary">
          {job.fileName}
        </Text>
      </header>
      <FolderProfile profile={job.profile} />
      {job.lastError && job.status !== "Undoing" && (
        <Banner status="error" title={job.lastError} />
      )}
      {error && <Banner role="alert" status="error" title={error} />}
      {hasDraft && !canReview && (
        <Banner
          role="status"
          status="warning"
          title={
            reviewUnavailable
              ? "This import is no longer in the current results. Your unsaved changes are preserved here, but cannot be saved."
              : "This import is no longer awaiting review. Your unsaved changes are preserved here, but cannot be saved."
          }
        />
      )}
      {showChoices && (
        <section className="review-section" aria-label="Metadata review">
          <div className="review-section-heading">
            <Heading level={3}>Metadata choices</Heading>
            <Button
              label={editing ? "Cancel edit" : "Edit details"}
              variant="secondary"
              size="sm"
              onClick={() => {
                if (editing) cancelEdit();
                else {
                  setTitle(candidate?.title ?? "");
                  setAuthor(candidate?.author ?? "");
                  clearEdition();
                  setEditing(true);
                  onDraftChange?.(job, false);
                }
              }}
              isDisabled={busy || (!canReview && !editing)}
            />
          </div>
          {editing && (
            <form
              className="metadata-form"
              onSubmit={(event) => {
                event.preventDefault();
                if (!canReview || busy) return;
                void mutate(`/api/imports/${job.id}/metadata`, {
                  title,
                  author,
                  ...(hasEditionDraft
                    ? {
                        edition: {
                          isbn: editionDraft.isbn || null,
                          publisher: editionDraft.publisher || null,
                          publicationYear: editionDraft.year
                            ? Number(editionDraft.year)
                            : null,
                          language: editionDraft.language || null,
                        },
                      }
                    : {}),
                });
              }}
            >
              <div className="metadata-form-grid">
                <TextInput
                  {...requiredName}
                  label="Title"
                  htmlName="title"
                  value={title}
                  onChange={(value) => setDraft(value, author)}
                />
                <TextInput
                  {...requiredName}
                  label="Author"
                  htmlName="author"
                  value={author}
                  onChange={(value) => setDraft(title, value)}
                />
              </div>
              <Text size="sm" color="secondary">
                Edition details are optional. A manual choice uses only the
                details you enter.
              </Text>
              <div className="metadata-form-grid">
                <TextInput
                  {...{ ...fieldState, maxLength: 64 }}
                  label="ISBN (optional)"
                  value={editionDraft.isbn}
                  onChange={(value) => updateEdition("isbn", value)}
                />
                <TextInput
                  {...{ ...fieldState, maxLength: 300 }}
                  label="Publisher (optional)"
                  value={editionDraft.publisher}
                  onChange={(value) => updateEdition("publisher", value)}
                />
                <Field
                  label="Publication year (optional)"
                  inputID={`year-${job.id}`}
                >
                  <input
                    id={`year-${job.id}`}
                    className="edition-year"
                    type="number"
                    min={1000}
                    max={9999}
                    step={1}
                    value={editionDraft.year}
                    onChange={(event) =>
                      updateEdition("year", event.target.value)
                    }
                    readOnly={!canReview}
                    disabled={busy}
                  />
                </Field>
                <TextInput
                  {...{ ...fieldState, maxLength: 35 }}
                  label="Language tag (optional)"
                  value={editionDraft.language}
                  placeholder="e.g. en-US"
                  onChange={(value) => updateEdition("language", value)}
                />
              </div>
              <Button
                type="submit"
                variant="secondary"
                label="Save metadata choice"
                isDisabled={!canReview || busy}
              />
            </form>
          )}
          {job.candidates.length > 0 ? (
            <RadioList
              label="Metadata choices"
              isLabelHidden
              htmlName={"candidate-" + job.id}
              value={String(candidate?.id)}
              isDisabled={!canReview || busy}
              onChange={(value) => {
                const item = job.candidates.find(
                  (option) => String(option.id) === value,
                );
                if (!item) return;
                setCandidateId(item.id);
                setTitle(item.title);
                setAuthor(item.author);
                clearEdition();
                setError("");
                onDraftChange?.(job, false);
                setPreview(null);
              }}
            >
              {job.candidates.map((item) => (
                <RadioListItem
                  key={item.id}
                  value={String(item.id)}
                  align="start"
                  className="review-choice"
                  label={
                    <span className="choice-heading">
                      <strong>{item.title}</strong>
                      <Badge
                        label={
                          item.source === "manual" ? "Your edit" : item.source
                        }
                      />
                    </span>
                  }
                  description={
                    <span className="choice-description">
                      <Text>{item.author}</Text>
                      <Text size="sm" color="secondary">
                        {item.evidence}
                      </Text>
                      <EditionDetails edition={item.edition} />
                      {item.source !== "manual" && (
                        <Text size="sm" weight="semibold">
                          Match score · {Math.round(item.confidenceScore)}/100
                        </Text>
                      )}
                    </span>
                  }
                />
              ))}
            </RadioList>
          ) : (
            <Text color="secondary">
              No complete metadata was found. Add a title and author using Edit
              details.
            </Text>
          )}
          {job.candidates.some((item) => hasEditionDetails(item.edition)) && (
            <Text size="sm" color="secondary">
              Compare edition details before approving. An ISBN checksum
              validates the number, but does not confirm that it belongs to this
              file.
            </Text>
          )}
          {currentPreview && (
            <div className="review-destination">
              <Heading level={4}>Destination preview</Heading>
              <code>{currentPreview.relativePath}</code>
              <Text size="sm" color="secondary">
                {currentPreview.destinationExists
                  ? "An existing file will be verified before completing this import. "
                  : ""}
                {currentPreview.sourceRetained
                  ? "The original file will be retained."
                  : "The original is removed only after verified import completion."}
              </Text>
            </div>
          )}
          {canReview && (
            <Button
              label={busy ? "Saving…" : "Approve & import"}
              variant="primary"
              icon={<Icon icon="check" />}
              isDisabled={busy || !candidate || !currentPreview}
              onClick={() =>
                void mutate(`/api/imports/${job.id}/accept/${candidate?.id}`)
              }
            />
          )}
        </section>
      )}
      {job.status === "Failed" && (
        <Button
          label={busy ? "Queuing…" : "Retry import"}
          variant="primary"
          isDisabled={busy}
          onClick={() => void mutate(`/api/imports/${job.id}/retry`)}
        />
      )}
      {job.acceptedBook && (
        <section className="review-destination">
          <Heading level={3}>In your library</Heading>
          <Text weight="semibold">{job.acceptedBook.author}</Text>
          <EditionDetails edition={job.acceptedBook.edition} />
          <Text size="sm">
            Imported {new Date(job.acceptedBook.importedAt).toLocaleString()} ·{" "}
            {job.acceptedBook.source}
          </Text>
          <Text size="sm">
            Source cleanup:{" "}
            {job.status === "Undoing"
              ? "Paused during undo"
              : {
                  NotRequested: "Original retained",
                  Pending: "Source cleanup queued",
                  Completed: "Source cleanup completed",
                  Retained: "Source cleanup needs attention",
                }[job.cleanup.status]}{" "}
            · {job.cleanup.attemptCount}{" "}
            {job.cleanup.attemptCount === 1 ? "attempt" : "attempts"}
            {job.cleanup.error ? ` · ${job.cleanup.error}` : ""}
          </Text>
          {job.cleanup.canRetry && (
            <Button
              variant="secondary"
              label={busy ? "Retrying…" : "Retry source cleanup"}
              isDisabled={busy}
              onClick={() =>
                void mutate(`/api/imports/${job.id}/cleanup/retry`)
              }
            />
          )}
        </section>
      )}
      {(job.status === "Completed" || job.status === "Undoing") && (
        <UndoImport job={job} onChange={onChange} disabled={busy} />
      )}
      <DuplicateSources jobId={job.id} />
      <section className="review-section" aria-label="Import history">
        <div className="review-section-heading">
          <Heading level={3}>Import history</Heading>
          <Text size="sm" color="secondary">
            {job.attemptCount} {job.attemptCount === 1 ? "attempt" : "attempts"}
          </Text>
        </div>
        {job.events.length ? (
          <ol className="review-history">
            {job.events.map((event, index) => (
              <li key={index}>
                <Text>{event.message}</Text>
                <time dateTime={event.createdAt}>
                  {new Date(event.createdAt).toLocaleString()}
                </time>
              </li>
            ))}
          </ol>
        ) : (
          <Text color="secondary">No recorded events for this import.</Text>
        )}
      </section>
    </section>
  );
}
