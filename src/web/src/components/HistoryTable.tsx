import type { JobSummary, Page } from "../api/types";
import { StatusBadge } from "./StatusBadge";
import { formatShort, hostOf } from "../lib/format";

interface Props {
    data: Page<JobSummary> | null;
    loading: boolean;
    error: string | null;
    selectedId: string | null;
    page: number;
    pages: number;
    onSelect: (jobId: string) => void;
    onPage: (page: number) => void;
}

export function HistoryTable({
    data,
    loading,
    error,
    selectedId,
    page,
    pages,
    onSelect,
    onPage,
}: Props) {
    if (loading && !data) return <p className="muted">Loading jobs…</p>;
    if (error && !data)
        return (
            <p className="error" role="alert">
                {error}
            </p>
        );
    if (!data) return null;

    const first = data.total === 0 ? 0 : (page - 1) * data.pageSize + 1;
    const last = Math.min(page * data.pageSize, data.total);

    return (
        <div className="history">
            {error && (
                <p className="error" role="alert">
                    {error}
                </p>
            )}
            {data.items.length === 0 ? (
                <p className="empty">
                    No crawls yet. Start one to see it here.
                </p>
            ) : (
                <table>
                    <thead>
                        <tr>
                            <th>Site</th>
                            <th>Status</th>
                            <th className="num">Pages</th>
                            <th>Started</th>
                        </tr>
                    </thead>
                    <tbody>
                        {data.items.map((j) => {
                            const done =
                                j.progress.fetched +
                                j.progress.failed +
                                j.progress.skipped;
                            return (
                                <tr
                                    key={j.jobId}
                                    className={
                                        j.jobId === selectedId
                                            ? "selected"
                                            : undefined
                                    }
                                    onClick={() => onSelect(j.jobId)}
                                    tabIndex={0}
                                    onKeyDown={(e) => {
                                        if (e.key === "Enter")
                                            onSelect(j.jobId);
                                    }}
                                    aria-selected={j.jobId === selectedId}
                                >
                                    <td>
                                        <div className="site">
                                            {hostOf(j.url)}
                                        </div>
                                        <div className="muted small">
                                            {j.url}
                                        </div>
                                    </td>
                                    <td>
                                        <StatusBadge status={j.status} />
                                    </td>
                                    <td className="num">
                                        {done}
                                        <span className="muted">
                                            {" "}
                                            / {j.progress.discovered}
                                        </span>
                                    </td>
                                    <td className="muted small">
                                        {formatShort(j.createdAt)}
                                    </td>
                                </tr>
                            );
                        })}
                    </tbody>
                </table>
            )}
            <div className="pager">
                <span className="muted small">
                    Showing {first}–{last} of {data.total}
                </span>
                {pages > 1 && (
                    <span className="pager-buttons">
                        <button
                            className="ghost"
                            onClick={() => onPage(page - 1)}
                            disabled={page <= 1}
                        >
                            Newer
                        </button>
                        <span className="muted small">
                            page {page} of {pages}
                        </span>
                        <button
                            className="ghost"
                            onClick={() => onPage(page + 1)}
                            disabled={page >= pages}
                        >
                            Older
                        </button>
                    </span>
                )}
            </div>
        </div>
    );
}
