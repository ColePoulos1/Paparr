import { test, expect } from "@playwright/test";
import { emptyDuplicates, job, preview, summary } from "../src/test/fixtures";

test("review a book, approve it, and find it in history", async ({ page }) => {
  let imported = false;
  const profiledJob = { ...job, profile: {
    name: "Research", sourceFolder: "research", collection: "Research", filenameOrder: "AuthorTitle",
  } };
  const profiledPreview = { ...preview, relativePath: "Research/" + preview.relativePath };
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({
      json: { enabled: false, authenticated: false, csrfToken: null },
    }),
  );
  await page.route("**/api/imports**", async (route) => {
    const url = new URL(route.request().url());
    const completed = {
      ...profiledJob,
      status: "Completed",
      acceptedBook: {
        id: 1,
        title: job.candidates[0].title,
        author: "H. G. Wells",
        source: "embedded",
        externalId: "",
        importedAt: job.updatedAt,
      },
    };
    let body: unknown;
    if (url.pathname.endsWith("/duplicates"))
      body = {
        items: [
          {
            id: 3,
            fileName: "Another copy.epub",
            relativePath: "downloads/Another copy.epub",
            discoveredAt: job.createdAt,
          },
        ],
        total: 1,
        page: 1,
        pageSize: 25,
      };
    else if (url.pathname.endsWith("/summary"))
      body = {
        ...summary,
        AwaitingApproval: imported ? 0 : 1,
        Completed: imported ? 25 : 24,
      };
    else if (url.pathname.includes("/preview/")) body = profiledPreview;
    else if (url.pathname.includes("/accept/")) {
      imported = true;
      body = completed;
    } else {
      const show =
        url.searchParams.get("status") === "Completed" ? imported : !imported;
      body = {
        items: show ? [imported ? completed : profiledJob] : [],
        total: show ? 1 : 0,
        page: 1,
        pageSize: 25,
      };
    }
    await route.fulfill({ json: body });
  });
  await page.goto("/");
  await expect(page.getByText(profiledPreview.relativePath)).toBeVisible();
  const profile = page.getByRole("region", { name: "Folder profile" });
  await expect(profile).toContainText("Research");
  await expect(profile).toContainText("Author — Title");
  await expect(page.getByText("Read from the ebook metadata.")).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Duplicate files", exact: true }),
  ).toContainText("downloads/Another copy.epub");
  await page.getByRole("button", { name: "Approve & import" }).click();
  await expect(
    page.getByRole("heading", { name: "Your review desk is clear" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Imported 25" }).click();
  await expect(page.getByText("IN YOUR LIBRARY")).toBeVisible();
  await expect(page).toHaveURL(/status=Completed/);
});

test("keyboard users can reach search and review controls", async ({
  page,
}) => {
  await page.route("**/api/auth/session", (route) =>
    route.fulfill({
      json: { enabled: false, authenticated: false, csrfToken: null },
    }),
  );
  await page.route("**/api/imports**", async (route) => {
    const url = route.request().url();
    await route.fulfill({
      json: url.includes("/duplicates?")
        ? emptyDuplicates
        : url.includes("/preview/")
          ? preview
          : url.endsWith("/summary")
            ? summary
            : { items: [job], total: 1, page: 1, pageSize: 25 },
    });
  });
  await page.goto("/");
  await expect(
    page.getByRole("button", { name: "Approve & import" }),
  ).toBeEnabled();
  await page.getByRole("textbox", { name: "Search imports" }).focus();
  await page.keyboard.type("Wells");
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/q=Wells/);
  await page.getByRole("button", { name: "Edit details" }).click();
  await expect(
    page.getByRole("textbox", { name: "Title", exact: true }),
  ).toHaveValue("The Time Machine");
});
