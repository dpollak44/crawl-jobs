import { useEffect, useState } from "react";
import { api, errorMessage } from "../api/client";
import { isSettled, type JobSummary, type TreeNode } from "../api/types";

/** The page tree, fetched once per job as soon as the job is settled. */
export function useTree(job: JobSummary | null) {
    const [tree, setTree] = useState<TreeNode | null>(null);
    const [error, setError] = useState<string | null>(null);
    const jobId = job?.jobId ?? null;
    const ready =
        job !== null &&
        isSettled(job) &&
        (job.status !== "Failed" || job.progress.fetched > 0);

    useEffect(() => {
        setTree(null);
        setError(null);
    }, [jobId]);

    useEffect(() => {
        if (!jobId || !ready) return;
        let stale = false;
        api.getTree(jobId)
            .then((t) => {
                if (!stale) setTree(t);
            })
            .catch((err) => {
                if (!stale)
                    setError(errorMessage(err, "Could not load the results."));
            });
        return () => {
            stale = true;
        };
    }, [jobId, ready]);

    return { tree, error, loading: ready && tree === null && error === null };
}
