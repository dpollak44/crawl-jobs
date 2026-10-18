import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, ApiError, API_BASE, JobSummary, TreeNode } from "../api";

const POLL_MS = 2000;
const isTerminal = (s: JobSummary["status"]) =>
    s === "Completed" || s === "Failed" || s === "Canceled";

type Mode = "live" | "polling";

export function JobDetails() {
    const { jobId = "" } = useParams();
    const [job, setJob] = useState<JobSummary | null>(null);
    const [tree, setTree] = useState<TreeNode | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [canceling, setCanceling] = useState(false);
    const [mode, setMode] = useState<Mode>("live");

    // Live mode: one open stream from the API; the browser does not poll. Any stream error drops to polling.
    useEffect(() => {
        if (mode !== "live" || typeof EventSource === "undefined") {
            if (typeof EventSource === "undefined") setMode("polling");
            return;
        }
        const source = new EventSource(`${API_BASE}/api/jobs/${jobId}/events`);
        let finished = false;
        source.onmessage = (e) => {
            setJob(JSON.parse(e.data) as JobSummary);
            setError(null);
        };
        source.addEventListener("done", async () => {
            finished = true;
            source.close();
            try {
                setTree(await api.getTree(jobId));
            } catch (err) {
                setError(
                    err instanceof ApiError
                        ? err.message
                        : "Could not load the results.",
                );
            }
        });
        source.onerror = () => {
            source.close();
            if (!finished) setMode("polling"); // the fallback below takes over
        };
        return () => source.close();
    }, [jobId, mode]);

    // Fallback: poll every two seconds until the job reaches a final state and nothing is queued, then fetch the tree once.
    useEffect(() => {
        if (mode !== "polling") return;
        let stopped = false;
        let timer: number | undefined;

        async function tick() {
            try {
                const next = await api.getJob(jobId);
                if (stopped) return;
                setJob(next);
                setError(null);
                // A canceled job reaches its final status while in-flight pages are still being settled;
                // keep polling until nothing is queued, so the tree shown is the final one.
                if (isTerminal(next.status) && next.progress.queued === 0) {
                    if (next.status !== "Failed" || next.progress.fetched > 0) {
                        setTree(await api.getTree(jobId));
                    }
                    return;
                }
            } catch (err) {
                if (stopped) return;
                setError(
                    err instanceof ApiError
                        ? err.message
                        : "Something went wrong.",
                );
                if (err instanceof ApiError && err.status === 404) return; // no point polling a job that does not exist
            }
            timer = window.setTimeout(tick, POLL_MS);
        }

        tick();
        return () => {
            stopped = true;
            if (timer) window.clearTimeout(timer);
        };
    }, [jobId, mode]);

    async function onCancel() {
        setCanceling(true);
        try {
            setJob(await api.cancel(jobId));
        } catch (err) {
            setError(
                err instanceof ApiError ? err.message : "Could not cancel.",
            );
        } finally {
            setCanceling(false);
        }
    }

    if (!job && !error) return <p className="muted">Loading job…</p>;
    if (!job && error)
        return (
            <p className="error" role="alert">
                {error}
            </p>
        );
    const j = job!;
    const done = j.progress.fetched + j.progress.failed + j.progress.skipped;
    const settled = isTerminal(j.status) && j.progress.queued === 0;

    return (
        <section className="stack">
            <h2>
                Job <code>{j.jobId}</code>
            </h2>
            <dl className="facts">
                <dt>Address</dt>
                <dd>
                    <a href={j.url} target="_blank" rel="noreferrer">
                        {j.url}
                    </a>
                </dd>
                <dt>Status</dt>
                <dd>
                    <span className={`status status-${j.status.toLowerCase()}`}>
                        {j.status}
                    </span>
                </dd>
                <dt>Depth / page cap</dt>
                <dd>
                    {j.maxDepth} / {j.maxPages}
                </dd>
                <dt>Created</dt>
                <dd>{fmt(j.createdAt)}</dd>
                <dt>Started</dt>
                <dd>{fmt(j.startedAt)}</dd>
                <dt>Completed</dt>
                <dd>{fmt(j.completedAt)}</dd>
                {j.failureReason && (
                    <>
                        <dt>Reason</dt>
                        <dd className="error">{j.failureReason}</dd>
                    </>
                )}
            </dl>

            <div>
                <progress
                    value={done}
                    max={Math.max(j.progress.discovered, 1)}
                />
                <p className="muted">
                    {done} of {j.progress.discovered} discovered pages handled
                    {j.progress.queued > 0 && ` · ${j.progress.queued} waiting`}
                    {j.progress.failed > 0 && ` · ${j.progress.failed} failed`}
                    {j.progress.skipped > 0 &&
                        ` · ${j.progress.skipped} skipped`}
                    {!settled &&
                        (mode === "live"
                            ? " · live"
                            : " · refreshing every 2 s")}
                </p>
            </div>

            {!isTerminal(j.status) && (
                <button onClick={onCancel} disabled={canceling}>
                    {canceling ? "Canceling…" : "Cancel job"}
                </button>
            )}
            {error && (
                <p className="error" role="alert">
                    {error}
                </p>
            )}

            {settled && !tree && j.status !== "Failed" && (
                <p className="muted">Loading results…</p>
            )}
            {tree && (
                <>
                    <h3>Pages</h3>
                    <p className="muted">
                        Each page shows its Domain Link Ratio: the share of its
                        outgoing links that stay on {new URL(j.url).host}.
                    </p>
                    <ul className="tree">
                        <Node node={tree} />
                    </ul>
                </>
            )}
            <p>
                <Link to="/history">← History</Link>
            </p>
        </section>
    );
}

function Node({ node }: { node: TreeNode }) {
    return (
        <li>
            <span className={`status status-${node.status.toLowerCase()}`}>
                {node.status}
            </span>{" "}
            <a href={node.url} target="_blank" rel="noreferrer">
                {node.url}
            </a>
            {node.status === "Fetched" && (
                <span className="muted">
                    {" "}
                    · ratio {node.domainLinkRatio.toFixed(2)} (
                    {node.internalLinks} of {node.outgoingLinks} links on-site)
                </span>
            )}
            {node.error && <span className="muted"> · {node.error}</span>}
            {node.children.length > 0 && (
                <ul>
                    {node.children.map((c) => (
                        <Node key={c.pageId} node={c} />
                    ))}
                </ul>
            )}
        </li>
    );
}

function fmt(iso: string | null) {
    return iso ? new Date(iso).toLocaleString() : "—";
}
