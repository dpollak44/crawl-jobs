import { useState } from "react";
import { Link } from "react-router-dom";
import { api, errorMessage } from "../api/client";
import { isSettled, isTerminal } from "../api/types";
import { useJob } from "../hooks/useJob";
import { useTree } from "../hooks/useTree";
import { StatusBadge } from "./StatusBadge";
import { PageTree } from "./PageTree";
import { duration, formatDateTime, hostOf } from "../lib/format";
import { useState as useCopiedState } from "react";

interface Props {
    jobId: string;
    onChanged: () => void; // tell the history list to refresh (e.g. after cancel)
}

/** One job: status, timings, live progress, and the page tree once it is final. */
export function JobPanel({ jobId, onChanged }: Props) {
    const { job, setJob, error, setError, mode } = useJob(jobId);
    const { tree, error: treeError, loading: treeLoading } = useTree(job);
    const [canceling, setCanceling] = useState(false);
    const [copied, setCopied] = useCopiedState(false);

    async function onCancel() {
        setCanceling(true);
        try {
            setJob(await api.cancel(jobId));
            onChanged();
        } catch (err) {
            setError(errorMessage(err, "Could not cancel."));
        } finally {
            setCanceling(false);
        }
    }

    if (!job && !error)
        return (
            <div className="card panel">
                <p className="muted">Loading job…</p>
            </div>
        );
    if (!job)
        return (
            <div className="card panel empty-panel">
                <p className="error" role="alert">{error}</p>
                <p className="muted small">The address may belong to a job that no longer exists.</p>
                <Link className="button-link" to="/">Show the latest crawl</Link>
            </div>
        );

    const done =
        job.progress.fetched + job.progress.failed + job.progress.skipped;
    const settled = isSettled(job);
    const took = duration(job.startedAt, job.completedAt);

    async function copyId() {
        try {
            await navigator.clipboard.writeText(job!.jobId);
            setCopied(true);
            window.setTimeout(() => setCopied(false), 1500);
        } catch {
            /* clipboard unavailable: the id is still selectable text */
        }
    }

    return (
        <div className="card panel">
            <header className="panel-head">
                <div>
                    <h2>{hostOf(job.url)}</h2>
                    <a
                        className="muted small"
                        href={job.url}
                        target="_blank"
                        rel="noreferrer"
                    >
                        {job.url}
                    </a>
                </div>
                <div className="panel-actions">
                    <StatusBadge status={job.status} />
                    {!isTerminal(job.status) && (
                        <button
                            className="ghost"
                            onClick={onCancel}
                            disabled={canceling}
                        >
                            {canceling ? "Canceling…" : "Cancel"}
                        </button>
                    )}
                </div>
            </header>
            <dl className="facts">
                <div className="fact-wide">
                    <dt>Job id</dt>
                    <dd>
                        <code className="job-id">{job.jobId}</code>{" "}
                        <button className="ghost tiny" onClick={copyId} aria-label="Copy job id">{copied ? "Copied" : "Copy"}</button>
                    </dd>
                </div>
                <div><dt>Depth</dt><dd>{job.maxDepth}</dd></div>
                <div><dt>Page cap</dt><dd>{job.maxPages}</dd></div>
                <div><dt>Created</dt><dd>{formatDateTime(job.createdAt)}</dd></div>
                <div><dt>Started</dt><dd>{job.startedAt ? formatDateTime(job.startedAt) : "Not started"}</dd></div>
                <div><dt>Completed</dt><dd>{job.completedAt ? formatDateTime(job.completedAt) : "—"}</dd></div>
                {took && (
                    <div>
                        <dt>Took</dt>
                        <dd>{took}</dd>
                    </div>
                )}
            </dl>

            <div className="progress-block">
                <div className="progress-track">
                    <div
                        className="progress-fill"
                        style={{
                            width: `${Math.round((done / Math.max(job.progress.discovered, 1)) * 100)}%`,
                        }}
                    />
                </div>
                <p className="muted small progress-text">
                    <strong>{done}</strong> of{" "}
                    <strong>{job.progress.discovered}</strong> discovered pages
                    handled
                    {job.progress.queued > 0 && (
                        <> · {job.progress.queued} waiting</>
                    )}
                    {job.progress.failed > 0 && (
                        <> · {job.progress.failed} failed</>
                    )}
                    {job.progress.skipped > 0 && (
                        <> · {job.progress.skipped} skipped</>
                    )}
                    {!settled && (
                        <span className={`live ${mode}`}>
                            {mode === "live" ? "live" : "refreshing every 2 s"}
                        </span>
                    )}
                </p>
            </div>

            {job.failureReason && (
                <p className="error" role="alert">
                    {job.failureReason}
                </p>
            )}
            {error && (
                <p className="error" role="alert">
                    {error}
                </p>
            )}
            {treeError && (
                <p className="error" role="alert">
                    {treeError}
                </p>
            )}
            {treeLoading && <p className="muted">Loading results…</p>}
            {tree && <PageTree root={tree} host={hostOf(job.url)} />}
        </div>
    );
}
