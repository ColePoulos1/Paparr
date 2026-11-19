import { useCallback, useEffect, useState } from "react";
import { labels, request, statuses } from "./api";
import type { ImportJob, Page, Status, Summary } from "./api";
import ReviewPanel from "./components/ReviewPanel";
import LibraryHealth from "./components/LibraryHealth";
import WorkerHealth from "./components/WorkerHealth";

function locationView(): "imports" | "library" | "worker" {
  const view = new URLSearchParams(window.location.search).get("view");
  return view === "library" || view === "worker" ? view : "imports";
}

function locationFilter() {
  const params = new URLSearchParams(window.location.search);
  const value = params.get("status");
  return {
    status: statuses.includes(value as Status)
      ? (value as Status)
      : ("AwaitingApproval" as Status),
    search: params.get("q") ?? "",
    page: Math.min(
      100000,
      Math.max(1, Math.trunc(Number(params.get("page"))) || 1),
    ),
  };
}

interface AppProps {
  signOut?: () => Promise<void>;
}

export default function App({ signOut }: AppProps) {
  const [filter, setFilter] = useState(locationFilter);
  const [view, setView] = useState(locationView);
  const [result, setResult] = useState<Page>({
    items: [],
    total: 0,
    page: 1,
    pageSize: 25,
  });
  const [summary, setSummary] = useState<Summary | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [detail, setDetail] = useState<ImportJob | null>(null);
  const [detailError, setDetailError] = useState<{ id: number; message: string } | null>(null);
  const [draftJob, setDraftJob] = useState<ImportJob | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [signingOut, setSigningOut] = useState(false);
  const [undone, setUndone] = useState(false);
  const activeDraft = draftJob && (selectedId === null || draftJob.id === selectedId)
    ? draftJob
    : null;
  const selectedRow = result.items.find((job) => job.id === (selectedId ?? activeDraft?.id))
    ?? (activeDraft ? undefined : result.items[0]);
  const activeId = selectedRow?.id ?? activeDraft?.id;
  const selected = detail?.id === activeId ? detail : activeDraft;
  const reload = useCallback(() => setRefresh((n) => n + 1), []);

  useEffect(() => {
    const pop = () => {
      setDraftJob(null);
      setSelectedId(null);
      setFilter(locationFilter());
      setView(locationView());
    };
    window.addEventListener("popstate", pop);
    return () => window.removeEventListener("popstate", pop);
  }, []);

  useEffect(() => {
    const interval = window.setInterval(reload, 15000);
    return () => window.clearInterval(interval);
  }, [reload]);

  useEffect(() => {
    const controller = new AbortController();
    request<Summary>("/api/imports/summary", { signal: controller.signal })
      .then((counts) => { if (!controller.signal.aborted) setSummary(counts); })
      .catch(() => { if (!controller.signal.aborted) setSummary(null); });
    return () => controller.abort();
  }, [refresh, view]);

  useEffect(() => {
    if (view !== "imports") return;
    const controller = new AbortController();
    const params = new URLSearchParams({
      status: filter.status,
      search: filter.search,
      page: String(filter.page),
      pageSize: "25",
    });
    setLoading(true);
    request<Page>("/api/imports?" + params, { signal: controller.signal })
      .then((page) => {
        if (controller.signal.aborted) return;
        setResult(page);
        setError("");
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setError(reason.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [filter, refresh, view]);

  useEffect(() => {
    if (view !== "imports" || !selectedRow) return;
    const controller = new AbortController();
    const id = selectedRow.id;
    request<ImportJob>(`/api/imports/${id}`, { signal: controller.signal })
      .then((job) => {
        if (controller.signal.aborted) return;
        setDetail(job);
        setDetailError(null);
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setDetailError({ id, message: reason.message });
      });
    return () => controller.abort();
  }, [selectedRow, view]);

  function navigate(next: typeof filter) {
    setUndone(false);
    setView("imports");
    setDraftJob(null);
    setSelectedId(null);
    setFilter(next);
    const params = new URLSearchParams({ status: next.status });
    if (next.search) params.set("q", next.search);
    if (next.page > 1) params.set("page", String(next.page));
    window.history.pushState(null, "", "?" + params);
  }

  function changed(job: ImportJob) {
    if (job.status === "AwaitingApproval" && (selected?.status === "Completed" || selected?.status === "Undoing"))
      setUndone(true);
    setDetail((previous) => previous?.id === job.id ? job : previous);
    setDraftJob((previous) => (previous?.id === job.id ? null : previous));
    const book = job.acceptedBook ?? job.candidates[0];
    setResult((previous) => ({
      ...previous,
      items: previous.items.map((item) => item.id === job.id
        ? { ...item, status: job.status, title: book?.title ?? null, author: book?.author ?? null }
        : item),
    }));
    reload();
  }

  return (
    <div className={view === "imports" ? "workspace accession-ledger" : "workspace"}>
      <aside className="sidebar">
        <a className="brand" href="/">
          <span className="brand-mark">p</span>
          <span>
            Paparr<small>A place for every book.</small>
          </span>
        </a>
        <div className="nav-caption">YOUR WORKSPACE</div>
        <nav aria-label="Import status">
          {statuses.map((status) => (
            <button
              key={status}
              className={
                view === "imports" && filter.status === status
                  ? "nav-item active"
                  : "nav-item"
              }
              aria-current={
                view === "imports" && filter.status === status ? "page" : undefined
              }
              onClick={() => navigate({ ...filter, status, page: 1 })}
            >
              <span className={"status-dot " + status} />
              <span>{labels[status]}</span>
              <span className="nav-count">{summary?.[status] ?? "—"}</span>
            </button>
          ))}
          <button
            className={view === "library" ? "nav-item active" : "nav-item"}
            aria-current={view === "library" ? "page" : undefined}
            onClick={() => {
              setDraftJob(null);
              setSelectedId(null);
              setView("library");
              window.history.pushState(null, "", "?view=library");
            }}
          >
            <span className="status-dot Completed" />
            <span>Library health</span>
          </button>
          <button
            className={view === "worker" ? "nav-item active" : "nav-item"}
            aria-current={view === "worker" ? "page" : undefined}
            onClick={() => {
              setDraftJob(null);
              setSelectedId(null);
              setView("worker");
              window.history.pushState(null, "", "?view=worker");
            }}
          >
            <span className="status-dot Processing" />
            <span>Worker status</span>
          </button>
        </nav>
        {signOut && (
          <button
            className="nav-item auth-signout"
            disabled={signingOut}
            onClick={async () => {
              setSigningOut(true);
              try { await signOut(); }
              finally { setSigningOut(false); }
            }}
          >
            <span aria-hidden="true">↪</span>
            <span>{signingOut ? "Signing out…" : "Sign out"}</span>
          </button>
        )}
        <div className="sidebar-note">
          <span className="tiny-label">THOUGHTFULLY ORGANIZED</span>
          <p>
            Your files. Your library.
            <br />
            Every import has a history.
          </p>
        </div>
        <span className="docs-link">Paparr · Self-hosted ebook intake</span>
      </aside>

      <main className="main">
        {view === "library" ? (
          <LibraryHealth />
        ) : view === "worker" ? (
          <WorkerHealth />
        ) : (
          <>
            <header className="page-header">
              <h1>{labels[filter.status]}</h1>
              <button
                className="button secondary"
                onClick={reload}
                disabled={loading}
              >
                {loading ? "Refreshing…" : "Refresh"}
              </button>
            </header>
            <div className="stats" aria-label="Import overview">
              <div>
                <span>Ready for review</span>
                <strong>{summary?.AwaitingApproval ?? "—"}</strong>
              </div>
              <div>
                <span>In progress</span>
                <strong>
                  {summary
                    ? summary.Pending + summary.Processing + summary.Publishing + (summary.Undoing ?? 0)
                    : "—"}
                </strong>
              </div>
              <div>
                <span>Safely imported</span>
                <strong>{summary?.Completed ?? "—"}</strong>
              </div>
            </div>
            <form
              className="toolbar"
              onSubmit={(event) => {
                event.preventDefault();
                const data = new FormData(event.currentTarget);
                navigate({
                  ...filter,
                  search: String(data.get("search") ?? ""),
                  page: 1,
                });
              }}
            >
              <label className="search-field">
                <span className="sr-only">Search imports</span>
                <svg aria-hidden="true" width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round">
                  <circle cx="6.5" cy="6.5" r="4" />
                  <path d="m9.5 9.5 3 3" />
                </svg>
                <input
                  key={filter.search}
                  name="search"
                  defaultValue={filter.search}
                  placeholder="Search by title, author, or filename"
                  maxLength={200}
                />
              </label>
              <button className="button secondary" type="submit">
                Search
              </button>
              <span className="result-count">
                {result.total} {result.total === 1 ? "book" : "books"}
              </span>
            </form>
            {error && (
              <div className="notice error" role="alert">
                {error} <button onClick={reload}>Try again</button>
              </div>
            )}
            {undone && <div className="notice" role="status">The import returned to review. Your original file is retained.{" "}
              <button className="text-button" onClick={() => navigate({ status: "AwaitingApproval", search: "", page: 1 })}>Review again</button>
            </div>}
            {loading && result.items.length === 0 && !selected ? (
              <div className="empty" role="status">
                Loading your imports…
              </div>
            ) : result.items.length === 0 && !selected ? (
              <div className="empty">
                <div className="empty-book" aria-hidden="true">
                  ▤
                </div>
                <h2>
                  {filter.search
                    ? "No matching books"
                    : filter.status === "AwaitingApproval"
                      ? "Your review desk is clear"
                      : "Nothing here yet"}
                </h2>
                <p>
                  {filter.search
                    ? "Try another title, author, or filename."
                    : "Add EPUB or PDF files to your watch folder. New imports will appear here."}
                </p>
              </div>
            ) : (
              <div className="review-workspace">
                <section className="book-list" aria-label="Books">
                  {result.items.map((job) => (
                      <button
                        key={job.id}
                        className={
                          "book-row " +
                          (activeId === job.id ? "selected" : "")
                        }
                        aria-pressed={activeId === job.id}
                        onClick={() => {
                          if (activeId !== job.id) setDraftJob(null);
                          setSelectedId(job.id);
                        }}
                      >
                        <span className="book-cover" aria-hidden="true">
                          <span>
                            {(job.title ?? job.fileName).slice(0, 1)}
                          </span>
                          <small>
                            {job.fileName.split(".").pop()?.toUpperCase()}
                          </small>
                        </span>
                        <span className="book-row-info">
                          <strong>{job.title ?? job.fileName}</strong>
                          <span>{job.author ?? "Metadata needed"}</span>
                          <small>{job.fileName}</small>
                          <span className={"badge " + job.status}>
                            {labels[job.status]}
                          </span>
                        </span>
                      </button>
                  ))}
                </section>
                {selectedRow && detailError && detailError.id === activeId && (
                  <div className="notice error" role="alert">
                    {detailError.message} <button onClick={reload}>Try again</button>
                  </div>
                )}
                {selectedRow && !selected && detailError?.id !== activeId && (
                  <div className="review-panel" role="status">Loading import details…</div>
                )}
                {selected && (
                  <ReviewPanel
                    key={selected.id}
                    job={selected}
                    onChange={changed}
                    onDraftChange={(job, dirty) =>
                      setDraftJob((previous) => {
                        if (dirty) return job;
                        return previous?.id === job.id ? null : previous;
                      })
                    }
                    reviewUnavailable={!selectedRow}
                  />
                )}
              </div>
            )}
            <footer className="pagination">
              <span>
                Page {filter.page} of{" "}
                {Math.max(1, Math.ceil(result.total / 25))}
              </span>
              <button
                className="button secondary"
                disabled={filter.page <= 1 || loading}
                onClick={() => navigate({ ...filter, page: filter.page - 1 })}
              >
                Previous
              </button>
              <button
                className="button secondary"
                disabled={filter.page * 25 >= result.total || loading}
                onClick={() => navigate({ ...filter, page: filter.page + 1 })}
              >
                Next
              </button>
            </footer>
          </>
        )}
      </main>
    </div>
  );
}
