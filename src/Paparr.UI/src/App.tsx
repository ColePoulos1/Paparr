import { useCallback, useEffect, useState } from "react";
import { labels, request, statuses } from "./api";
import type { ImportJob, Page, Status, Summary } from "./api";
import ReviewPanel from "./components/ReviewPanel";
import LibraryHealth from "./components/LibraryHealth";

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
  const [libraryView, setLibraryView] = useState(
    () => new URLSearchParams(window.location.search).get("view") === "library",
  );
  const [result, setResult] = useState<Page>({
    items: [],
    total: 0,
    page: 1,
    pageSize: 25,
  });
  const [summary, setSummary] = useState<Summary | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [draftJob, setDraftJob] = useState<ImportJob | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [signingOut, setSigningOut] = useState(false);
  const selectedFromResult = result.items.find((job) => job.id === selectedId);
  const selected =
    selectedFromResult ??
    (draftJob && (selectedId === null || draftJob.id === selectedId)
      ? draftJob
      : undefined) ??
    result.items[0];
  const selectedIsFilteredOut = Boolean(
    selected && !result.items.some((job) => job.id === selected.id),
  );
  const reload = useCallback(() => setRefresh((n) => n + 1), []);

  useEffect(() => {
    const pop = () => {
      setDraftJob(null);
      setSelectedId(null);
      setFilter(locationFilter());
      setLibraryView(
        new URLSearchParams(window.location.search).get("view") === "library",
      );
    };
    window.addEventListener("popstate", pop);
    return () => window.removeEventListener("popstate", pop);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    if (libraryView) {
      request<Summary>("/api/imports/summary", { signal: controller.signal })
        .then((counts) => { if (!controller.signal.aborted) setSummary(counts); })
        .catch(() => { if (!controller.signal.aborted) setSummary(null); });
      const interval = window.setInterval(reload, 15000);
      return () => {
        controller.abort();
        window.clearInterval(interval);
      };
    }
    const params = new URLSearchParams({
      status: filter.status,
      search: filter.search,
      page: String(filter.page),
      pageSize: "25",
    });
    setLoading(true);
    Promise.all([
      request<Page>("/api/imports?" + params, { signal: controller.signal }),
      request<Summary>("/api/imports/summary", { signal: controller.signal }),
    ])
      .then(([page, counts]) => {
        if (controller.signal.aborted) return;
        setResult(page);
        setDraftJob((previous) =>
          previous
            ? (page.items.find((job) => job.id === previous.id) ?? previous)
            : null,
        );
        setSummary(counts);
        setError("");
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setError(reason.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    const interval = window.setInterval(reload, 15000);
    return () => {
      controller.abort();
      window.clearInterval(interval);
    };
  }, [filter, refresh, reload, libraryView]);

  function navigate(next: typeof filter) {
    setLibraryView(false);
    setDraftJob(null);
    setSelectedId(null);
    setFilter(next);
    const params = new URLSearchParams({ status: next.status });
    if (next.search) params.set("q", next.search);
    if (next.page > 1) params.set("page", String(next.page));
    window.history.pushState(null, "", "?" + params);
  }

  function changed(job: ImportJob) {
    setDraftJob((previous) => (previous?.id === job.id ? null : previous));
    setResult((previous) => ({
      ...previous,
      items: previous.items.map((item) => (item.id === job.id ? job : item)),
    }));
    reload();
  }

  return (
    <div className="workspace">
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
                !libraryView && filter.status === status
                  ? "nav-item active"
                  : "nav-item"
              }
              aria-current={
                !libraryView && filter.status === status ? "page" : undefined
              }
              onClick={() => navigate({ ...filter, status, page: 1 })}
            >
              <span className={"status-dot " + status} />
              <span>{labels[status]}</span>
              <span className="nav-count">{summary?.[status] ?? "—"}</span>
            </button>
          ))}
          <button
            className={libraryView ? "nav-item active" : "nav-item"}
            aria-current={libraryView ? "page" : undefined}
            onClick={() => {
              setDraftJob(null);
              setSelectedId(null);
              setLibraryView(true);
              window.history.pushState(null, "", "?view=library");
            }}
          >
            <span className="status-dot Completed" />
            <span>Library health</span>
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
        {libraryView ? (
          <LibraryHealth />
        ) : (
          <>
            <header className="page-header">
              <div>
                <div className="eyebrow">LIBRARY INTAKE</div>
                <h1>{labels[filter.status]}</h1>
                <p>
                  Good metadata makes a great library. Give every book the right
                  home.
                </p>
              </div>
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
                <small>Waiting for your decision</small>
              </div>
              <div>
                <span>In progress</span>
                <strong>
                  {summary
                    ? summary.Pending + summary.Processing + summary.Publishing
                    : "—"}
                </strong>
                <small>From discovery to library</small>
              </div>
              <div>
                <span>Safely imported</span>
                <strong>{summary?.Completed ?? "—"}</strong>
                <small>Verified library copies</small>
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
                <span aria-hidden="true">⌕</span>
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
                  {result.items.map((job) => {
                    const book = job.acceptedBook ?? job.candidates[0];
                    return (
                      <button
                        key={job.id}
                        className={
                          "book-row " +
                          (selected?.id === job.id ? "selected" : "")
                        }
                        aria-pressed={selected?.id === job.id}
                        onClick={() => {
                          if (selected?.id !== job.id) setDraftJob(null);
                          setSelectedId(job.id);
                        }}
                      >
                        <span className="book-cover" aria-hidden="true">
                          <span>
                            {(book?.title ?? job.fileName).slice(0, 1)}
                          </span>
                          <small>
                            {job.fileName.split(".").pop()?.toUpperCase()}
                          </small>
                        </span>
                        <span className="book-row-info">
                          <strong>{book?.title ?? job.fileName}</strong>
                          <span>{book?.author ?? "Metadata needed"}</span>
                          <small>{job.fileName}</small>
                          <span className={"badge " + job.status}>
                            {labels[job.status]}
                          </span>
                        </span>
                      </button>
                    );
                  })}
                </section>
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
                    reviewUnavailable={selectedIsFilteredOut}
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
