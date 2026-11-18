import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import { emptyDuplicates, importRow, job, preview, summary } from "./test/fixtures";

beforeEach(() => window.history.replaceState(null, "", "/"));
afterEach(() => vi.unstubAllGlobals());

function mockApi(items = [job]) {
  const fetchMock = vi.fn(async (url: string) => {
    if (url.includes("/duplicates?")) return new Response(JSON.stringify(emptyDuplicates));
    const detail = items.find((item) => url === `/api/imports/${item.id}`);
    if (detail) return new Response(JSON.stringify(detail));
    const body = url.includes("/preview/")
      ? preview
      : url.endsWith("/summary")
        ? summary
        : { items: items.map(importRow), total: items.length, page: 1, pageSize: 25 };
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
    let items = [importRow(job)];
    const fetchMock = vi.fn(async (url: string) => {
      if (url.includes("/duplicates?")) return new Response(JSON.stringify(emptyDuplicates));
      if (url === `/api/imports/${job.id}`) return new Response(JSON.stringify(job));
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

  it("requests details only for the selected row and ignores a previous selection's response", async () => {
    const second = { ...job, id: 2, fileName: "second.epub", candidates: [
      { ...job.candidates[0], title: "Second book" },
    ] };
    let finishFirst!: (response: Response) => void;
    const firstDetail = new Promise<Response>((resolve) => { finishFirst = resolve; });
    const fetchMock = vi.fn(async (url: string) => {
      if (url === "/api/imports/1") return firstDetail;
      const body = url === "/api/imports/2" ? second
        : url.includes("/duplicates?") ? emptyDuplicates
        : url.includes("/preview/") ? preview
        : url.endsWith("/summary") ? summary
        : { items: [importRow(job), importRow(second)], total: 2, page: 1, pageSize: 25 };
      return new Response(JSON.stringify(body));
    });
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    render(<App />);
    const secondRow = await screen.findByRole("button", { name: /Second book/ });
    expect(fetchMock).toHaveBeenCalledWith("/api/imports/1", expect.anything());
    expect(fetchMock).not.toHaveBeenCalledWith("/api/imports/2", expect.anything());
    await user.click(secondRow);
    expect(await screen.findByRole("heading", { name: "Second book" })).toBeVisible();
    await act(async () => finishFirst(new Response(JSON.stringify(job))));
    expect(screen.getByRole("heading", { name: "Second book" })).toBeVisible();
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
      if (url === `/api/imports/${job.id}`)
        return Promise.resolve(new Response(JSON.stringify(job)));
      listCalls++;
      if (listCalls === 2)
        return new Promise<Response>((resolve) => {
          finishStale = resolve;
        });
      const items = url.includes("status=Pending") ? [] : [importRow(job)];
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
        JSON.stringify({ items: [importRow(job)], total: 1, page: 1, pageSize: 25 }),
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
