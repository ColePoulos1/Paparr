import { Banner } from "@astryxdesign/core/Banner";
import { Button } from "@astryxdesign/core/Button";
import { Card } from "@astryxdesign/core/Card";
import { Heading } from "@astryxdesign/core/Heading";
import { useEffect, useState } from "react";
import { request } from "../api";
import type { StagingStatusDto } from "../generated/types.gen";
import "./StagingHealth.css";

export default function StagingHealth() {
  const [snapshot, setSnapshot] = useState<StagingStatusDto | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    let timer = 0;
    async function poll() {
      try {
        const result = await request<StagingStatusDto>(
          "/api/diagnostics/staging",
          { signal: controller.signal },
        );
        if (controller.signal.aborted) return;
        setSnapshot(result);
        setError("");
      } catch (reason) {
        if (!controller.signal.aborted)
          setError(
            reason instanceof Error
              ? reason.message
              : "Temporary file status is unavailable.",
          );
      } finally {
        if (!controller.signal.aborted)
          timer = window.setTimeout(() => void poll(), 10000);
      }
    }
    void poll();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [refresh]);
  return (
    <Card padding={5} role="region" className="staging-health" aria-label="Temporary file maintenance">
      <Heading level={2}>Temporary file maintenance</Heading>
      {error && (
        <Banner status="error" role="alert" title={error}
          description={snapshot ? "Counts are from the last successful refresh." : undefined}
          endContent={<Button label="Retry temporary file status" onClick={() => setRefresh((value) => value + 1)} />}
        />
      )}
      {!snapshot && !error && (
        <p role="status">Loading temporary file status…</p>
      )}
      {snapshot && (
        <>
          <dl>
            <div>
              <dt>Active attempts</dt>
              <dd>{snapshot.activeAttempts}</dd>
            </div>
            <div>
              <dt>Awaiting maintenance</dt>
              <dd>{snapshot.pendingMaintenance}</dd>
            </div>
            <div>
              <dt>Retained for review</dt>
              <dd>{snapshot.retained}</dd>
            </div>
            <div>
              <dt>Settled attempts</dt>
              <dd>{snapshot.settled}</dd>
            </div>
          </dl>
          {snapshot.retained > 0 && (
            <Banner status="warning" title="Some temporary files need attention."
              description="Check the server logs before removing anything." />
          )}
        </>
      )}
      <p className="worker-footnote">
        Paparr cleans only confirmed temporary files from tracked publication
        attempts. Files without confirmed ownership and older untracked files
        remain untouched. Settled attempts can include files that were already
        absent.
      </p>
    </Card>
  );
}
