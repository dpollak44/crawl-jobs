import { handledCount, type JobSummary, type Page } from "../api/types";
import { ErrorText } from "./ErrorText";
import { StatusBadge } from "./StatusBadge";
import { formatShort, hostOf } from "../lib/format";

interface Props {
    data: Page<JobSummary> | null;
    loading: boolean;
    error: string | null;
    page: number; // requested
    shownPage: number; // what the rows on screen belong to
    pages: number;
    onSelect: (jobId: string) => void;
    onPage: (page: number) => void;
    onRetry: () => void; // ask again for `page` after an error
}

export function HistoryTable({
    data,
    loading,
    error,
    page,
    shownPage,
    pages,
    onSelect,
    onPage,
    onRetry,
}: Props) {
    if (loading && !data) return <p className="muted">Loading jobs…</p>;
    if (error && !data) {
        return (
            <ErrorText>{error}{" "}
                <button className="ghost tiny" onClick={onRetry}>
                    Retry
                </button></ErrorText>
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
                <ErrorText>{error}{" "}
                    <button className="ghost tiny" onClick={onRetry}>
                        Retry
                    </button></ErrorText>
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
                                <th>Created</th>
                            </tr>
                        </thead>
                        <tbody>
                            {data.items.map((j) => {
                                const done = handledCount(j.progress);
                                return (
                                    <tr
                                        key={j.jobId}
                                        onClick={() => onSelect(j.jobId)}
                                        tabIndex={0}
                                        onKeyDown={(e) => {
                                            if (e.key === "Enter")
                                                onSelect(j.jobId);
                                        }}
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
