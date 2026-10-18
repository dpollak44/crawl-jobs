import type { JobSummary, Page } from "../api/types";
import { StatusBadge } from "./StatusBadge";
import { formatShort, hostOf } from "../lib/format";

interface Props {
    data: Page<JobSummary> | null;
    loading: boolean;
    error: string | null;
    selectedId: string | null;
    page: number; // requested
    shownPage: number; // what the rows on screen belong to
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
    shownPage,
    pages,
    onSelect,
    onPage,
}: Props) {
    if (loading && !data) return <p className="muted">Loading jobs…</p>;
    if (error && !data) {
        return (
            <p className="error" role="alert">
                {error}{" "}
                <button className="ghost tiny" onClick={() => onPage(page)}>
                    Retry
                </button>
            </p>
        );
    }
    if (!data) return null;

    // Labels describe the rows actually on screen, never the page that is still loading or failed to load.
    const first = data.total === 0 ? 0 : (shownPage - 1) * data.pageSize + 1;
    const last = Math.min(shownPage * data.pageSize, data.total);
    const switching = loading && page !== shownPage;

    return (
        <div className="history">
            {error && (
                <p className="error" role="alert">
                    {error}{" "}
                    <button className="ghost tiny" onClick={() => onPage(page)}>
                        Retry
                    </button>
                </p>
            )}
            {switching && <p className="muted small">Loading page {page}…</p>}
            {data.items.length === 0 ? (
                <p className="empty">
                    No crawls yet. Start one to see it here.
                </p>
            ) : (
                <div className={`table-scroll${switching ? " stale" : ""}`}>
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
                </div>
            )}
            <div className="pager">
                <span className="muted small">
                    Showing {first}–{last} of {data.total}
                </span>
                <span className="pager-buttons">
                    <button
                        className="ghost"
                        onClick={() => onPage(shownPage - 1)}
                        disabled={loading || shownPage <= 1}
                    >
                        Newer
                    </button>
                    <span className="muted small">
                        page {shownPage} of {pages}
                    </span>
                    <button
                        className="ghost"
                        onClick={() => onPage(shownPage + 1)}
                        disabled={loading || shownPage >= pages}
                    >
                        Older
                    </button>
                </span>
            </div>
        </div>
    );
}
