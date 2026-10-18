import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useHistory } from "../hooks/useHistory";
import { HistoryTable } from "../components/HistoryTable";
import { StartCrawlForm } from "../components/StartCrawlForm";

/** The home page: start a crawl, and the list of crawls. Clicking a row opens that job on its own page. */
export function JobsPage() {
    const navigate = useNavigate();
    const history = useHistory();
    const [showForm, setShowForm] = useState(false);

    function onStarted(id: string) {
        setShowForm(false);
        history.refresh();
        navigate(`/jobs/${id}`);
    }

    return (
        <div className="page">
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
                selectedId={null}
                page={history.page}
                pages={history.pages}
                onSelect={(id) => navigate(`/jobs/${id}`)}
                onPage={history.setPage}
            />
        </div>
    );
}
