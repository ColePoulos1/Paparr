import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { ImportJob } from "../api";
import { job } from "../test/fixtures";
import UndoImport from "./UndoImport";

const completed: ImportJob = { ...job, status: "Completed" };
const preview = {
  revision: "preview-revision",
  canUndo: true,
  reason: null,
  sourceRelativePath: "incoming/original.epub",
  libraryRelativePath: "Writer/Title/book.epub",
  operationId: null,
  operationStatus: null,
  error: null,
  canRetry: false,
};
const response = (value: unknown) => new Response(JSON.stringify(value));
afterEach(() => vi.unstubAllGlobals());

it("checks both copies on demand and requires a separate confirmation", async () => {
  const fetchMock = vi.fn(async (_url: string, options: RequestInit) =>
    response(options.method === "POST" ? job : preview),
  );
  vi.stubGlobal("fetch", fetchMock);
  const onChange = vi.fn();
  render(<UndoImport job={completed} onChange={onChange} />);
  expect(fetchMock).not.toHaveBeenCalled();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Undo import…" }));
  expect(await screen.findByText("incoming/original.epub")).toBeVisible();
  expect(screen.getByText("Writer/Title/book.epub")).toBeVisible();
  expect(fetchMock).toHaveBeenCalledTimes(1);
  await user.click(
    screen.getByRole("button", { name: "Undo import" }),
  );
  await waitFor(() => expect(onChange).toHaveBeenCalledWith(job));
  const sent = JSON.parse(
    String(
      fetchMock.mock.calls.find((call) => call[1].method === "POST")?.[1].body,
    ),
  );
  expect(sent.revision).toBe("preview-revision");
  expect(sent.operationId).toMatch(/^[a-f0-9-]{36}$/);
});

it("explains ineligible originals without offering an enabled undo", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      response({
        ...preview,
        canUndo: false,
        reason: "The original file has changed.",
      }),
    ),
  );
  render(<UndoImport job={completed} onChange={vi.fn()} />);
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Undo import…" }));
  expect(
    await screen.findByText("The original file has changed."),
  ).toBeVisible();
  expect(
    screen.getByRole("button", { name: "Undo import" }),
  ).toBeDisabled();
});

it("keeps the same operation id when a network failure leaves the result uncertain", async () => {
  const fetchMock = vi.fn(async (_url: string, options: RequestInit) => {
    if (options.method === "POST")
      throw new Error("Connection lost. Refresh undo status.");
    return response(preview);
  });
  vi.stubGlobal("fetch", fetchMock);
  render(<UndoImport job={completed} onChange={vi.fn()} />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Undo import…" }));
  await screen.findByText("incoming/original.epub");
  await user.click(
    screen.getByRole("button", { name: "Undo import" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent("Connection lost");
  await user.click(
    screen.getByRole("button", { name: "Undo import" }),
  );
  const posts = fetchMock.mock.calls.filter(
    (call) => call[1].method === "POST",
  );
  expect(posts).toHaveLength(2);
  expect(posts[0][1].body).toBe(posts[1][1].body);
});

it("recovers persisted undo state after reload and retries its existing operation", async () => {
  const operationId = "99a3c79f-a276-4574-a959-137647a546b0";
  const fetchMock = vi.fn(async (_url: string, options: RequestInit) =>
    response(
      options.method === "POST"
        ? job
        : {
            ...preview,
            canUndo: false,
            operationId,
            operationStatus: "Retained",
            canRetry: true,
            error:
              "The library copy is locked. Close other programs before retrying.",
          },
    ),
  );
  vi.stubGlobal("fetch", fetchMock);
  const onChange = vi.fn();
  render(
    <UndoImport
      job={{ ...completed, status: "Undoing" }}
      onChange={onChange}
    />,
  );
  expect(await screen.findByRole("alert")).toHaveTextContent("locked");
  expect(screen.getByText(/This request is saved/)).toBeVisible();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Retry undo" }));
  await waitFor(() => expect(onChange).toHaveBeenCalledWith(job));
  expect(fetchMock).toHaveBeenCalledWith(
    "/api/imports/1/undo/retry",
    expect.objectContaining({ body: JSON.stringify({ operationId }) }),
  );
});

it("cancels outdated previews and never enables a decision from the previous revision", async () => {
  let finish!: (value: Response) => void;
  const fetchMock = vi
    .fn()
    .mockImplementationOnce(
      async () =>
        new Promise<Response>((resolve) => {
          finish = resolve;
        }),
    )
    .mockImplementation(async () =>
      response({
        ...preview,
        canUndo: false,
        reason: "The original is missing.",
      }),
    );
  vi.stubGlobal("fetch", fetchMock);
  const view = render(<UndoImport job={completed} onChange={vi.fn()} />);
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Undo import…" }));
  const signal = fetchMock.mock.calls[0][1].signal as AbortSignal;
  view.rerender(
    <UndoImport
      job={{ ...completed, revision: "new-revision" }}
      onChange={vi.fn()}
    />,
  );
  expect(signal.aborted).toBe(true);
  expect(await screen.findByText("The original is missing.")).toBeVisible();
  await act(async () => {
    finish(response(preview));
  });
  expect(
    screen.getByRole("button", { name: "Undo import" }),
  ).toBeDisabled();
});

it("blocks repeated confirmation and cancels the request when leaving", async () => {
  const fetchMock = vi.fn(async (_url: string, options: RequestInit) =>
    options.method === "POST"
      ? new Promise<Response>(() => {})
      : response(preview),
  );
  vi.stubGlobal("fetch", fetchMock);
  const view = render(<UndoImport job={completed} onChange={vi.fn()} />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Undo import…" }));
  await screen.findByText("incoming/original.epub");
  await user.click(
    screen.getByRole("button", { name: "Undo import" }),
  );
  expect(
    screen.getByRole("button", { name: "Returning to review…" }),
  ).toBeDisabled();
  const posts = fetchMock.mock.calls.filter(
    (call) => call[1].method === "POST",
  );
  expect(posts).toHaveLength(1);
  view.unmount();
  expect((posts[0][1].signal as AbortSignal).aborted).toBe(true);
});
