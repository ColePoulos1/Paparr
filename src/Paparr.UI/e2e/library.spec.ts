import { test, expect } from "@playwright/test";

test("library health is deep linkable and scans show actionable results", async ({
  page,
}) => {
  let started = false;
  await page.route("**/api/imports/summary", (route) => route.fulfill({
    json: { AwaitingApproval: 4, Pending: 0, Processing: 0, Publishing: 0, Failed: 1, Completed: 2 },
  }));
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({ json: { enabled: false, authenticated: false, csrfToken: null } }),
  );
  await page.route("**/api/library**", async (route) => {
    const url = new URL(route.request().url());
    const audit = {
      id: 1,
      status: started ? "Completed" : "Queued",
      totalBooks: 1,
      processedBooks: started ? 1 : 0,
      verifiedBooks: 0,
      missingBooks: 0,
      changedBooks: started ? 1 : 0,
      unavailableBooks: 0,
      createdAt: "2026-10-20T12:00:00Z",
      completedAt: started ? "2026-10-20T12:00:01Z" : null,
    };
    if (
      url.pathname.endsWith("/audits") &&
      route.request().method() === "POST"
    ) {
      started = true;
      await route.fulfill({
        status: 202,
        json: { ...audit, status: "Queued" },
      });
    } else if (url.pathname.endsWith("/latest")) {
      await route.fulfill({
        json: { audit: started ? { ...audit, status: "Completed" } : null },
      });
    } else {
      await route.fulfill({
        json: {
          items: [
            {
              id: 1,
              title: "The Quiet Atlas",
              author: "Mara Finch",
              relativePath: "Mara Finch/The Quiet Atlas/The Quiet Atlas.epub",
              importedAt: "2026-10-20T12:00:00Z",
              integrityStatus: started ? "Changed" : "Verified",
              checkedAt: "2026-10-20T12:00:01Z",
              message: started
                ? "The library file differs from the imported copy."
                : null,
            },
          ],
          total: 1,
          page: 1,
          pageSize: 25,
          counts: {
            Unchecked: 0,
            Verified: started ? 0 : 1,
            Missing: 0,
            Changed: started ? 1 : 0,
            Unavailable: 0,
          },
        },
      });
    }
  });
  await page.goto("/?view=library");
  await expect(page.getByRole("button", { name: "Needs review 4", exact: true })).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Library health" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Start integrity scan" }).click();
  await expect(
    page.getByRole("heading", { name: "Scan complete" }),
  ).toBeVisible();
  await expect(
    page.getByText("The library file differs from the imported copy."),
  ).toBeVisible();
  await page.getByRole("button", { name: "Changed, 1 books" }).click();
  await expect(page.getByRole("table")).toContainText("The Quiet Atlas");
  await expect(page).toHaveURL(/view=library/);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    ),
  ).toBe(false);
});
