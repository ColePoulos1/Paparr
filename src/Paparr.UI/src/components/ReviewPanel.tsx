import { useEffect, useState } from "react";
import { labels, request } from "../api";
import type { ImportJob, Preview } from "../api";
import DuplicateSources from "./DuplicateSources";
import EditionDetails from "./EditionDetails";
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
  const [editionDraft, setEditionDraft] = useState({ isbn: "", publisher: "", year: "", language: "" });
  const hasEditionDraft = Object.values(editionDraft).some((value) => value !== "");
  const candidate =
    job.candidates.find((item) => item.id === candidateId) ?? job.candidates[0];
  const hasDraft = Boolean(
    editing &&
    (title !== (candidate?.title ?? "") ||
      author !== (candidate?.author ?? "") || hasEditionDraft),
  );
  const canReview = job.status === "AwaitingApproval" && !reviewUnavailable;
  const showChoices = canReview || hasDraft;
  const choice = candidate?.id;
  const currentPreview = preview && preview.candidateId === choice ? preview.data : null;

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
          nextAuthor !== (candidate?.author ?? "") || hasEditionDraft),
    );
  }

  function updateEdition(field: keyof typeof editionDraft, value: string) {
    const updated = { ...editionDraft, [field]: value };
    setEditionDraft(updated);
    onDraftChange?.(job, editing && (title !== (candidate?.title ?? "") ||
      author !== (candidate?.author ?? "") || Object.values(updated).some((item) => item !== "")));
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

  return (
    <section className="review-panel" aria-label="Import details">
      <div className="panel-heading">
        <span className="eyebrow">IMPORT #{job.id}</span>
        <span className={"badge " + job.status}>{labels[job.status]}</span>
      </div>
      <h2>
        {job.acceptedBook?.title ?? candidate?.title ?? "Give this book a name"}
      </h2>
      <p className="muted filename">{job.fileName}</p>
      {job.lastError && <div className="notice error">{job.lastError}</div>}
      {error && (
        <div className="notice error" role="alert">
          {error}
        </div>
      )}
      {hasDraft && !canReview && (
        <div className="notice" role="status">
          {reviewUnavailable
            ? "This import is no longer in the current results. Your unsaved changes are preserved here, but cannot be saved."
            : "This import is no longer awaiting review. Your unsaved changes are preserved here, but cannot be saved."}
        </div>
      )}
      {showChoices && (
        <>
          <div className="section-heading">
            <h3>Choose the best metadata</h3>
            <button
              className="text-button"
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
              disabled={busy || (!canReview && !editing)}
            >
              {editing ? "Cancel edit" : "Edit details"}
            </button>
          </div>
          {editing && (
            <form
              className="edit-form"
              onSubmit={(event) => {
                event.preventDefault();
                if (!canReview || busy) return;
                void mutate(`/api/imports/${job.id}/metadata`, {
                  title,
                  author,
                  ...(hasEditionDraft ? { edition: {
                    isbn: editionDraft.isbn || null,
                    publisher: editionDraft.publisher || null,
                    publicationYear: editionDraft.year ? Number(editionDraft.year) : null,
                    language: editionDraft.language || null,
                  } } : {}),
                });
              }}
            >
              <label>
                Title
                <input
                  name="title"
                  value={title}
                  onChange={(event) => setDraft(event.target.value, author)}
                  required
                  maxLength={300}
                  readOnly={!canReview}
                  disabled={busy}
                />
              </label>
              <label>
                Author
                <input
                  name="author"
                  value={author}
                  onChange={(event) => setDraft(title, event.target.value)}
                  required
                  maxLength={300}
                  readOnly={!canReview}
                  disabled={busy}
                />
              </label>
              <p className="muted">Edition details are optional. Provider details are not copied into a new manual choice.</p>
              <div className="edition-edit-grid">
                <label>ISBN (optional)<input value={editionDraft.isbn} maxLength={64}
                  onChange={(event) => updateEdition("isbn", event.target.value)} readOnly={!canReview} disabled={busy} /></label>
                <label>Publisher (optional)<input value={editionDraft.publisher} maxLength={300}
                  onChange={(event) => updateEdition("publisher", event.target.value)} readOnly={!canReview} disabled={busy} /></label>
                <label>Publication year (optional)<input type="number" min={1000} max={9999} step={1} value={editionDraft.year}
                  onChange={(event) => updateEdition("year", event.target.value)} readOnly={!canReview} disabled={busy} /></label>
                <label>Language tag (optional)<input value={editionDraft.language} maxLength={35} placeholder="e.g. en-US"
                  onChange={(event) => updateEdition("language", event.target.value)} readOnly={!canReview} disabled={busy} /></label>
              </div>
              <button
                className="button secondary"
                disabled={!canReview || busy}
              >
                Save metadata choice
              </button>
            </form>
          )}
          <fieldset className="candidate-list" disabled={!canReview || busy}>
            <legend className="sr-only">Metadata choices</legend>
            {job.candidates.map((item) => (
              <label
                key={item.id}
                className={
                  "candidate " + (candidate?.id === item.id ? "chosen" : "")
                }
              >
                <input
                  type="radio"
                  name={"candidate-" + job.id}
                  checked={candidate?.id === item.id}
                  onChange={() => {
                    setCandidateId(item.id);
                    setTitle(item.title);
                    setAuthor(item.author);
                    clearEdition();
                    setError("");
                    onDraftChange?.(job, false);
                    setPreview(null);
                  }}
                />
                <span>
                  <span className="candidate-top">
                    <strong>{item.title}</strong>
                    <small>
                      {item.source === "manual" ? "Your edit" : item.source}
                    </small>
                  </span>
                  <span className="candidate-author">{item.author}</span>
                  <span className="evidence">{item.evidence}</span>
                  <EditionDetails edition={item.edition} />
                  {item.source !== "manual" && (
                    <span className="match-score">
                      Match score · {Math.round(item.confidenceScore)}/100
                    </span>
                  )}
                </span>
              </label>
            ))}
          </fieldset>
          {job.candidates.some((item) => hasEditionDetails(item.edition)) && (
            <p className="muted edition-note">
              Compare edition details before approving. An ISBN checksum validates the number,
              but does not confirm that it belongs to this file.
            </p>
          )}
          {job.candidates.length === 0 && (
            <p className="muted">
              No complete metadata was found. Add a title and author using Edit
              details.
            </p>
          )}
          {currentPreview && (
            <div className="destination">
              <span className="tiny-label">DESTINATION PREVIEW</span>
              <code>{currentPreview.relativePath}</code>
              <small>
                {currentPreview.destinationExists
                  ? "An existing file will be verified before completing this import. "
                  : ""}
                {currentPreview.sourceRetained
                  ? "The original file will be retained."
                  : "The original is removed only after verified import completion."}
              </small>
            </div>
          )}
          {canReview && (
            <button
              className="button primary import-button"
              disabled={busy || !candidate || !currentPreview}
              onClick={() =>
                void mutate(`/api/imports/${job.id}/accept/${candidate?.id}`)
              }
            >
              {busy ? "Saving…" : "Approve & import"}
            </button>
          )}
        </>
      )}
      {job.status === "Failed" && (
        <button
          className="button primary"
          disabled={busy}
          onClick={() => void mutate(`/api/imports/${job.id}/retry`)}
        >
          {busy ? "Queuing…" : "Retry import"}
        </button>
      )}
      {job.acceptedBook && (
        <div className="destination">
          <span className="tiny-label">IN YOUR LIBRARY</span>
          <strong>{job.acceptedBook.author}</strong>
          <EditionDetails edition={job.acceptedBook.edition} />
          <small>
            Imported {new Date(job.acceptedBook.importedAt).toLocaleString()} ·{" "}
            {job.acceptedBook.source}
          </small>
          <small>
            Source cleanup: {{
              NotRequested: "Original retained",
              Pending: "Source cleanup queued",
              Completed: "Source cleanup completed",
              Retained: "Source cleanup needs attention",
            }[job.cleanup.status]} · {job.cleanup.attemptCount}{" "}
            {job.cleanup.attemptCount === 1 ? "attempt" : "attempts"}
            {job.cleanup.error ? ` · ${job.cleanup.error}` : ""}
          </small>
          {job.cleanup.canRetry && (
            <button
              className="button secondary"
              disabled={busy}
              onClick={() =>
                void mutate(`/api/imports/${job.id}/cleanup/retry`)
              }
            >
              {busy ? "Retrying…" : "Retry source cleanup"}
            </button>
          )}
        </div>
      )}
      <DuplicateSources jobId={job.id} />
      <div className="section-heading timeline-heading">
        <h3>Import history</h3>
        <small>
          {job.attemptCount} {job.attemptCount === 1 ? "attempt" : "attempts"}
        </small>
      </div>
      <ol className="timeline">
        {job.events.map((event, index) => (
          <li key={index}>
            <span>{event.message}</span>
            <time dateTime={event.createdAt}>
              {new Date(event.createdAt).toLocaleString()}
            </time>
          </li>
        ))}
      </ol>
      {job.events.length === 0 && (
        <p className="muted">No recorded events for this import.</p>
      )}
    </section>
  );
}
