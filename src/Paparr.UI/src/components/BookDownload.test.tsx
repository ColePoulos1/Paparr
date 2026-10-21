import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import BookDownload from "./BookDownload";

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

it("downloads verified bytes using the server's Unicode attachment filename", async () => {
  const create = vi.fn(() => "blob:verified-book");
  vi.stubGlobal(
    "URL",
    class extends URL {
      static createObjectURL = create;
      static revokeObjectURL = vi.fn();
    },
  );
  let filename = "";
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(
    function (this: HTMLAnchorElement) {
      filename = this.download;
    },
  );
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async () =>
        new Response("verified book", {
          headers: {
            "Content-Disposition":
              "attachment; filename*=UTF-8''L%27%C3%A9t%C3%A9%20-%20Auteur.epub",
            "Content-Type": "application/epub+zip",
          },
        }),
    ),
  );
  render(<BookDownload id={3} title="L'été" />);
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Download L'été" }));
  expect(filename).toBe("L'été - Auteur.epub");
  expect(create).toHaveBeenCalledOnce();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("shows a changed-file error in the library without navigating or downloading it", async () => {
  const click = vi
    .spyOn(HTMLAnchorElement.prototype, "click")
    .mockImplementation(() => {});
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async () =>
        new Response(
          JSON.stringify({
            detail:
              "The library file changed. Run an integrity scan before restoring a verified backup.",
          }),
          { status: 409 },
        ),
    ),
  );
  render(<BookDownload id={3} title="Atlas" />);
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Download Atlas" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "The library file changed.",
  );
  expect(screen.getByRole("button", { name: "Download Atlas" })).toBeEnabled();
  expect(click).not.toHaveBeenCalled();
});

it("prevents repeated requests while verifying and aborts when leaving the view", async () => {
  let finish: (response: Response) => void = () => undefined;
  const fetchMock = vi.fn<
    (url: string, options: RequestInit) => Promise<Response>
  >(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  vi.stubGlobal("fetch", fetchMock);
  const click = vi
    .spyOn(HTMLAnchorElement.prototype, "click")
    .mockImplementation(() => {});
  const view = render(<BookDownload id={3} title="Atlas" />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Download Atlas" }));
  expect(screen.getByRole("button", { name: "Download Atlas" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Download Atlas" }));
  expect(fetchMock).toHaveBeenCalledTimes(1);
  const signal = fetchMock.mock.calls[0][1].signal as AbortSignal;
  view.unmount();
  expect(signal.aborted).toBe(true);
  await act(async () =>
    finish(
      new Response("book", {
        headers: { "Content-Disposition": 'attachment; filename="Atlas.epub"' },
      }),
    ),
  );
  expect(click).not.toHaveBeenCalled();
});
