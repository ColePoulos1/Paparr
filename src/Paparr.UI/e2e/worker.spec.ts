import { expect, test } from "@playwright/test";

test("worker status is deep linkable and identifies a delayed discovery stage", async ({
  page,
}) => {
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({
      json: { enabled: false, authenticated: false, csrfToken: null },
    }),
  );
  await page.route("**/api/imports/summary", (route) =>
    route.fulfill({
      json: {
        AwaitingApproval: 0,
        Pending: 2,
        Processing: 0,
        Publishing: 0,
        Completed: 0,
        Failed: 0,
      },
    }),
  );
  await page.route("**/api/diagnostics/worker", (route) =>
    route.fulfill({
      json: {
        enabled: true,
        state: "Delayed",
        startedAt: "2026-10-21T12:00:00Z",
        lastProgressAt: "2026-10-21T12:01:00Z",
        staleAfterSeconds: 60,
        currentStage: "discovery",
        stages: [
          {
            key: "discovery",
            state: "Running",
            lastAttemptAt: "2026-10-21T12:01:00Z",
            lastSuccessAt: "2026-10-21T12:00:30Z",
            lastFailureAt: null,
            lastDurationMs: 350,
            error: null,
          },
        ],
      },
    }),
  );
  await page.route("**/api/diagnostics/staging", (route) =>
    route.fulfill({
      json: {
        activeAttempts: 1,
        pendingMaintenance: 2,
        retained: 1,
        settled: 12,
      },
    }),
  );
  await page.goto("/?view=worker");
  await expect(
    page.getByRole("heading", { name: "Taking longer than expected" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Worker status", exact: true }),
  ).toHaveAttribute("aria-current", "page");
  await expect(
    page.getByRole("button", { name: "Queued 2", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("article", { name: "File discovery" }),
  ).toContainText("Running");
  await page
    .getByRole("button", { name: "Refresh status", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Refresh status", exact: true }),
  ).toBeEnabled();
  await page
    .getByRole("button", { name: "Show temporary file status" })
    .click();
  const maintenance = page.getByRole("region", {
    name: "Temporary file maintenance",
  });
  await expect(maintenance.getByText("Settled attempts", { exact: true })).toBeVisible();
  await expect(
    maintenance.getByText(/Check the server logs before removing anything/),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth > innerWidth,
    ),
  ).toBe(false);
  await page
    .getByRole("button", { name: "Hide temporary file status" })
    .click();
  await expect(maintenance).toHaveCount(0);
});
