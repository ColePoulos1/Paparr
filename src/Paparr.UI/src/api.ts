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
export type Summary = Record<Status, number>;
export interface AuthSession {
  enabled: boolean;
  authenticated: boolean;
  csrfToken: string | null;
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

export async function request<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
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
  return response.json() as Promise<T>;
}
