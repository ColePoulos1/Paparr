import { expect, test } from "@playwright/test";

test("sign-in and sign-out use session CSRF tokens without storing the password", async ({ page }) => {
  let authenticated = false;
  let currentToken = "csrf-initial";
  const session = () => ({
    enabled: true,
    authenticated,
    csrfToken: currentToken,
  });

  await page.route("**/api/auth/session", (route) =>
    route.fulfill({ json: session() }),
  );
  await page.route("**/api/auth/login", async (route) => {
    expect(route.request().headers()["x-csrf-token"]).toBe("csrf-initial");
    expect(route.request().postDataJSON()).toEqual({ password: "correct horse" });
    authenticated = true;
    currentToken = "csrf-authenticated";
    await route.fulfill({ json: session() });
  });
  await page.route("**/api/auth/logout", async (route) => {
    expect(route.request().headers()["x-csrf-token"]).toBe("csrf-authenticated");
    authenticated = false;
    currentToken = "csrf-after-logout";
    await route.fulfill({ json: session() });
  });
  await page.route("**/api/imports**", async (route) => {
    const body = new URL(route.request().url()).pathname.endsWith("/summary")
      ? {
          AwaitingApproval: 0,
          Pending: 0,
          Processing: 0,
          Publishing: 0,
          Completed: 0,
          Failed: 0,
        }
      : { items: [], total: 0, page: 1, pageSize: 25 };
    await route.fulfill({ json: body });
  });

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Sign in to Paparr" })).toBeVisible();
  await page.getByLabel("Password").fill("correct horse");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Your review desk is clear" })).toBeVisible();
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in to Paparr" })).toBeVisible();
  expect(await page.evaluate(() => localStorage.length)).toBe(0);
});
