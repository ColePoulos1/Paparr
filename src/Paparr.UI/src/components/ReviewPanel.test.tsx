import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import ReviewPanel from "./ReviewPanel";
import { emptyDuplicates, job, preview } from "../test/fixtures";

beforeEach(() =>
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => new Response(JSON.stringify(emptyDuplicates))),
  ),
);

afterEach(() => vi.unstubAllGlobals());

it("renders awaiting approval when the provider found no complete metadata", () => {
  const withoutCandidates = { ...job, candidates: [] };

  render(<ReviewPanel job={withoutCandidates} onChange={vi.fn()} />);

  expect(
    screen.getByRole("heading", { name: "Give this book a name" }),
  ).toBeVisible();
  expect(screen.getByRole("button", { name: "Edit details" })).toBeVisible();
  expect(screen.getByText(/No complete metadata was found/)).toBeVisible();
});

it("renders retry for a failed import without metadata candidates", () => {
  const failedWithoutCandidates = {
    ...job,
    status: "Failed" as const,
    candidates: [],
  };

  render(<ReviewPanel job={failedWithoutCandidates} onChange={vi.fn()} />);

  expect(screen.getByRole("button", { name: "Retry import" })).toBeVisible();
  expect(
    screen.queryByRole("button", { name: "Approve & import" }),
  ).not.toBeInTheDocument();
});

it("prevents duplicate approval while a request is in flight", async () => {
  let finish: (response: Response) => void = () => undefined;
  const pending = new Promise<Response>((resolve) => {
    finish = resolve;
  });
  const fetchMock = vi.fn((url: string) =>
    url.includes("/duplicates?")
      ? Promise.resolve(new Response(JSON.stringify(emptyDuplicates)))
      : url.includes("/accept/")
        ? pending
        : Promise.resolve(new Response(JSON.stringify(preview))),
  );
  vi.stubGlobal("fetch", fetchMock);
  const onChange = vi.fn();
  const user = userEvent.setup();
  render(<ReviewPanel job={job} onChange={onChange} />);
  await screen.findByText(preview.relativePath);
  await user.click(screen.getByRole("button", { name: "Approve & import" }));
  expect(screen.getByRole("button", { name: "Saving…" })).toBeDisabled();
  expect(
    fetchMock.mock.calls.filter(([url]) => url.includes("/accept/")),
  ).toHaveLength(1);
  finish(new Response(JSON.stringify({ ...job, status: "Completed" })));
  await waitFor(() => expect(onChange).toHaveBeenCalledOnce());
});

it("keeps the review available after a failed approval", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) =>
      url.includes("/duplicates?")
        ? new Response(JSON.stringify(emptyDuplicates))
        : url.includes("/accept/")
          ? new Response(
              JSON.stringify({ detail: "Destination unavailable" }),
              {
                status: 409,
              },
            )
          : new Response(JSON.stringify(preview)),
    ),
  );
  const user = userEvent.setup();
  render(<ReviewPanel job={job} onChange={vi.fn()} />);
  await screen.findByText(preview.relativePath);
  await user.click(screen.getByRole("button", { name: "Approve & import" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Destination unavailable",
  );
  expect(
    screen.getByRole("button", { name: "Approve & import" }),
  ).toBeEnabled();
});

it("saves a manual choice with the entered fields", async () => {
  const fetchMock = vi.fn(
    async (url: string) =>
      new Response(
        JSON.stringify(
          url.includes("/duplicates?")
            ? emptyDuplicates
            : url.includes("/metadata")
              ? job
              : preview,
        ),
      ),
  );
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(<ReviewPanel job={job} onChange={vi.fn()} />);
  await user.click(screen.getByRole("button", { name: "Edit details" }));
  await user.clear(screen.getByRole("textbox", { name: "Title" }));
  await user.type(
    screen.getByRole("textbox", { name: "Title" }),
    "A corrected title",
  );
  await user.click(
    screen.getByRole("button", { name: "Save metadata choice" }),
  );
  await waitFor(() =>
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/imports/1/metadata",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({
          title: "A corrected title",
          author: "H. G. Wells",
        }),
      }),
    ),
  );
});

it("cannot approve without a successful destination preview", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) =>
      url.includes("/duplicates?")
        ? new Response(JSON.stringify(emptyDuplicates))
        : new Response(JSON.stringify({ detail: "Invalid destination" }), {
            status: 409,
          }),
    ),
  );
  render(<ReviewPanel job={job} onChange={vi.fn()} />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Invalid destination",
  );
  expect(
    screen.getByRole("button", { name: "Approve & import" }),
  ).toBeDisabled();
});

it("does not let an older preview authorize the newly selected candidate", async () => {
  let finishFirst: (response: Response) => void = () => undefined;
  let finishSecond: (response: Response) => void = () => undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string) =>
      url.includes("/duplicates?")
        ? Promise.resolve(new Response(JSON.stringify(emptyDuplicates)))
        : url.includes("/preview/11")
          ? new Promise<Response>((resolve) => {
              finishFirst = resolve;
            })
          : url.includes("/preview/12")
            ? new Promise<Response>((resolve) => {
                finishSecond = resolve;
              })
            : Promise.resolve(new Response(JSON.stringify(job))),
    ),
  );
  const user = userEvent.setup();
  render(<ReviewPanel job={job} onChange={vi.fn()} />);
  await user.click(
    screen.getByRole("radio", { name: /The Time Machine: An Invention/ }),
  );
  finishFirst(new Response(JSON.stringify(preview)));
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Approve & import" }),
    ).toBeDisabled(),
  );
  finishSecond(
    new Response(
      JSON.stringify({ ...preview, relativePath: "Different destination" }),
    ),
  );
  expect(await screen.findByText("Different destination")).toBeVisible();
  expect(
    screen.getByRole("button", { name: "Approve & import" }),
  ).toBeEnabled();
});

it("retries source cleanup when the completed import says it can be retried", async () => {
  const completed = {
    ...job,
    status: "Completed" as const,
    acceptedBook: {
      id: 2,
      title: "The Time Machine",
      author: "H. G. Wells",
      source: "embedded",
      externalId: "",
      importedAt: job.updatedAt,
    },
    cleanup: {
      status: "Retained" as const,
      attemptCount: 1,
      error: "Permission denied",
      canRetry: true,
    },
  };
  const fetchMock = vi.fn(async (url: string) =>
    url.includes("/duplicates?")
      ? new Response(JSON.stringify(emptyDuplicates))
      : new Response(
          JSON.stringify({
            ...completed,
            cleanup: {
              status: "Completed",
              attemptCount: 2,
              error: null,
              canRetry: false,
            },
          }),
        ),
  );
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(<ReviewPanel job={completed} onChange={vi.fn()} />);
  expect(screen.getByText(/Source cleanup needs attention/)).toBeVisible();
  await user.click(
    screen.getByRole("button", { name: "Retry source cleanup" }),
  );
  expect(fetchMock).toHaveBeenCalledWith(
    "/api/imports/1/cleanup/retry",
    expect.objectContaining({ method: "POST" }),
  );
});
