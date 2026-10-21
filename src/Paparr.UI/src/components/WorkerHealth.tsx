import { useEffect, useState } from "react";
import { request } from "../api";
import type { WorkerSnapshot } from "../api";
import "./WorkerHealth.css";

const stageNames: Record<string, string> = {
  queuedWork: "Queued imports",
  sourceCleanup: "Source cleanup",
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
    "No recent stage progress was reported. A large file or slow storage can cause this; check API logs if it continues.",
};

function date(value: string | null) {
  return value ? new Date(value).toLocaleString() : "Not yet";
}

export default function WorkerHealth() {
  const [snapshot, setSnapshot] = useState<WorkerSnapshot | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);

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
      <header className="page-header">
        <div>
          <div className="eyebrow">BEHIND YOUR LIBRARY</div>
          <h1>Worker status</h1>
          <p>See when background work last made progress.</p>
        </div>
        <button
          className="button secondary"
          disabled={loading}
          onClick={() => setRefresh((value) => value + 1)}
        >
          {loading ? "Refreshing…" : "Refresh status"}
        </button>
      </header>
      {error && (
        <div className="notice error" role="alert">
          {error}{" "}
          {snapshot &&
            "The information below is from the last successful refresh."}
        </div>
      )}
      {!snapshot && !error && <p role="status">Loading worker status…</p>}
      {snapshot && (
        <>
          <div className={"worker-summary " + snapshot.state.toLowerCase()}>
            <div className="worker-state">
              <h2>
                {snapshot.state === "Delayed"
                  ? "Taking longer than expected"
                  : snapshot.state}
              </h2>
              {snapshot.currentStage && (
                <span>
                  {stageNames[snapshot.currentStage] ?? snapshot.currentStage}
                </span>
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
          </div>
          <div className="worker-stages">
            {snapshot.stages.map((stage) => (
              <article
                key={stage.key}
                aria-label={stageNames[stage.key] ?? stage.key}
                className={"worker-stage " + stage.state.toLowerCase()}
              >
                <div className="worker-state">
                  <h3>{stageNames[stage.key] ?? stage.key}</h3>
                  <span>
                    {stage.state === "NotRun" ? "Not run yet" : stage.state}
                  </span>
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
                {stage.error && <p className="worker-error">{stage.error}</p>}
              </article>
            ))}
          </div>
          <p className="worker-footnote">
            This view describes the API process serving this request and resets
            when it restarts. A finished stage can include failed imports or cleanup
            attempts; check each import's history for its result. Directory discovery may skip
            inaccessible folders.
          </p>
        </>
      )}
    </section>
  );
}
