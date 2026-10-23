import { expect, test } from "@playwright/test";
import { emptyDuplicates, job, preview, summary } from "../src/test/fixtures";

test("undo previews the two copies and returns the file to review", async ({
  page,
}) => {
  let undone = false;
  let confirmations = 0;
  const completed = {
    ...job,
    status: "Completed",
    acceptedBook: {
      id: 4,
      title: job.candidates[0].title,
      author: job.candidates[0].author,
      source: "embedded",
      externalId: "",
      importedAt: job.updatedAt,
      edition: null,
    },
  };
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({
      json: { enabled: false, authenticated: false, csrfToken: null },
    }),
  );
  await page.route("**/api/imports**", async (route) => {
    const url = new URL(route.request().url());
    let body: unknown;
    if (url.pathname.endsWith("/summary"))
      body = {
        ...summary,
        AwaitingApproval: undone ? 1 : 0,
        Completed: undone ? 0 : 1,
      };
    else if (url.pathname.endsWith("/duplicates")) body = emptyDuplicates;
    else if (url.pathname.endsWith("/undo-preview"))
      body = {
        revision: job.revision,
        canUndo: true,
        reason: null,
        sourceRelativePath: "inbox/original.epub",
        libraryRelativePath: preview.relativePath,
        operationId: null,
        operationStatus: null,
        error: null,
        canRetry: false,
      };
    else if (url.pathname.endsWith("/undo")) {
      confirmations++;
      undone = true;
      body = job;
    } else if (url.pathname.includes("/preview/")) body = preview;
    else {
      const visible =
        url.searchParams.get("status") ===
        (undone ? "AwaitingApproval" : "Completed");
      body = {
        items: visible ? [undone ? job : completed] : [],
        total: visible ? 1 : 0,
        page: 1,
        pageSize: 25,
      };
    }
    await route.fulfill({ json: body });
  });
  await page.goto("/?status=Completed");
  await page.getByRole("button", { name: "Undo import…" }).click();
  const undo = page.getByRole("region", { name: "Undo import", exact: true });
  await expect(undo.getByText("inbox/original.epub")).toBeVisible();
  await expect(undo.getByText(preview.relativePath)).toBeVisible();
  expect(confirmations).toBe(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > innerWidth,
    ),
  ).toBe(false);
  await undo.getByRole("button", { name: "Undo import", exact: true }).click();
  await expect(page.getByText(/The import returned to review/)).toBeVisible();
  expect(confirmations).toBe(1);
  await page.getByRole("button", { name: "Review again" }).click();
  await expect(
    page.getByRole("button", { name: "Approve & import" }),
  ).toBeEnabled();
  await expect(page).toHaveURL(/status=AwaitingApproval/);
});
