import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import DuplicateSources from "./DuplicateSources";
import { emptyDuplicates } from "../test/fixtures";

const copy = {
  id: 4,
  fileName: "atlas.epub",
  relativePath: "inbox/atlas.epub",
  discoveredAt: "2026-10-21T12:00:00Z",
};
const page = { items: [copy], total: 1, page: 1, pageSize: 25 };
const response = (body: unknown) => new Response(JSON.stringify(body));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

it("shows historical observations without implying copies still exist", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      response({
        ...page,
        items: [
          copy,
          { ...copy, id: 5, fileName: "old.pdf", relativePath: null },
        ],
        total: 2,
      }),
    ),
  );
  render(<DuplicateSources jobId={1} />);
  expect(await screen.findByText("inbox/atlas.epub")).toBeVisible();
  expect(screen.getByText("Outside the current ingest folder")).toBeVisible();
  expect(screen.getByText(/Their locations may have changed/)).toBeVisible();
  expect(screen.getByText("2 observed")).toBeVisible();
  expect(
    screen.queryByRole("button", { name: /delete|remove/i }),
  ).not.toBeInTheDocument();
});

it("distinguishes no duplicates from an unavailable history and retries errors", async () => {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(
      new Response(JSON.stringify({ detail: "History is unavailable." }), {
        status: 503,
      }),
    )
    .mockResolvedValueOnce(response(emptyDuplicates));
  vi.stubGlobal("fetch", fetchMock);
  render(<DuplicateSources jobId={1} />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "History is unavailable.",
  );
  expect(
    screen.queryByText("No duplicate files observed."),
  ).not.toBeInTheDocument();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Retry duplicate files" }));
  expect(await screen.findByText("No duplicate files observed.")).toBeVisible();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("loads bounded pages and hides the previous page while loading the next", async () => {
  let complete: (value: Response) => void = () => undefined;
  const fetchMock = vi.fn((url: string) =>
    url.includes("page=2&")
      ? new Promise<Response>((resolve) => {
          complete = resolve;
        })
      : Promise.resolve(response({ ...page, total: 26 })),
  );
  vi.stubGlobal("fetch", fetchMock);
  render(<DuplicateSources jobId={1} />);
  await screen.findByText("inbox/atlas.epub");
  const navigation = within(
    screen.getByRole("navigation", { name: "Duplicate pages" }),
  );
  await userEvent
    .setup()
    .click(navigation.getByRole("button", { name: "Next" }));
  expect(screen.queryByText("inbox/atlas.epub")).not.toBeInTheDocument();
  expect(navigation.getByRole("button", { name: "Next" })).toBeDisabled();
  expect(fetchMock).toHaveBeenLastCalledWith(
    "/api/imports/1/duplicates?page=2&pageSize=25",
    expect.anything(),
  );
  await act(async () =>
    complete(
      response({
        ...page,
        page: 2,
        total: 26,
        items: [{ ...copy, id: 6, relativePath: "other/atlas.epub" }],
      }),
    ),
  );
  expect(await screen.findByText("other/atlas.epub")).toBeVisible();
  expect(navigation.getByRole("button", { name: "Next" })).toBeDisabled();
  expect(navigation.getByRole("button", { name: "Previous" })).toBeEnabled();
});

it("aborts old requests and ignores their results when the selected import changes", async () => {
  let finishFirst: (value: Response) => void = () => undefined;
  const fetchMock = vi.fn<
    (url: string, options: RequestInit) => Promise<Response>
  >((url) =>
    url.includes("/1/")
      ? new Promise<Response>((resolve) => {
          finishFirst = resolve;
        })
      : Promise.resolve(response(emptyDuplicates)),
  );
  vi.stubGlobal("fetch", fetchMock);
  const view = render(<DuplicateSources jobId={1} />);
  const firstOptions = fetchMock.mock.calls[0];
  view.rerender(<DuplicateSources jobId={2} />);
  expect(firstOptions[1].signal?.aborted).toBe(true);
  await screen.findByText("No duplicate files observed.");
  await act(async () => finishFirst(response(page)));
  expect(screen.queryByText("inbox/atlas.epub")).not.toBeInTheDocument();
  expect(screen.getByText("No duplicate files observed.")).toBeVisible();
});

it("polls for newly discovered copies and stops after unmount", async () => {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response(emptyDuplicates))
    .mockImplementation(async () => response(page));
  vi.stubGlobal("fetch", fetchMock);
  vi.useFakeTimers();
  const view = render(<DuplicateSources jobId={1} />);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
  expect(screen.getByText("No duplicate files observed.")).toBeVisible();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(15000);
  });
  expect(screen.getByText("inbox/atlas.epub")).toBeVisible();
  view.unmount();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(30000);
  });
  expect(fetchMock).toHaveBeenCalledTimes(2);
});

it("retains the last observations with an explicit warning when refresh fails", async () => {
  const fetchMock = vi
    .fn()
    .mockResolvedValueOnce(response(page))
    .mockResolvedValue(
      new Response(JSON.stringify({ detail: "Offline" }), { status: 503 }),
    );
  vi.stubGlobal("fetch", fetchMock);
  vi.useFakeTimers();
  render(<DuplicateSources jobId={1} />);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
  await act(async () => {
    await vi.advanceTimersByTimeAsync(15000);
  });
  expect(screen.getByRole("alert")).toHaveTextContent(
    "Showing the last loaded observations.",
  );
  expect(screen.getByText("inbox/atlas.epub")).toBeVisible();
});
