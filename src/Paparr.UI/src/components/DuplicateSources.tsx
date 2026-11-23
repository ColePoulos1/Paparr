import { Banner } from "@astryxdesign/core/Banner";
import { Heading } from "@astryxdesign/core/Heading";
import { Text } from "@astryxdesign/core/Text";
import { Button } from "@astryxdesign/core/Button";
import { useEffect, useState } from "react";
import { request } from "../api";
import type { DuplicatePage } from "../api";
import "./DuplicateSources.css";

export default function DuplicateSources({ jobId }: { jobId: number }) {
  return <SourceHistory key={jobId} jobId={jobId} />;
}

function SourceHistory({ jobId }: { jobId: number }) {
  const [page, setPage] = useState(1);
  const [result, setResult] = useState<DuplicatePage | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const current = result?.page === page ? result : null;

  useEffect(() => {
    const controller = new AbortController();
    let timer = 0;
    async function poll() {
      try {
        const data = await request<DuplicatePage>(
          `/api/imports/${jobId}/duplicates?page=${page}&pageSize=25`,
          { signal: controller.signal },
        );
        if (controller.signal.aborted) return;
        setResult(data);
        setError("");
      } catch (reason) {
        if (!controller.signal.aborted)
          setError(
            reason instanceof Error
              ? reason.message
              : "Could not load duplicate files.",
          );
      } finally {
        if (!controller.signal.aborted) timer = window.setTimeout(poll, 15000);
      }
    }
    void poll();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [jobId, page, refresh]);

  const totalPages = current
    ? Math.max(1, Math.ceil(current.total / current.pageSize))
    : 1;
  function changePage(next: number) {
    setError("");
    setPage(next);
  }

  return (
    <section className="duplicate-sources" aria-label="Duplicate files">
      <div className="review-section-heading">
        <Heading level={3}>Duplicate files</Heading>
        {current && <small>{current.total} observed</small>}
      </div>
      {error && (
        <Banner
          status="error"
          role="alert"
          title={error}
          description={
            current ? "Showing the last loaded observations." : undefined
          }
          endContent={
            <Button
              variant="ghost"
              size="sm"
              label="Retry duplicate files"
              onClick={() => setRefresh((value) => value + 1)}
            />
          }
        />
      )}
      {!current && !error && (
        <Text color="secondary">Checking duplicate history…</Text>
      )}
      {current?.total === 0 && (
        <Text color="secondary">No duplicate files observed.</Text>
      )}
      {current && current.total > 0 && (
        <>
          <Text color="secondary">
            These files had the same content when discovered and were left
            untouched. Their locations may have changed since then.
          </Text>
          <ul>
            {current.items.map((source) => (
              <li key={source.id}>
                <strong>{source.fileName}</strong>
                <code>
                  {source.relativePath ?? "Outside the current ingest folder"}
                </code>
                <time dateTime={source.discoveredAt}>
                  Observed {new Date(source.discoveredAt).toLocaleString()}
                </time>
              </li>
            ))}
          </ul>
        </>
      )}
      {(page > 1 || totalPages > 1) && (
        <nav className="duplicate-pagination" aria-label="Duplicate pages">
          <Button
            variant="secondary"
            size="sm"
            label="Previous"
            isDisabled={page === 1}
            onClick={() => changePage(page - 1)}
          />
          <span>
            Page {page}
            {current ? ` of ${totalPages}` : ""}
          </span>
          <Button
            variant="secondary"
            size="sm"
            label="Next"
            isDisabled={!current || page >= totalPages}
            onClick={() => changePage(page + 1)}
          />
        </nav>
      )}
    </section>
  );
}
