// The UI's whole knowledge of the API: shapes mirrored by hand from Crawler.Api/Jobs/JobContracts.cs.

export type JobStatus =
    "Pending" | "Running" | "Completed" | "Failed" | "Canceled";
export type PageStatus = "Queued" | "Fetched" | "Failed" | "Skipped";

export interface JobProgress {
    discovered: number;
    fetched: number;
    failed: number;
    skipped: number;
    queued: number;
}

export interface JobSummary {
    jobId: string;
    url: string;
    status: JobStatus;
    maxDepth: number;
    maxPages: number;
    createdAt: string;
    startedAt: string | null;
    completedAt: string | null;
    failureReason: string | null;
    progress: JobProgress;
}

export interface TreeNode {
    pageId: string;
    url: string;
    depth: number;
    status: PageStatus;
    domainLinkRatio: number;
    internalLinks: number;
    outgoingLinks: number;
    error: string | null;
    children: TreeNode[];
}

export interface Page<T> {
    items: T[];
    pageNumber: number;
    pageSize: number;
    total: number;
}

export const isTerminal = (s: JobStatus): boolean =>
    s === "Completed" || s === "Failed" || s === "Canceled";

/** A job is settled when it has a final status and no page is still waiting: only then is the tree final. */
export const isSettled = (j: JobSummary): boolean =>
    isTerminal(j.status) && j.progress.queued === 0;

/** Pages the worker has dealt with, one way or another: fetched, failed or skipped. */
export const handledCount = (p: JobProgress): number => p.fetched + p.failed + p.skipped;
