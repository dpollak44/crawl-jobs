// The UI's whole knowledge of the API: three shapes and five calls. Mirrors Crawler.Api/Jobs/JobContracts.cs.
// Kept by hand rather than generated: two languages, three small shapes, no build step worth adding.

export type JobStatus =
    "Pending" | "Running" | "Completed" | "Failed" | "Canceled";

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
    status: "Queued" | "Fetched" | "Failed" | "Skipped";
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

const BASE = (import.meta.env.VITE_API_URL as string | undefined) ?? "";

export class ApiError extends Error {
    constructor(
        public status: number,
        message: string,
    ) {
        super(message);
    }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
    let response: Response;
    try {
        response = await fetch(`${BASE}${path}`, {
            ...init,
            headers: { "content-type": "application/json", ...init?.headers },
        });
    } catch {
        throw new ApiError(
            0,
            "Could not reach the server. Is the API running?",
        );
    }
    if (!response.ok) {
        throw new ApiError(response.status, await readProblem(response));
    }
    return (await response.json()) as T;
}

// The API answers errors as Problem Details; surface the human-readable part, never a raw status code alone.
async function readProblem(response: Response): Promise<string> {
    try {
        const body = await response.json();
        if (body.errors) {
            return Object.values<string[]>(body.errors).flat().join(" ");
        }
        return (
            body.detail ?? body.title ?? `Request failed (${response.status}).`
        );
    } catch {
        return `Request failed (${response.status}).`;
    }
}

export const api = {
    createJob: (url: string, maxDepth?: number) =>
        request<{ jobId: string }>("/api/jobs", {
            method: "POST",
            body: JSON.stringify({ url, maxDepth: maxDepth ?? null }),
        }),
    getJob: (jobId: string) => request<JobSummary>(`/api/jobs/${jobId}`),
    getTree: (jobId: string) => request<TreeNode>(`/api/jobs/${jobId}/tree`),
    history: (page: number, pageSize = 20) =>
        request<Page<JobSummary>>(
            `/api/jobs?page=${page}&pageSize=${pageSize}`,
        ),
    cancel: (jobId: string) =>
        request<JobSummary>(`/api/jobs/${jobId}/cancel`, { method: "POST" }),
};
