import { Banner } from "@astryxdesign/core/Banner";
import { Button } from "@astryxdesign/core/Button";
import { Card } from "@astryxdesign/core/Card";
import { Heading } from "@astryxdesign/core/Heading";
import { TextInput } from "@astryxdesign/core/TextInput";
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
    if (loginPending || password.length === 0) return;
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

  if (!loadError && session && (!session.enabled || session.authenticated)) {
    return (
      <>
        {logoutError && <Banner className="auth-logout-error" status="error" title={logoutError} role="alert" />}
        {children({ signOut: session.enabled ? signOut : undefined })}
      </>
    );
  }

  if (!loadError && !session) {
    return <main className="auth-shell"><p role="status">Checking your session…</p></main>;
  }

  return (
    <main className="auth-shell">
      <Card className="auth-card" padding={8} role="region" aria-labelledby="auth-title">
        <img className="auth-mark" src="/paparr.svg" width="48" height="48" alt="" />
        <Heading level={1} id="auth-title">
          {loadError ? "Could not check your session" : "Sign in to Paparr"}
        </Heading>
        {sessionExpired && (
          <Banner status="warning" role="status" title="Your session expired. Sign in again to continue." />
        )}
        {loadError ? (
          <>
            <Banner status="error" role="alert" title={loadError} />
            <Button label="Retry" onClick={() => setLoadAttempt((value) => value + 1)} />
          </>
        ) : (
          <>
            <p>Enter your library password to continue.</p>
            {loginError && <Banner status="error" role="alert" title={loginError} />}
            <form className="auth-form" onSubmit={submit}>
              <TextInput
                label="Password"
                type="password"
                htmlName="password"
                autoComplete="current-password"
                {...{ maxLength: 512, required: true }}
                isRequired
                size="lg"
                value={password}
                onChange={setPassword}
                isDisabled={loginPending}
              />
              <Button
                type="submit"
                variant="primary"
                size="lg"
                label={loginPending ? "Signing in…" : "Sign in"}
                isDisabled={loginPending || password.length === 0}
              />
            </form>
          </>
        )}
      </Card>
    </main>
  );
}
