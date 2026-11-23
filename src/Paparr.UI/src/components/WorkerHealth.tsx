import { Badge } from "@astryxdesign/core/Badge";
import { Banner } from "@astryxdesign/core/Banner";
import { Button } from "@astryxdesign/core/Button";
import { Card } from "@astryxdesign/core/Card";
import { Heading } from "@astryxdesign/core/Heading";
import { useEffect, useState } from "react";
import { request } from "../api";
import type { WorkerSnapshot } from "../api";
import "./OperationsHeader.css";
import "./WorkerHealth.css";
import StagingHealth from "./StagingHealth";

const stageNames: Record<string, string> = {
  queuedWork: "Queued imports",
  sourceCleanup: "File maintenance",
  libraryAudit: "Library integrity",
  discovery: "File discovery",
};

const explanations = {
  Disabled:
    "The worker is disabled in configuration. New files and queued work will wait until it is enabled.",
  Starting: "The worker has not reported its first stage yet.",
  Running:
    "The worker is processing a stage. Individual import results are recorded in import history.",
  Idle: "The latest stage finished. The worker will check for more work on its next cycle.",
  Delayed:
    "No recent stage progress was reported. A large file or slow storage can cause this. Check API logs if it continues.",
};

function date(value: string | null) {
  return value ? new Date(value).toLocaleString() : "Not yet";
}

export default function WorkerHealth() {
  const [snapshot, setSnapshot] = useState<WorkerSnapshot | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [showTemporaryFiles, setShowTemporaryFiles] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    let timer = 0;
    async function poll() {
      try {
        const result = await request<WorkerSnapshot>(
          "/api/diagnostics/worker",
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
              : "Could not load worker status.",
          );
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
          timer = window.setTimeout(poll, 10000);
        }
      }
    }
    setLoading(true);
    void poll();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [refresh]);

  return (
    <section className="worker-health" aria-label="Worker status">
      <header className="operations-header">
        <div>
          <Heading level={1}>Worker status</Heading>
          <p>See when background work last made progress.</p>
        </div>
        <Button
          label={loading ? "Refreshing…" : "Refresh status"}
          isDisabled={loading}
          onClick={() => setRefresh((value) => value + 1)}
        />
      </header>
      {error && (
        <Banner status="error" role="alert" title={error}
          description={snapshot ? "The information below is from the last successful refresh." : undefined} />
      )}
      {!snapshot && !error && <p role="status">Loading worker status…</p>}
      {snapshot && (
        <>
          <Card className="worker-summary" padding={5}>
            <div className="worker-state">
              <Heading level={2}>
                {snapshot.state === "Delayed"
                  ? "Taking longer than expected"
                  : snapshot.state}
              </Heading>
              {snapshot.currentStage && (
                <Badge variant={snapshot.state === "Delayed" ? "warning" : "info"}
                  label={stageNames[snapshot.currentStage] ?? snapshot.currentStage} />
              )}
            </div>
            <p>{explanations[snapshot.state]}</p>
            <dl>
              <div>
                <dt>Last progress</dt>
                <dd>{date(snapshot.lastProgressAt)}</dd>
              </div>
              <div>
                <dt>Process started</dt>
                <dd>{date(snapshot.startedAt)}</dd>
              </div>
              <div>
                <dt>Delay indicator</dt>
                <dd>
                  After {snapshot.staleAfterSeconds} seconds without stage
                  progress
                </dd>
              </div>
            </dl>
          </Card>
          <Heading level={2}>Worker stages</Heading>
          <div className="worker-stages">
            {snapshot.stages.map((stage) => (
              <article
                key={stage.key}
                aria-label={stageNames[stage.key] ?? stage.key}
                className="worker-stage"
              >
                <div className="worker-state">
                  <Heading level={3}>{stageNames[stage.key] ?? stage.key}</Heading>
                  <Badge variant={stage.state === "Failed" ? "error" : stage.state === "Succeeded" ? "success" : stage.state === "Running" ? "info" : "neutral"}
                    label={stage.state === "NotRun" ? "Not run yet" : stage.state} />
                </div>
                <dl>
                  <div>
                    <dt>Last attempt</dt>
                    <dd>{date(stage.lastAttemptAt)}</dd>
                  </div>
                  <div>
                    <dt>Last success</dt>
                    <dd>{date(stage.lastSuccessAt)}</dd>
                  </div>
                  <div>
                    <dt>Last failure</dt>
                    <dd>{date(stage.lastFailureAt)}</dd>
                  </div>
                  <div>
                    <dt>Last duration</dt>
                    <dd>
                      {stage.lastDurationMs === null
                        ? "Not recorded"
                        : `${(stage.lastDurationMs / 1000).toFixed(2)} seconds`}
                    </dd>
                  </div>
                </dl>
                {stage.error && <Banner status="error" title={stage.error} />}
              </article>
            ))}
          </div>
          <p className="worker-footnote">
            This view describes the API process serving this request and resets
            when it restarts. A finished stage can include failed imports or
            cleanup attempts. Check each import's history for its result.
            Directory discovery may skip inaccessible folders.
          </p>
        </>
      )}
      <Button
        label={showTemporaryFiles ? "Hide temporary file status" : "Show temporary file status"}
        aria-expanded={showTemporaryFiles}
        onClick={() => setShowTemporaryFiles((value) => !value)}
      />
      {showTemporaryFiles && <StagingHealth />}
    </section>
  );
}
