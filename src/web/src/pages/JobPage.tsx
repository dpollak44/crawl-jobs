import { Link, useParams } from "react-router-dom";
import { JobPanel } from "../components/JobPanel";

/** One job on its own page: status, timings, live progress, and the page tree once it is final. */
export function JobPage() {
    const { jobId = "" } = useParams();
    return (
        <div className="page">
            <p className="back">
                <Link to="/">← All crawls</Link>
            </p>
            <JobPanel key={jobId} jobId={jobId} />
        </div>
    );
}
