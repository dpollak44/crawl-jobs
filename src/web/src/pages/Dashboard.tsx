import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useHistory } from "../hooks/useHistory";
import { HistoryTable } from "../components/HistoryTable";
import { JobPanel } from "../components/JobPanel";
import { StartCrawlForm } from "../components/StartCrawlForm";

/**
 * The one page. History on the left, newest first; the selected job on the right. "New crawl" opens the form in
 * place and selects the job it creates. The URL carries the selection (/jobs/:id) so a job can be linked to.
 */
export function Dashboard() {
    const { jobId = null } = useParams();
    const navigate = useNavigate();
    const history = useHistory();
    const [showForm, setShowForm] = useState(false);

    // With nothing selected, open the newest job so the page is never empty.
    useEffect(() => {
        if (!jobId && history.data && history.data.items.length > 0) {
            navigate(`/jobs/${history.data.items[0].jobId}`, { replace: true });
        }
    }, [jobId, history.data, navigate]);

    function onStarted(id: string) {
        setShowForm(false);
        history.refresh();
        navigate(`/jobs/${id}`);
    }

    return (
        <div className="layout">
            <aside className="column list-column">
                <div className="column-head">
                    <h2>Crawls</h2>
                    {!showForm && (
                        <button
                            className="primary"
                            onClick={() => setShowForm(true)}
                        >
                            New crawl
                        </button>
                    )}
                </div>
                {showForm && (
                    <StartCrawlForm
                        onStarted={onStarted}
                        onClose={() => setShowForm(false)}
                    />
                )}
                <HistoryTable
                    data={history.data}
                    loading={history.loading}
                    error={history.error}
                    selectedId={jobId}
                    page={history.page}
                    pages={history.pages}
                    onSelect={(id) => navigate(`/jobs/${id}`)}
                    onPage={history.setPage}
                />
            </aside>
            <main className="column detail-column">
                {jobId ? (
                    <JobPanel
                        key={jobId}
                        jobId={jobId}
                        onChanged={history.refresh}
                    />
                ) : (
                    <div className="card panel empty-panel">
                        <h2>Nothing selected</h2>
                        <p className="muted">
                            Start a crawl, or pick one from the list.
                        </p>
                        {!showForm && (
                            <button
                                className="primary"
                                onClick={() => setShowForm(true)}
                            >
                                New crawl
                            </button>
                        )}
                    </div>
                )}
            </main>
        </div>
    );
}
