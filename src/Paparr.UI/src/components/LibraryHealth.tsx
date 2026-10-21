import { useCallback, useEffect, useState } from "react";
import { request } from "../api";
import "./LibraryHealth.css";
import BookDownload from "./BookDownload";

type IntegrityStatus =
  "Unchecked" | "Verified" | "Missing" | "Changed" | "Unavailable";
type AuditStatus = "Queued" | "Running" | "Completed";

interface LibraryBook {
  id: number;
  title: string;
  author: string;
  relativePath: string | null;
  importedAt: string;
  integrityStatus: IntegrityStatus;
  checkedAt: string | null;
  message: string | null;
}

interface LibraryPage {
  items: LibraryBook[];
  total: number;
  page: number;
  pageSize: number;
  counts: Record<IntegrityStatus, number>;
}

interface LibraryAudit {
  id: number;
  status: AuditStatus;
  totalBooks: number;
  processedBooks: number;
  verifiedBooks: number;
  missingBooks: number;
  changedBooks: number;
  unavailableBooks: number;
  createdAt: string;
  completedAt: string | null;
}

interface LatestAuditResponse {
  audit: LibraryAudit | null;
}

const filters: { label: string; value: IntegrityStatus | null }[] = [
  { label: "All books", value: null },
  { label: "Unchecked", value: "Unchecked" },
  { label: "Verified", value: "Verified" },
  { label: "Missing", value: "Missing" },
  { label: "Changed", value: "Changed" },
  { label: "Unavailable", value: "Unavailable" },
];

const statusLabels: Record<IntegrityStatus, string> = {
  Unchecked: "Not checked",
  Verified: "Verified",
  Missing: "Missing",
  Changed: "Changed",
  Unavailable: "Unavailable",
};

function isActive(audit: LibraryAudit | null): boolean {
  return audit?.status === "Queued" || audit?.status === "Running";
}

function formatDate(value: string | null): string {
  if (!value) return "Never checked";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unknown" : date.toLocaleString();
}

export default function LibraryHealth() {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<IntegrityStatus | null>(null);
  const [result, setResult] = useState<LibraryPage | null>(null);
  const [audit, setAudit] = useState<LibraryAudit | null>(null);
  const [loading, setLoading] = useState(true);
  const [auditLoading, setAuditLoading] = useState(true);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState("");
  const [auditError, setAuditError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [auditRefresh, setAuditRefresh] = useState(0);

  const reload = useCallback(() => setRefresh((value) => value + 1), []);
  const auditIsActive = isActive(audit);

  useEffect(() => {
    const controller = new AbortController();
    const params = new URLSearchParams({ page: String(page), pageSize: "25" });
    if (status) params.set("status", status);
    setLoading(true);
    request<LibraryPage>(`/api/library?${params}`, {
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted) {
          setResult(data);
          setError("");
        }
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setError(reason.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [page, status, refresh]);

  useEffect(() => {
    const controller = new AbortController();
    let timer = 0;
    setAuditLoading(true);
    const poll = async () => {
      let delay = auditIsActive ? 3000 : 15000;
      try {
        const data = await request<LatestAuditResponse>(
          "/api/library/audits/latest",
          {
            signal: controller.signal,
          },
        );
        if (controller.signal.aborted) return;
        setAudit(data.audit);
        setAuditError("");
        setAuditLoading(false);
        delay = isActive(data.audit) ? 3000 : 15000;
        if (
          data.audit?.status === "Running" ||
          data.audit?.status === "Completed"
        )
          reload();
      } catch (reason) {
        if (!controller.signal.aborted) {
          setAuditError(
            reason instanceof Error
              ? reason.message
              : "Could not load scan status.",
          );
          setAuditLoading(false);
        }
      } finally {
        if (!controller.signal.aborted) timer = window.setTimeout(poll, delay);
      }
    };
    void poll();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [auditIsActive, auditRefresh, reload]);

  async function startScan() {
    if (starting || auditIsActive) return;
    setStarting(true);
    setAuditError("");
    try {
      const created = await request<LibraryAudit>("/api/library/audits", {
        method: "POST",
      });
      setAudit(created);
      setAuditRefresh((value) => value + 1);
      reload();
    } catch (reason) {
      setAuditError(
        reason instanceof Error ? reason.message : "Could not start the scan.",
      );
    } finally {
      setStarting(false);
    }
  }

  const totalPages = result
    ? Math.max(1, Math.ceil(result.total / result.pageSize))
    : 1;
  const totalLibraryBooks = result
    ? Object.values(result.counts).reduce((sum, count) => sum + count, 0)
    : 0;
  const active = auditIsActive;
  const progress =
    audit && audit.totalBooks > 0
      ? Math.min(
          100,
          Math.round((audit.processedBooks / audit.totalBooks) * 100),
        )
      : audit?.status === "Completed"
        ? 100
        : 0;

  return (
    <section className="library-health" aria-labelledby="library-health-title">
      <header className="page-header library-health-header">
        <div>
          <div className="eyebrow">YOUR COLLECTION</div>
          <h1 id="library-health-title">Library health</h1>
          <p>
            Check that each book still matches its recorded SHA-256 fingerprint.
            Integrity scans are read-only and never modify your books.
          </p>
        </div>
        <button
          className="button primary"
          onClick={() => void startScan()}
          disabled={starting || active || auditLoading || Boolean(auditError)}
        >
          {starting
            ? "Starting scan…"
            : active
              ? "Scan in progress"
              : "Start integrity scan"}
        </button>
      </header>

      {auditError && (
        <div className="notice error" role="alert">
          {auditError}{" "}
          <button onClick={() => setAuditRefresh((value) => value + 1)}>
            Retry
          </button>
        </div>
      )}

      <section
        className="library-audit-card"
        aria-label="Latest integrity scan"
      >
        <div className="library-audit-copy">
          <span className="tiny-label">LATEST INTEGRITY SCAN</span>
          {auditLoading ? (
            <p className="muted" role="status">
              Loading scan status…
            </p>
          ) : audit ? (
            <>
              <h2>
                {audit.status === "Completed"
                  ? "Scan complete"
                  : audit.status === "Running"
                    ? "Checking your books"
                    : "Scan queued"}
              </h2>
              <p className="muted">
                {audit.status === "Completed"
                  ? `Finished ${formatDate(audit.completedAt)}. This scan covered ${audit.totalBooks} ${audit.totalBooks === 1 ? "book" : "books"}.`
                  : `Started ${formatDate(audit.createdAt)}. The scan checks the books present when it began.`}
              </p>
            </>
          ) : (
            <>
              <h2>No integrity scan yet</h2>
              <p className="muted">
                Start a scan to compare stored fingerprints with your library
                files.
              </p>
            </>
          )}
        </div>
        {audit && active && (
          <div
            className="library-progress"
            aria-label={`${audit.processedBooks} of ${audit.totalBooks} books checked`}
          >
            <div className="library-progress-label">
              <strong>
                {audit.processedBooks} of {audit.totalBooks}
              </strong>
              <span>{progress}%</span>
            </div>
            <label className="sr-only" htmlFor="library-audit-progress">
              Integrity scan progress
            </label>
            <progress id="library-audit-progress" max={100} value={progress}>
              {progress}%
            </progress>
            <small>
              Books already in your library are not changed by this scan.
            </small>
          </div>
        )}
        {audit?.status === "Completed" && (
          <div
            className="library-audit-results"
            aria-label="Latest scan results"
          >
            <span>
              <strong>{audit.verifiedBooks}</strong> verified
            </span>
            <span>
              <strong>{audit.missingBooks}</strong> missing
            </span>
            <span>
              <strong>{audit.changedBooks}</strong> changed
            </span>
            <span>
              <strong>{audit.unavailableBooks}</strong> unavailable
            </span>
          </div>
        )}
      </section>

      <div
        className="stats library-health-stats"
        aria-label="Integrity status counts"
      >
        <div>
          <span>Verified</span>
          <strong>{result?.counts.Verified ?? "—"}</strong>
          <small>Matches recorded fingerprint</small>
        </div>
        <div>
          <span>Needs attention</span>
          <strong>
            {result
              ? result.counts.Missing +
                result.counts.Changed +
                result.counts.Unavailable
              : "—"}
          </strong>
          <small>Missing, changed, or unavailable files</small>
        </div>
        <div>
          <span>Not checked</span>
          <strong>{result?.counts.Unchecked ?? "—"}</strong>
          <small>Awaiting an integrity scan</small>
        </div>
      </div>

      <div
        className="library-filter-row"
        role="group"
        aria-label="Filter library by integrity status"
      >
        {filters.map((filter) => (
          <button
            key={filter.label}
            type="button"
            className={
              status === filter.value
                ? "library-filter active"
                : "library-filter"
            }
            aria-pressed={status === filter.value}
            aria-label={
              filter.value
                ? `${filter.label}, ${result?.counts[filter.value] ?? 0} books`
                : filter.label
            }
            onClick={() => {
              setStatus(filter.value);
              setPage(1);
            }}
          >
            {filter.label}
            {filter.value && <span>{result?.counts[filter.value] ?? 0}</span>}
          </button>
        ))}
      </div>

      {error && (
        <div className="notice error" role="alert">
          {error} <button onClick={reload}>Retry</button>
        </div>
      )}
      {loading && (
        <div className="library-loading" role="status">
          Loading library…
        </div>
      )}
      {!loading && !error && result && result.items.length === 0 && (
        <div className="empty library-empty">
          <div className="empty-book" aria-hidden="true">
            ⌕
          </div>
          <h2>
            {totalLibraryBooks === 0
              ? "Your library is empty"
              : "No books match this filter"}
          </h2>
          <p>
            {totalLibraryBooks === 0
              ? "Imported books will appear here with their integrity status."
              : "Choose another status to see more of your collection."}
          </p>
        </div>
      )}
      {!loading && !error && result && result.items.length > 0 && (
        <>
          <div className="library-table-wrap">
            <table className="library-table">
              <thead>
                <tr>
                  <th scope="col">Book</th>
                  <th scope="col">Library path</th>
                  <th scope="col">Integrity</th>
                  <th scope="col">Last checked</th>
                </tr>
              </thead>
              <tbody>
                {result.items.map((book) => (
                  <tr key={book.id}>
                    <td>
                      <strong>{book.title}</strong>
                      <span>{book.author}</span>
                      <BookDownload id={book.id} title={book.title} />
                    </td>
                    <td>
                      <code title={book.relativePath ?? undefined}>
                        {book.relativePath ?? "Path unavailable"}
                      </code>
                    </td>
                    <td>
                      <span
                        className={`library-status ${book.integrityStatus}`}
                      >
                        {statusLabels[book.integrityStatus]}
                      </span>
                      {book.message && (
                        <small className="library-message">
                          {book.message}
                        </small>
                      )}
                    </td>
                    <td>{formatDate(book.checkedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <nav className="pagination" aria-label="Library pages">
            <span>
              {result.total} {result.total === 1 ? "book" : "books"}
            </span>
            <button
              className="button secondary"
              disabled={page <= 1 || loading}
              onClick={() => setPage((value) => value - 1)}
            >
              Previous
            </button>
            <span aria-live="polite">
              Page {page} of {totalPages}
            </span>
            <button
              className="button secondary"
              disabled={page >= totalPages || loading}
              onClick={() => setPage((value) => value + 1)}
            >
              Next
            </button>
          </nav>
        </>
      )}
      <p className="library-footnote">
        A scan snapshots the books in your library when it starts. New imports
        remain available for the next scan.
      </p>
    </section>
  );
}
