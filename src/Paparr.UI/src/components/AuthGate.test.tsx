import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { request, setCsrfToken } from "../api";
import AuthGate from "./AuthGate";

const enabledAnonymous = { enabled: true, authenticated: false, csrfToken: "csrf-session" };
const enabledAuthenticated = { enabled: true, authenticated: true, csrfToken: "csrf-authenticated" };
const disabled = { enabled: false, authenticated: false, csrfToken: null };
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

afterEach(() => {
  setCsrfToken(null);
  vi.unstubAllGlobals();
});

it("renders the app without sign-in when authentication is disabled", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => response(disabled)));

  render(<AuthGate>{({ signOut }) => <div>App ready{signOut && <button>Sign out</button>}</div>}</AuthGate>);

  expect(await screen.findByText("App ready")).toBeVisible();
  expect(screen.queryByRole("button", { name: "Sign out" })).not.toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "Sign in to Paparr" })).not.toBeInTheDocument();
});

it("lets the user retry a failed initial session check", async () => {
  let calls = 0;
  vi.stubGlobal("fetch", vi.fn(async () => {
    calls++;
    return calls === 1 ? response({ detail: "Session service unavailable" }, 503) : response(disabled);
  }));
  const user = userEvent.setup();
  render(<AuthGate>{() => <div>App ready</div>}</AuthGate>);

  expect(await screen.findByRole("alert")).toHaveTextContent("Session service unavailable");
  await user.click(screen.getByRole("button", { name: "Retry" }));
  expect(await screen.findByText("App ready")).toBeVisible();
  expect(calls).toBe(2);
});

it("keeps the sign-in form available after a rejected password", async () => {
  let finishLogin: (value: Response) => void = () => undefined;
  vi.stubGlobal("fetch", vi.fn((input: string | URL | Request) =>
    String(input) === "/api/auth/session"
      ? Promise.resolve(response(enabledAnonymous))
      : new Promise<Response>((resolve) => { finishLogin = resolve; }),
  ));
  const user = userEvent.setup();
  render(<AuthGate>{() => <div>App ready</div>}</AuthGate>);

  await user.type(await screen.findByLabelText("Password"), "wrong password");
  await user.click(screen.getByRole("button", { name: "Sign in" }));
  expect(screen.getByRole("button", { name: "Signing in…" })).toBeDisabled();
  finishLogin(response({ detail: "Password was not accepted" }, 401));

  expect(await screen.findByRole("alert")).toHaveTextContent("Password was not accepted");
  expect(screen.getByRole("button", { name: "Sign in" })).toBeEnabled();
});

it("sends CSRF on login and sends an expired session back to sign-in without retrying", async () => {
  let session = enabledAnonymous;
  let protectedCalls = 0;
  const fetchMock = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    void init;
    const path = String(input);
    if (path === "/api/auth/session") return response(session);
    if (path === "/api/auth/login") {
      session = enabledAuthenticated;
      return response(session);
    }
    if (path === "/api/books") {
      protectedCalls++;
      session = { enabled: true, authenticated: false, csrfToken: "csrf-anonymous-after-expiry" };
      return response({ detail: "Session expired" }, 401);
    }
    return response({ detail: "Not found" }, 404);
  });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(
    <AuthGate>
      {() => <button onClick={() => void request("/api/books").catch(() => undefined)}>Load books</button>}
    </AuthGate>,
  );

  const password = await screen.findByLabelText("Password");
  expect(password).toHaveAttribute("autocomplete", "current-password");
  expect(password).toHaveAttribute("maxlength", "512");
  await user.type(password, "library secret");
  await user.click(screen.getByRole("button", { name: "Sign in" }));
  expect(await screen.findByRole("button", { name: "Load books" })).toBeVisible();
  const loginCall = fetchMock.mock.calls.find(([path]) => String(path) === "/api/auth/login");
  expect(new Headers(loginCall?.[1]?.headers).get("X-CSRF-TOKEN")).toBe("csrf-session");

  await user.click(screen.getByRole("button", { name: "Load books" }));
  expect(await screen.findByText(/Your session expired/)).toBeVisible();
  expect(screen.getByLabelText("Password")).toBeVisible();
  await user.type(screen.getByLabelText("Password"), "library secret");
  await user.click(screen.getByRole("button", { name: "Sign in" }));
  expect(await screen.findByRole("button", { name: "Load books" })).toBeVisible();
  expect(protectedCalls).toBe(1);
  const loginCalls = fetchMock.mock.calls.filter(([path]) => String(path) === "/api/auth/login");
  expect(new Headers(loginCalls.at(-1)?.[1]?.headers).get("X-CSRF-TOKEN")).toBe("csrf-anonymous-after-expiry");
});

it("ignores a protected 401 from a request started before a refreshed sign-in", async () => {
  let session = enabledAuthenticated;
  let finishOldRequest: (value: Response) => void = () => undefined;
  let sessionCalls = 0;
  const fetchMock = vi.fn((input: string | URL | Request) => {
    const path = String(input);
    if (path === "/api/auth/session") {
      sessionCalls++;
      return Promise.resolve(sessionCalls === 1
        ? response(session)
        : response({ enabled: true, authenticated: false, csrfToken: "csrf-refreshed-anonymous" }));
    }
    if (path === "/api/books")
      return new Promise<Response>((resolve) => { finishOldRequest = resolve; });
    if (path === "/api/auth/login") {
      session = enabledAuthenticated;
      return Promise.resolve(response(session));
    }
    return Promise.resolve(response({}));
  });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(
    <AuthGate>
      {({ signOut }) => signOut
        ? <><button onClick={() => void request("/api/books").catch(() => undefined)}>Load books</button><button onClick={() => void signOut()}>Sign out</button></>
        : <span>Signed out</span>}
    </AuthGate>,
  );

  await user.click(await screen.findByRole("button", { name: "Load books" }));
  window.dispatchEvent(new CustomEvent("paparr:session-expired"));
  const password = await screen.findByLabelText("Password");
  await user.type(password, "library secret");
  await user.click(screen.getByRole("button", { name: "Sign in" }));
  expect(await screen.findByRole("button", { name: "Load books" })).toBeVisible();

  finishOldRequest(response({ detail: "Old session expired" }, 401));
  await screen.findByRole("button", { name: "Load books" });
  expect(screen.queryByRole("heading", { name: "Sign in to Paparr" })).not.toBeInTheDocument();
});

it("shows logout errors and uses rotated CSRF tokens for later auth requests", async () => {
  let session = enabledAuthenticated;
  let logoutCount = 0;
  const fetchMock = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    void init;
    const path = String(input);
    if (path === "/api/auth/session") return response(session);
    if (path === "/api/auth/logout") {
      logoutCount++;
      if (logoutCount === 1) return response({ detail: "Could not sign out" }, 503);
      session = { enabled: true, authenticated: false, csrfToken: "csrf-after-logout" };
      return response(session);
    }
    if (path === "/api/auth/login") {
      session = { enabled: true, authenticated: true, csrfToken: "csrf-after-login" };
      return response(session);
    }
    return response({});
  });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(
    <AuthGate>
      {({ signOut }) => signOut
        ? <button onClick={() => void signOut()}>Sign out</button>
        : <span>Signed out</span>}
    </AuthGate>,
  );

  await user.click(await screen.findByRole("button", { name: "Sign out" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Could not sign out");
  const logoutCalls = fetchMock.mock.calls.filter(([path]) => String(path) === "/api/auth/logout");
  expect(new Headers(logoutCalls[0]?.[1]?.headers).get("X-CSRF-TOKEN")).toBe("csrf-authenticated");

  await user.click(screen.getByRole("button", { name: "Sign out" }));
  expect(await screen.findByLabelText("Password")).toBeVisible();
  await user.type(screen.getByLabelText("Password"), "new secret");
  await user.click(screen.getByRole("button", { name: "Sign in" }));
  expect(await screen.findByRole("button", { name: "Sign out" })).toBeVisible();

  const secondLogout = fetchMock.mock.calls.filter(([path]) => String(path) === "/api/auth/logout")[1];
  const loginCall = fetchMock.mock.calls.find(([path]) => String(path) === "/api/auth/login");
  expect(new Headers(secondLogout?.[1]?.headers).get("X-CSRF-TOKEN")).toBe("csrf-authenticated");
  expect(new Headers(loginCall?.[1]?.headers).get("X-CSRF-TOKEN")).toBe("csrf-after-logout");
});
