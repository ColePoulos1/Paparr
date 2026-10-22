import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import LibraryHealth from "./LibraryHealth";
import type { IntegrityStatus, LibraryBook } from "../api";

type TestStatus = IntegrityStatus;
type TestBook = LibraryBook;

const book: TestBook = {
  id: 1,
  title: "The Time Machine",
  author: "H. G. Wells",
  relativePath: "Wells, H. G/The Time Machine.epub",
  importedAt: "2026-01-01T12:00:00Z",
  integrityStatus: "Unchecked" as const,
  checkedAt: null,
  message: null,
  edition: null,
};

function library(
  items: TestBook[] = [book],
  total = items.length,
  counts: Record<TestStatus, number> = {
    Unchecked: 1,
    Verified: 0,
    Missing: 0,
    Changed: 0,
    Unavailable: 0,
  },
) {
  return { items, total, page: 1, pageSize: 25, counts };
}

function audit(status: "Queued" | "Running" | "Completed", processedBooks = 0) {
  return {
    id: 7,
    status,
    totalBooks: 2,
    processedBooks,
    verifiedBooks: processedBooks,
    missingBooks: 0,
    changedBooks: 0,
    unavailableBooks: 0,
    createdAt: "2026-01-01T12:00:00Z",
    completedAt: status === "Completed" ? "2026-01-01T12:01:00Z" : null,
  };
}

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

it("starts one scan, disables duplicate submission, and displays polled completion", async () => {
  const user = userEvent.setup();
  let started = false;
  let finishPost: (value: Response) => void = () => undefined;
  let latestAfterStart = 0;
  const fetchMock = vi.fn(
    (input: string | URL | Request, init?: RequestInit) => {
      const url = String(input);
      if (url === "/api/library/audits" && init?.method === "POST") {
        return new Promise<Response>((resolve) => {
          finishPost = (value) => {
            started = true;
            resolve(value);
          };
        });
      }
      if (url === "/api/library/audits/latest") {
        if (!started) return Promise.resolve(response({ audit: null }));
        latestAfterStart++;
        return Promise.resolve(
          response({
            audit:
              latestAfterStart >= 3 ? audit("Completed", 2) : audit("Queued"),
          }),
        );
      }
      return Promise.resolve(response(library()));
    },
  );
  vi.stubGlobal("fetch", fetchMock);
  render(<LibraryHealth />);

  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Start integrity scan" }),
    ).toBeEnabled(),
  );
  await user.click(
    screen.getByRole("button", { name: "Start integrity scan" }),
  );
  expect(screen.getByRole("button", { name: "Starting scan…" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Starting scan…" }));
  expect(
    fetchMock.mock.calls.filter(
      ([url, init]) =>
        String(url) === "/api/library/audits" && init?.method === "POST",
    ),
  ).toHaveLength(1);

  finishPost(response(audit("Queued")));
  expect(await screen.findByText("Scan queued")).toBeVisible();
  expect(
    await screen.findByText("Scan complete", {}, { timeout: 9000 }),
  ).toBeVisible();
  expect(screen.getByText(/This scan covered 2 books/)).toBeVisible();
}, 10000);

it("filters by integrity status and requests the selected page data", async () => {
  const changed = {
    ...book,
    integrityStatus: "Missing" as const,
    message: "File not found",
  };
  const fetchMock = vi.fn(async (input: string | URL | Request) => {
    const url = String(input);
    if (url.startsWith("/api/library/audits/latest"))
      return response({ audit: null });
    return url.includes("status=Missing")
      ? response(
          library([changed], 1, {
            Unchecked: 0,
            Verified: 0,
            Missing: 1,
            Changed: 0,
            Unavailable: 0,
          }),
        )
      : response(library());
  });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(<LibraryHealth />);

  expect(await screen.findByText("The Time Machine")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Missing, 0 books" }));

  expect(
    screen.getByText("Missing", { selector: ".library-status" }),
  ).toBeVisible();
  expect(screen.getByText("File not found")).toBeVisible();
  expect(fetchMock).toHaveBeenCalledWith(
    "/api/library?page=1&pageSize=25&status=Missing",
    expect.anything(),
  );
});

it("shows a recoverable library load error and retries the request", async () => {
  let libraryRequests = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string | URL | Request) => {
      if (String(input).startsWith("/api/library/audits/latest"))
        return response({ audit: null });
      libraryRequests++;
      return libraryRequests === 1
        ? response({ detail: "Library could not be read" }, 503)
        : response(library());
    }),
  );
  const user = userEvent.setup();
  render(<LibraryHealth />);

  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Library could not be read",
  );
  await user.click(screen.getByRole("button", { name: "Retry" }));
  await waitFor(() =>
    expect(screen.getByText("The Time Machine")).toBeVisible(),
  );
  expect(libraryRequests).toBe(2);
});
