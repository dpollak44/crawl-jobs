import type { JobSummary, Page, TreeNode } from "./types";

export const API_BASE =
    (import.meta.env.VITE_API_URL as string | undefined) ?? "";

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
        response = await fetch(`${API_BASE}${path}`, {
            ...init,
            headers: { "content-type": "application/json", ...init?.headers },
        });
    } catch {
        throw new ApiError(
            0,
            "Could not reach the server. Is the API running?",
        );
    }
    if (response.status === 404) {
        throw new ApiError(404, "No job with that id exists.");
    }
    if (!response.ok) {
        throw new ApiError(response.status, await readProblem(response));
    }
    return (await response.json()) as T;
}

// The API answers errors as Problem Details; surface the human-readable part, never a bare status code.
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
    /** The live stream address; EventSource cannot go through fetch, so the hook opens it directly. */
    eventsUrl: (jobId: string) => `${API_BASE}/api/jobs/${jobId}/events`,
};

export const errorMessage = (err: unknown, fallback: string): string =>
    err instanceof ApiError ? err.message : fallback;
