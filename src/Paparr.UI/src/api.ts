export type Status =
  | "Pending"
  | "Processing"
  | "AwaitingApproval"
  | "Publishing"
  | "Completed"
  | "Failed";
export interface Candidate {
  id: number;
  title: string;
  author: string;
  source: string;
  externalId: string;
  confidenceScore: number;
  evidence: string;
}
export interface Book {
  id: number;
  title: string;
  author: string;
  source: string;
  externalId: string;
  importedAt: string;
}
export interface ImportJob {
  id: number;
  fileName: string;
  status: Status;
  createdAt: string;
  updatedAt: string;
  revision: string;
  attemptCount: number;
  lastError: string | null;
  cleanup: {
    status: "NotRequested" | "Pending" | "Completed" | "Retained";
    attemptCount: number;
    error: string | null;
    canRetry: boolean;
  };
  candidates: Candidate[];
  acceptedBook: Book | null;
  events: { message: string; createdAt: string }[];
}
export interface Page {
  items: ImportJob[];
  total: number;
  page: number;
  pageSize: number;
}
export interface Preview {
  relativePath: string;
  destinationExists: boolean;
  sourceRetained: boolean;
}
export interface DuplicatePage {
  items: {
    id: number;
    fileName: string;
    relativePath: string | null;
    discoveredAt: string;
  }[];
  total: number;
  page: number;
  pageSize: number;
}
export type Summary = Record<Status, number>;
export interface AuthSession {
  enabled: boolean;
  authenticated: boolean;
  csrfToken: string | null;
}
export interface WorkerSnapshot {
  enabled: boolean;
  state: "Disabled" | "Starting" | "Running" | "Idle" | "Delayed";
  startedAt: string;
  lastProgressAt: string | null;
  staleAfterSeconds: number;
  currentStage: string | null;
  stages: {
    key: string;
    state: "NotRun" | "Running" | "Succeeded" | "Failed" | "Canceled";
    lastAttemptAt: string | null;
    lastSuccessAt: string | null;
    lastFailureAt: string | null;
    lastDurationMs: number | null;
    error: string | null;
  }[];
}

let csrfToken: string | null = null;
let sessionGeneration = 0;

export function setCsrfToken(token: string | null): void {
  csrfToken = token;
  sessionGeneration++;
}

function isAuthEndpoint(path: string): boolean {
  try {
    return new URL(path, window.location.origin).pathname.startsWith("/api/auth/");
  } catch {
    return path.startsWith("/api/auth/");
  }
}

export const statuses: Status[] = [
  "AwaitingApproval",
  "Pending",
  "Processing",
  "Publishing",
  "Failed",
  "Completed",
];
export const labels: Record<Status, string> = {
  AwaitingApproval: "Needs review",
  Pending: "Queued",
  Processing: "Reading metadata",
  Publishing: "Saving to library",
  Failed: "Needs attention",
  Completed: "Imported",
};

async function fetchResponse(
  path: string,
  options: RequestInit = {},
): Promise<Response> {
  const headers = new Headers(options.headers);
  if (options.body != null && !headers.has("Content-Type"))
    headers.set("Content-Type", "application/json");
  const method = (options.method ?? "GET").toUpperCase();
  const requestGeneration = sessionGeneration;
  if (!/^(GET|HEAD|OPTIONS)$/.test(method) && csrfToken)
    headers.set("X-CSRF-TOKEN", csrfToken);
  const response = await fetch(path, {
    ...options,
    headers,
  });
  if (!response.ok) {
    if (response.status === 401 && !isAuthEndpoint(path) && requestGeneration === sessionGeneration)
      window.dispatchEvent(new CustomEvent("paparr:session-expired"));
    const problem = (await response.json().catch(() => null)) as {
      detail?: string;
      title?: string;
    } | null;
    throw new Error(
      problem?.detail ??
        problem?.title ??
        "The server could not complete this request.",
    );
  }
  return response;
}

export async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  return (await fetchResponse(path, options)).json() as Promise<T>;
}

export async function downloadBook(id: number, signal: AbortSignal): Promise<void> {
  const response = await fetchResponse(`/api/library/${id}/file`, { signal });
  const disposition = response.headers.get("Content-Disposition") ?? "";
  const encodedName = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  const fileName = encodedName ? decodeURIComponent(encodedName)
    : /filename="([^"]+)"/i.exec(disposition)?.[1];
  if (!fileName) throw new Error("The download did not include a filename. Please try again.");
  const blob = await response.blob();
  signal.throwIfAborted();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 60000);
}
