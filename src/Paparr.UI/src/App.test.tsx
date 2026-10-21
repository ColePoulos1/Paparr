import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import { emptyDuplicates, job, preview, summary } from "./test/fixtures";

beforeEach(() => window.history.replaceState(null, "", "/"));
afterEach(() => vi.unstubAllGlobals());

function mockApi(items = [job]) {
  const fetchMock = vi.fn(async (url: string) => {
    if (url.includes("/duplicates?")) return new Response(JSON.stringify(emptyDuplicates));
    const body = url.includes("/preview/")
      ? preview
      : url.endsWith("/summary")
        ? summary
        : { items, total: items.length, page: 1, pageSize: 25 };
    return new Response(JSON.stringify(body), { status: 200 });
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("Import workspace", () => {
  it("shows real metadata evidence and a destination before approval", async () => {
    mockApi();
    render(<App />);
    expect(
      await screen.findByText("Read from the ebook metadata."),
    ).toBeVisible();
    expect(await screen.findByText(preview.relativePath)).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Approve & import" }),
    ).toBeEnabled();
    expect(screen.getByText("Metadata is ready for review.")).toBeVisible();
  });

  it("shows an actionable empty state", async () => {
    mockApi([]);
    render(<App />);
    expect(
      await screen.findByRole("heading", { name: "Your review desk is clear" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Approve & import" }),
    ).not.toBeInTheDocument();
  });

  it("reports request failures and lets the user retry", async () => {
    const mock = mockApi();
    mock.mockImplementation(
      async () =>
        new Response(JSON.stringify({ detail: "Database unavailable" }), {
          status: 503,
        }),
    );
    render(<App />);
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Database unavailable",
    );
    expect(screen.getByRole("button", { name: "Try again" })).toBeEnabled();
  });

  it("puts search in the URL and sends it to the server", async () => {
    const mock = mockApi();
    const user = userEvent.setup();
    render(<App />);
    await screen.findByText(preview.relativePath);
    await user.type(
      screen.getByRole("textbox", { name: "Search imports" }),
      "Wells",
    );
    await user.click(screen.getByRole("button", { name: "Search" }));
    await waitFor(() => expect(window.location.search).toContain("q=Wells"));
    await waitFor(() =>
      expect(
        mock.mock.calls.some(([url]) => url.includes("search=Wells")),
      ).toBe(true),
    );
  });

  it("preserves an unsaved metadata draft when refresh removes the job", async () => {
    let items = [job];
    const fetchMock = vi.fn(async (url: string) => {
      if (url.includes("/duplicates?")) return new Response(JSON.stringify(emptyDuplicates));
      const body = url.includes("/preview/")
        ? preview
        : url.endsWith("/summary")
          ? summary
          : { items, total: items.length, page: 1, pageSize: 25 };
      return new Response(JSON.stringify(body), { status: 200 });
    });
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    render(<App />);
    await screen.findByText(preview.relativePath);
    await user.click(screen.getByRole("button", { name: "Edit details" }));
    const title = screen.getByRole("textbox", { name: "Title" });
    await user.clear(title);
    await user.type(title, "My unsaved title");

    items = [];
    await user.click(screen.getByRole("button", { name: "Refresh" }));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Your unsaved changes are preserved here",
    );
    expect(screen.getByRole("textbox", { name: "Title" })).toHaveValue(
      "My unsaved title",
    );
    expect(
      screen.getByRole("button", { name: "Save metadata choice" }),
    ).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: "Approve & import" }),
    ).not.toBeInTheDocument();
  });

  it("ignores a list response that resolves after its filter request was aborted", async () => {
    let finishStale: (response: Response) => void = () => undefined;
    let listCalls = 0;
    const fetchMock = vi.fn((url: string) => {
      if (url.includes("/duplicates?"))
        return Promise.resolve(new Response(JSON.stringify(emptyDuplicates)));
      if (url.endsWith("/summary"))
        return Promise.resolve(new Response(JSON.stringify(summary)));
      if (url.includes("/preview/"))
        return Promise.resolve(new Response(JSON.stringify(preview)));
      listCalls++;
      if (listCalls === 2)
        return new Promise<Response>((resolve) => {
          finishStale = resolve;
        });
      const items = url.includes("status=Pending") ? [] : [job];
      return Promise.resolve(
        new Response(
          JSON.stringify({ items, total: items.length, page: 1, pageSize: 25 }),
        ),
      );
    });
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    render(<App />);
    await screen.findByText(preview.relativePath);
    await user.click(screen.getByRole("button", { name: "Refresh" }));
    await user.click(screen.getByRole("button", { name: /^Queued/ }));
    await screen.findByRole("heading", { name: "Queued" });
    finishStale(
      new Response(
        JSON.stringify({ items: [job], total: 1, page: 1, pageSize: 25 }),
      ),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: "Nothing here yet" }),
      ).toBeVisible(),
    );
    expect(screen.queryByText(preview.relativePath)).not.toBeInTheDocument();
  });
});
