import { createRequire } from "node:module";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";

const require = createRequire(new URL("../src/Paparr.UI/package.json", import.meta.url));
const { chromium, devices, expect } = require("@playwright/test");
let input = "";
for await (const chunk of process.stdin) input += chunk;
const { url, password, outputDirectory } = JSON.parse(input);
await mkdir(outputDirectory, { recursive: true });
const browser = await chromium.launch();
const errors = [];
let page;
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  page = await context.newPage();
  page.on("pageerror", error => errors.push(error.message));
  await page.goto(url);
  await expect(page.getByRole("heading", { name: "Sign in to Paparr" })).toBeVisible();
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByRole("navigation", { name: "Import status" }).getByRole("button", { name: /^Imported/ }).click();
  await page.getByRole("button", { name: /The Quiet Atlas/ }).click();
  await expect(page.getByRole("region", { name: "Duplicate files", exact: true }))
    .toContainText("copies/The Quiet Atlas copy.epub");
  await page.getByRole("navigation", { name: "Import status" }).getByRole("button", { name: /^Needs review/ }).click();
  await page.getByRole("button", { name: /Untitled.epub/ }).click();
  await expect(page.getByText(/No complete metadata was found/)).toBeVisible();
  await page.getByRole("button", { name: "Edit details", exact: true }).click();
  await page.getByLabel("Title", { exact: true }).fill("A Reader's Notebook");
  await page.getByLabel("Author", { exact: true }).fill("Demo Author");
  await page.getByRole("button", { name: "Save metadata choice", exact: true }).click();
  await expect(page.getByText("DESTINATION PREVIEW", { exact: true })).toBeVisible();
  await page.screenshot({ path: join(outputDirectory, "review-desktop.png"), fullPage: true });
  const approvalResponse = page.waitForResponse(response =>
    response.request().method() === "POST" && /\/accept\/\d+$/.test(new URL(response.url()).pathname));
  await page.getByRole("button", { name: "Approve & import", exact: true }).click();
  const approval = await approvalResponse;
  expect(approval.ok()).toBe(true);
  const accepted = await approval.json();
  expect(accepted.status).toBe("Completed");
  expect(accepted.acceptedBook.title).toBe("A Reader's Notebook");
  await page.getByRole("navigation", { name: "Import status" }).getByRole("button", { name: /^Imported/ }).click();
  const notebook = page.getByRole("button", { name: /A Reader's Notebook/ });
  await expect(notebook).toContainText("Imported");
  await notebook.click();
  await expect(page.getByRole("region", { name: "Import details" }))
    .toContainText("A Reader's Notebook");
  await expect(page.getByText("IN YOUR LIBRARY", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Library health", exact: true }).click();
  await page.getByRole("button", { name: "Start integrity scan", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Scan complete" })).toBeVisible({ timeout: 45000 });
  await expect(page.getByRole("region", { name: "Latest integrity scan", exact: true }))
    .toContainText("This scan covered 2 books.", { timeout: 45000 });
  await expect(page.getByRole("cell", { name: "Verified", exact: true })).toHaveCount(2);
  await page.screenshot({ path: join(outputDirectory, "library-desktop.png"), fullPage: true });
  const mobileContext = await browser.newContext({
    ...devices["iPhone 13"], storageState: await context.storageState(),
  });
  const mobile = await mobileContext.newPage();
  mobile.on("pageerror", error => errors.push(error.message));
  await mobile.goto(url + "/?view=library");
  await expect(mobile.getByRole("cell", { name: "Verified", exact: true })).toHaveCount(2);
  await mobile.screenshot({ path: join(outputDirectory, "library-mobile.png"), fullPage: true });
  for (const surface of [page, mobile]) {
    expect(await surface.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  }
  await mobile.getByRole("navigation", { name: "Import status" }).getByRole("button", { name: /^Imported/ }).click();
  await mobile.getByRole("button", { name: /The Quiet Atlas/ }).click();
  await expect(mobile.getByRole("region", { name: "Duplicate files", exact: true }))
    .toContainText("copies/The Quiet Atlas copy.epub");
  expect(await mobile.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await mobile.screenshot({ path: join(outputDirectory, "duplicates-mobile.png"), fullPage: true });
  await mobile.getByRole("navigation", { name: "Import status" }).getByRole("button", { name: /^Needs attention/ }).click();
  await mobile.getByRole("button", { name: /Incomplete download.pdf/ }).click();
  await expect(mobile.getByRole("region", { name: "Import details" }))
    .toContainText("The ebook could not be read.");
  await expect(mobile.getByRole("button", { name: "Retry import", exact: true })).toBeEnabled();
  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Sign in to Paparr" })).toBeVisible();
  expect((await mobileContext.request.get(url + "/api/imports")).status()).toBe(401);
  expect(errors).toEqual([]);
  console.log("Packaged desktop/mobile browser checks passed: sign-in, duplicates, manual review, approval, integrity scan, failed import, and revoked session.");
} catch (error) {
  await page?.screenshot({ path: join(outputDirectory, "failure.png"), fullPage: true }).catch(() => {});
  const message = String(error).replaceAll(password, "[redacted]");
  await writeFile(join(outputDirectory, "failure.txt"), message);
  console.error(message);
  process.exitCode = 1;
} finally {
  await browser.close();
}
