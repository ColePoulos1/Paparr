import type {
  BookDto, DuplicateSourceDtoPagedResult, EditionDto, ImportJobDto,
  ImportJobDtoPagedResult, ImportPreviewDto, LibraryAuditDto, LibraryBookDto,
  LibraryPageDto, MetadataCandidateDto, SessionDto,
  WorkerDiagnosticsSnapshot,
} from "./generated/types.gen";

export type Status = ImportJobDto["status"];
export type Candidate = MetadataCandidateDto;
export type Book = BookDto;
export type Edition = EditionDto;
export type ImportJob = ImportJobDto;
export type Page = ImportJobDtoPagedResult;
export type Preview = ImportPreviewDto;
export type DuplicatePage = DuplicateSourceDtoPagedResult;
export type Summary = Record<Status, number>;
export type AuthSession = SessionDto;
export type WorkerSnapshot = Omit<WorkerDiagnosticsSnapshot, "state"> & {
  state: "Disabled" | "Starting" | "Running" | "Idle" | "Delayed";
};
export type IntegrityStatus = LibraryBookDto["integrityStatus"];
export type LibraryBook = LibraryBookDto;
export type LibraryPage = Omit<LibraryPageDto, "counts"> & {
  counts: Record<IntegrityStatus, number>;
};
export type LibraryAudit = LibraryAuditDto;

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
  "Undoing",
  "Failed",
  "Completed",
];
export const labels: Record<Status, string> = {
  AwaitingApproval: "Needs review",
  Pending: "Queued",
  Processing: "Reading metadata",
  Publishing: "Saving to library",
  Undoing: "Returning to review",
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

export async function downloadBook(id: number, signal: AbortSignal, kind: "file" | "export" = "file"): Promise<void> {
  const response = await fetchResponse(`/api/library/${id}/${kind}`, { signal });
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
