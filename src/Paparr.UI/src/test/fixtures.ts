import type { ImportJob, Summary } from "../api";

export const emptyDuplicates = { items: [], total: 0, page: 1, pageSize: 25 };

export const job: ImportJob = {
  id: 1,
  fileName: "The Time Machine - H. G. Wells.epub",
  status: "AwaitingApproval",
  createdAt: "2026-10-20T12:00:00Z",
  updatedAt: "2026-10-20T12:01:00Z",
  revision: "revision-1",
  attemptCount: 1,
  lastError: null,
  cleanup: {
    status: "NotRequested",
    attemptCount: 0,
    error: null,
    canRetry: false,
  },
  acceptedBook: null,
  candidates: [
    {
      id: 11,
      title: "The Time Machine",
      author: "H. G. Wells",
      source: "embedded",
      externalId: "",
      confidenceScore: 85,
      evidence: "Read from the ebook metadata.",
    },
    {
      id: 12,
      title: "The Time Machine: An Invention",
      author: "H. G. Wells",
      source: "openlibrary",
      externalId: "/works/OL1W",
      confidenceScore: 81,
      evidence: "Title match 68/100; author match 100/100.",
    },
  ],
  events: [
    {
      message: "Discovered a stable ebook file.",
      createdAt: "2026-10-20T12:00:00Z",
    },
    {
      message: "Metadata is ready for review.",
      createdAt: "2026-10-20T12:01:00Z",
    },
  ],
};
export const summary: Summary = {
  AwaitingApproval: 1,
  Pending: 2,
  Processing: 1,
  Publishing: 0,
  Completed: 24,
  Failed: 0,
};
export const preview = {
  relativePath:
    "H. G. Wells/The Time Machine/The Time Machine - H. G. Wells [abc123].epub",
  destinationExists: false,
  sourceRetained: true,
};
