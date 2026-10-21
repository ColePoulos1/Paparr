import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { WorkerSnapshot } from "../api";
import WorkerHealth from "./WorkerHealth";

const snapshot: WorkerSnapshot = {
  enabled: true,
  state: "Idle",
  startedAt: "2026-10-21T12:00:00Z",
  lastProgressAt: "2026-10-21T12:01:00Z",
  staleAfterSeconds: 60,
  currentStage: null,
  stages: ["queuedWork", "sourceCleanup", "libraryAudit", "discovery"].map(
    (key) => ({
      key,
      state: "Succeeded",
      lastAttemptAt: "2026-10-21T12:01:00Z",
      lastSuccessAt: "2026-10-21T12:01:00Z",
      lastFailureAt: null,
      lastDurationMs: 1250,
      error: null,
    }),
  ),
};
const response = (value: unknown) => new Response(JSON.stringify(value));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

it("shows the worker stages and distinguishes stage completion from individual import success", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => response(snapshot)),
  );
  render(<WorkerHealth />);
  expect(await screen.findByRole("heading", { name: "Idle" })).toBeVisible();
  const discovery = within(
    screen.getByRole("article", { name: "File discovery" }),
  );
  expect(discovery.getByText("Succeeded")).toBeVisible();
  expect(discovery.getByText("1.25 seconds")).toBeVisible();
  expect(discovery.getByText("Not yet")).toBeVisible();
  expect(
    screen.getByText(/A finished stage can include failed imports/),
  ).toBeVisible();
});

it.each([
  ["Disabled", "Disabled", /queued work will wait/],
  ["Delayed", "Taking longer than expected", /large file or slow storage/],
  ["Starting", "Starting", /first stage yet/],
] as const)("explains the %s state", async (state, heading, explanation) => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      response({ ...snapshot, state, enabled: state !== "Disabled" }),
    ),
  );
  render(<WorkerHealth />);
  expect(
      await screen.findByRole("heading", { name: heading }),
  ).toBeVisible();
  expect(screen.getByText(explanation)).toBeVisible();
});

it("reports failed discovery without hiding the last successful stage time", async () => {
  const error =
    "The ingest directory could not be read. Check access and API logs.";
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      response({
        ...snapshot,
        stages: snapshot.stages.map((stage) =>
          stage.key === "discovery"
            ? {
                ...stage,
                state: "Failed",
                error,
                lastFailureAt: "2026-10-21T12:02:00Z",
              }
            : stage,
        ),
      }),
    ),
  );
  render(<WorkerHealth />);
  expect(await screen.findByText(error)).toBeVisible();
  const discovery = within(
    screen.getByRole("article", { name: "File discovery" }),
  );
  expect(discovery.getByText("Failed")).toBeVisible();
  expect(
    discovery.getByText("Last success").nextElementSibling,
  ).not.toHaveTextContent("Not yet");
});

it("recovers from an unavailable endpoint through refresh", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ detail: "Status unavailable" }), {
          status: 503,
        }),
      )
      .mockImplementation(async () => response(snapshot)),
  );
  render(<WorkerHealth />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Status unavailable",
  );
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Refresh status" }));
  expect(await screen.findByRole("heading", { name: "Idle" })).toBeVisible();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("polls for progress, labels stale data on failure, and cancels on unmount", async () => {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response(snapshot))
    .mockResolvedValue(
      new Response(JSON.stringify({ detail: "Offline" }), { status: 503 }),
    );
  vi.stubGlobal("fetch", fetchMock);
  vi.useFakeTimers();
  const view = render(<WorkerHealth />);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
  await act(async () => {
    await vi.advanceTimersByTimeAsync(10000);
  });
  expect(screen.getByRole("alert")).toHaveTextContent(
    "last successful refresh",
  );
  expect(screen.getByRole("heading", { name: "Idle" })).toBeVisible();
  const signal = fetchMock.mock.calls[0][1].signal as AbortSignal;
  view.unmount();
  expect(signal.aborted).toBe(true);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(30000);
  });
  expect(fetchMock).toHaveBeenCalledTimes(2);
});
