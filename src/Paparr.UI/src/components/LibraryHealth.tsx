import { Badge, type BadgeVariant } from "@astryxdesign/core/Badge";
import { Banner } from "@astryxdesign/core/Banner";
import { Button } from "@astryxdesign/core/Button";
import { Card } from "@astryxdesign/core/Card";
import { EmptyState } from "@astryxdesign/core/EmptyState";
import { Heading } from "@astryxdesign/core/Heading";
import { ProgressBar } from "@astryxdesign/core/ProgressBar";
import { Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from "@astryxdesign/core/Table";
import { ToggleButton } from "@astryxdesign/core/ToggleButton";
import { useCallback, useEffect, useState } from "react";
import { request } from "../api";
import type { IntegrityStatus, LibraryPage, LibraryAudit } from "../api";
import type { LatestLibraryAuditDto } from "../generated/types.gen";
import "./OperationsHeader.css";
import "./LibraryHealth.css";
import BookDownload from "./BookDownload";
import EditionDetails from "./EditionDetails";
import { hasEditionDetails } from "../edition";

type LatestAuditResponse = LatestLibraryAuditDto;

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

const statusVariants: Record<IntegrityStatus, BadgeVariant> = {
  Unchecked: "neutral",
  Verified: "success",
  Missing: "error",
  Changed: "warning",
  Unavailable: "warning",
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

  return (
    <section className="library-health" aria-labelledby="library-health-title">
      <header className="operations-header">
        <div>
          <Heading level={1} id="library-health-title">Library health</Heading>
          <p>Check each library file against its recorded SHA-256 fingerprint.</p>
        </div>
        <Button
          label={starting ? "Starting scan…" : auditIsActive ? "Scan in progress" : "Start integrity scan"}
          variant="primary"
          onClick={() => void startScan()}
          isDisabled={starting || auditIsActive || auditLoading || Boolean(auditError)}
        />
      </header>

      {auditError && <Banner status="error" role="alert" title={auditError}
        endContent={<Button label="Retry" onClick={() => setAuditRefresh((value) => value + 1)} />} />}

      <Card role="region" aria-label="Latest integrity scan" className="library-audit" padding={5}>
        <div>
          <Heading level={2}>
            {auditLoading ? "Latest integrity scan" : !audit ? "No integrity scan yet"
              : audit.status === "Completed" ? "Scan complete"
              : audit.status === "Running" ? "Checking your books" : "Scan queued"}
          </Heading>
          {auditLoading ? <p role="status">Loading scan status…</p>
            : !audit ? <p>A read-only scan verifies the files in your library.</p>
            : <p>{audit.status === "Completed"
              ? `Finished ${formatDate(audit.completedAt)}. This scan covered ${audit.totalBooks} ${audit.totalBooks === 1 ? "book" : "books"}.`
              : `Started ${formatDate(audit.createdAt)}. The scan covers books present when it began.`}</p>}
        </div>
        {audit && auditIsActive && <ProgressBar
          label={`${audit.processedBooks} of ${audit.totalBooks} books checked`}
          className="library-progress"
          max={audit.totalBooks || 1}
          value={audit.processedBooks}
          hasValueLabel
        />}
        {audit?.status === "Completed" && <div className="library-audit-results" aria-label="Latest scan results">
          <Badge variant="success" label={`${audit.verifiedBooks} verified`} />
          <Badge variant="neutral" label={`${audit.missingBooks} missing`} />
          <Badge variant="neutral" label={`${audit.changedBooks} changed`} />
          <Badge variant="neutral" label={`${audit.unavailableBooks} unavailable`} />
        </div>}
      </Card>

      <div className="library-collection">
        <Heading level={2}>Library files</Heading>
        <dl className="library-counts" aria-label="Integrity status counts">
          <div><dt>Verified</dt><dd>{result?.counts.Verified ?? "—"}</dd></div>
          <div><dt>Needs attention</dt><dd>{result ? result.counts.Missing + result.counts.Changed + result.counts.Unavailable : "—"}</dd></div>
          <div><dt>Not checked</dt><dd>{result?.counts.Unchecked ?? "—"}</dd></div>
        </dl>
        <div className="library-filters" role="group" aria-label="Filter library by integrity status">
          {filters.map((filter) => (
            <ToggleButton
              key={filter.label}
              label={filter.value ? `${filter.label}, ${result?.counts[filter.value] ?? 0} books` : filter.label}
              isPressed={status === filter.value}
              onPressedChange={() => { setStatus(filter.value); setPage(1); }}
            >
              {filter.label}{filter.value && ` · ${result?.counts[filter.value] ?? 0}`}
            </ToggleButton>
          ))}
        </div>
        {error && <Banner status="error" role="alert" title={error} endContent={<Button label="Retry" onClick={reload} />} />}
        {loading && <p role="status">Loading library…</p>}
        {!loading && !error && result && result.items.length === 0 && (
          <EmptyState
            headingLevel={3}
            title={totalLibraryBooks === 0 ? "Your library is empty" : "No books match this filter"}
            description={totalLibraryBooks === 0 ? "Imported books will appear here with their integrity status." : "Choose another status to see more of your collection."}
          />
        )}
        {!loading && !error && result && result.items.length > 0 && (
          <>
            <div className="library-table">
              <Table aria-label="Library files" verticalAlign="top">
                <TableHeader><TableRow>
                  <TableHeaderCell scope="col">Book</TableHeaderCell>
                  <TableHeaderCell scope="col">Library path</TableHeaderCell>
                  <TableHeaderCell scope="col">Integrity</TableHeaderCell>
                  <TableHeaderCell scope="col">Last checked</TableHeaderCell>
                </TableRow></TableHeader>
                <TableBody>
                  {result.items.map((book) => (
                    <TableRow key={book.id}>
                      <TableCell>
                        <div className="library-book">
                          <strong>{book.title}</strong>
                          <span>{book.author}</span>
                          {hasEditionDetails(book.edition) && (
                            <details><summary>Edition details</summary><EditionDetails edition={book.edition} /></details>
                          )}
                          <div className="library-actions">
                            <BookDownload id={book.id} title={book.title} />
                            <BookDownload id={book.id} title={book.title} kind="export" />
                          </div>
                        </div>
                      </TableCell>
                      <TableCell><span className="library-field-label" aria-hidden="true">Library path</span><code>{book.relativePath ?? "Path unavailable"}</code></TableCell>
                      <TableCell>
                        <span className="library-field-label" aria-hidden="true">Integrity</span>
                        <Badge variant={statusVariants[book.integrityStatus]} label={statusLabels[book.integrityStatus]} />
                        {book.message && <p className="library-message">{book.message}</p>}
                      </TableCell>
                      <TableCell><span className="library-field-label" aria-hidden="true">Last checked</span>{formatDate(book.checkedAt)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
            <nav className="library-pages" aria-label="Library pages">
              <span>{result.total} {result.total === 1 ? "book" : "books"}</span>
              <Button label="Previous" isDisabled={page <= 1 || loading} onClick={() => setPage((value) => value - 1)} />
              <span aria-live="polite">Page {page} of {totalPages}</span>
              <Button label="Next" isDisabled={page >= totalPages || loading} onClick={() => setPage((value) => value + 1)} />
            </nav>
          </>
        )}
      </div>
    </section>
  );
}
