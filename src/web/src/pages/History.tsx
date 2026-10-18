import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, ApiError, JobSummary, Page } from "../api";

const PAGE_SIZE = 20;

export function History() {
    const [page, setPage] = useState(1);
    const [data, setData] = useState<Page<JobSummary> | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        let stale = false;
        setLoading(true);
        api.history(page, PAGE_SIZE)
            .then((d) => {
                if (!stale) {
                    setData(d);
                    setError(null);
                }
            })
            .catch((err) => {
                if (!stale)
                    setError(
                        err instanceof ApiError
                            ? err.message
                            : "Something went wrong.",
                    );
            })
            .finally(() => {
                if (!stale) setLoading(false);
            });
        return () => {
            stale = true;
        };
    }, [page]);

    const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

    return (
        <section className="stack">
            <h2>History</h2>
            {loading && <p className="muted">Loading…</p>}
            {error && (
                <p className="error" role="alert">
                    {error}
                </p>
            )}
            {data && data.items.length === 0 && (
                <p className="muted">
                    No crawls yet. <Link to="/">Start one.</Link>
                </p>
            )}
            {data && data.items.length > 0 && (
                <table>
                    <thead>
                        <tr>
                            <th>Address</th>
                            <th>Status</th>
                            <th>Pages</th>
                            <th>Created</th>
                            <th>Completed</th>
                        </tr>
                    </thead>
                    <tbody>
                        {data.items.map((j) => (
                            <tr key={j.jobId}>
                                <td>
                                    <Link to={`/jobs/${j.jobId}`}>{j.url}</Link>
                                </td>
                                <td>
                                    <span
                                        className={`status status-${j.status.toLowerCase()}`}
                                    >
                                        {j.status}
                                    </span>
                                </td>
                                <td>
                                    {j.progress.fetched} /{" "}
                                    {j.progress.discovered}
                                </td>
                                <td>
                                    {new Date(j.createdAt).toLocaleString()}
                                </td>
                                <td>
                                    {j.completedAt
                                        ? new Date(
                                              j.completedAt,
                                          ).toLocaleString()
                                        : "—"}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
            {data && pages > 1 && (
                <p>
                    <button
                        onClick={() => setPage((p) => p - 1)}
                        disabled={page <= 1}
                    >
                        Newer
                    </button>{" "}
                    page {page} of {pages}{" "}
                    <button
                        onClick={() => setPage((p) => p + 1)}
                        disabled={page >= pages}
                    >
                        Older
                    </button>
                </p>
            )}
        </section>
    );
}
