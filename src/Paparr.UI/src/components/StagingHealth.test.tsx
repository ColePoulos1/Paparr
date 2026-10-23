import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import StagingHealth from "./StagingHealth";

const snapshot = {
  activeAttempts: 2,
  pendingMaintenance: 3,
  retained: 1,
  settled: 12,
};
const response = () => new Response(JSON.stringify(snapshot));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

it("explains retained files and distinguishes settled attempts from deleted files", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => response()),
  );
  render(<StagingHealth />);
  expect(
    (await screen.findByText("Settled attempts")).nextElementSibling,
  ).toHaveTextContent("12");
  expect(
    screen.getByText("Active attempts").nextElementSibling,
  ).toHaveTextContent("2");
  expect(
    screen.getByText("Awaiting maintenance").nextElementSibling,
  ).toHaveTextContent("3");
  expect(
    screen.getByText(/Check the server logs before removing anything/),
  ).toBeVisible();
  expect(
    screen.getByText(/older untracked files remain untouched/),
  ).toBeVisible();
});

it("retries an unavailable database without discarding the worker view", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ detail: "Status unavailable" }), {
          status: 503,
        }),
      )
      .mockImplementation(async () => response()),
  );
  render(<StagingHealth />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Status unavailable",
  );
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Retry temporary file status" }));
  expect(await screen.findByText("Settled attempts")).toBeVisible();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("keeps labeled stale counts and cancels polling when closed", async () => {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response())
    .mockImplementation(
      async () =>
        new Response(JSON.stringify({ detail: "Offline" }), { status: 503 }),
    );
  vi.stubGlobal("fetch", fetchMock);
  vi.useFakeTimers();
  const view = render(<StagingHealth />);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(10000);
  });
  expect(screen.getByRole("alert")).toHaveTextContent(
    "last successful refresh",
  );
  expect(
    screen.getByText("Settled attempts").nextElementSibling,
  ).toHaveTextContent("12");
  const signal = fetchMock.mock.calls[0][1].signal as AbortSignal;
  view.unmount();
  expect(signal.aborted).toBe(true);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(30000);
  });
  expect(fetchMock).toHaveBeenCalledTimes(2);
});
