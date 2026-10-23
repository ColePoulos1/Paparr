import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { request, setCsrfToken } from "../api";
import type { AuthSession } from "../api";
import "./AuthGate.css";

interface AuthGateProps {
  children: (context: { signOut?: () => Promise<void> }) => ReactNode;
}

export default function AuthGate({ children }: AuthGateProps) {
  const [session, setSession] = useState<AuthSession | null>(null);
  const [loadError, setLoadError] = useState("");
  const [loadAttempt, setLoadAttempt] = useState(0);
  const [password, setPassword] = useState("");
  const [loginError, setLoginError] = useState("");
  const [loginPending, setLoginPending] = useState(false);
  const [logoutPending, setLogoutPending] = useState(false);
  const [logoutError, setLogoutError] = useState("");
  const [sessionExpired, setSessionExpired] = useState(false);
  const sessionRequest = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    sessionRequest.current = controller;
    setLoadError("");
    request<AuthSession>("/api/auth/session", { signal: controller.signal })
      .then((value) => {
        if (controller.signal.aborted) return;
        setCsrfToken(value.csrfToken);
        setSession(value);
        setSessionExpired((wasExpired) => wasExpired && !value.authenticated);
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setLoadError(reason.message);
      });
    return () => {
      controller.abort();
      if (sessionRequest.current === controller) sessionRequest.current = null;
    };
  }, [loadAttempt]);

  useEffect(() => {
    const expired = () => {
      sessionRequest.current?.abort();
      setSession(null);
      setLoadError("");
      setSessionExpired(true);
      setLoginError("");
      setLogoutError("");
      setLoadAttempt((value) => value + 1);
    };
    window.addEventListener("paparr:session-expired", expired);
    return () => window.removeEventListener("paparr:session-expired", expired);
  }, []);

  const signOut = useCallback(async () => {
    if (!session?.enabled || !session.authenticated || logoutPending) return;
    setLogoutPending(true);
    setLogoutError("");
    try {
      const next = await request<AuthSession>("/api/auth/logout", { method: "POST" });
      setCsrfToken(next.csrfToken);
      setSession(next);
      setSessionExpired(false);
      setPassword("");
    } catch (reason) {
      setLogoutError(reason instanceof Error ? reason.message : "Could not sign out. Try again.");
    } finally {
      setLogoutPending(false);
    }
  }, [logoutPending, session]);

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (loginPending) return;
    setLoginPending(true);
    setLoginError("");
    try {
      const next = await request<AuthSession>("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({ password }),
      });
      setCsrfToken(next.csrfToken);
      setSession(next);
      setSessionExpired(false);
      setPassword("");
    } catch (reason) {
      setLoginError(reason instanceof Error ? reason.message : "Sign in failed. Check the password and try again.");
    } finally {
      setLoginPending(false);
    }
  }

  if (loadError) {
    return (
      <main className="auth-shell">
        <section className="auth-card" aria-labelledby="auth-title">
          <div className="eyebrow">PAPARR ACCESS</div>
          <h1 id="auth-title">Could not check your session</h1>
          {sessionExpired && <div className="notice auth-expired" role="status">Your session expired. Sign in again to continue.</div>}
          <div className="notice error" role="alert">{loadError}</div>
          <button className="button primary" onClick={() => setLoadAttempt((value) => value + 1)}>Retry</button>
        </section>
      </main>
    );
  }

  if (!session) {
    return <main className="auth-shell"><div className="auth-loading" role="status">Checking your session…</div></main>;
  }

  if (!session.enabled || session.authenticated) {
    return (
      <>
        {logoutError && <div className="notice error auth-logout-error" role="alert">{logoutError}</div>}
        {children({ signOut: session.enabled ? signOut : undefined })}
      </>
    );
  }

  return (
    <main className="auth-shell">
      <section className="auth-card" aria-labelledby="auth-title">
        <div className="auth-mark" aria-hidden="true">p</div>
        <div className="eyebrow">YOUR PRIVATE LIBRARY</div>
        <h1 id="auth-title">Sign in to Paparr</h1>
        <p className="muted">Enter your library password to continue.</p>
        {sessionExpired && <div className="notice auth-expired" role="status">Your session expired. Sign in again to continue.</div>}
        {loginError && <div className="notice error" role="alert">{loginError}</div>}
        <form className="auth-form" onSubmit={(event) => void submit(event)}>
          <label htmlFor="paparr-password">Password</label>
          <input
            id="paparr-password"
            name="password"
            type="password"
            autoComplete="current-password"
            maxLength={512}
            required
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            disabled={loginPending}
          />
          <button className="button primary" type="submit" disabled={loginPending || password.length === 0}>
            {loginPending ? "Signing in…" : "Sign in"}
          </button>
        </form>
      </section>
    </main>
  );
}
