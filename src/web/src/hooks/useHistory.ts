import { useCallback, useEffect, useState } from "react";
import { api, errorMessage } from "../api/client";
import { isTerminal, type JobSummary, type Page } from "../api/types";

const PAGE_SIZE = 20;
const REFRESH_MS = 4000;

/**
 * The job list, newest first. Refreshes on demand (after a new crawl) and on its own every few seconds while
 * any listed job is still running, so statuses and counts in the table move without a reload.
 */
export function useHistory() {
    const [page, setPage] = useState(1);
    const [data, setData] = useState<Page<JobSummary> | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);
    const [version, setVersion] = useState(0);

    const refresh = useCallback(() => setVersion((v) => v + 1), []);

    useEffect(() => {
        let stale = false;
        api.history(page, PAGE_SIZE)
            .then((d) => {
                if (!stale) {
                    setData(d);
                    setError(null);
                }
            })
            .catch((err) => {
                if (!stale)
                    setError(errorMessage(err, "Could not load the job list."));
            })
            .finally(() => {
                if (!stale) setLoading(false);
            });
        return () => {
            stale = true;
        };
    }, [page, version]);

    const anyActive = data?.items.some((j) => !isTerminal(j.status)) ?? false;
    useEffect(() => {
        if (!anyActive) return;
        const timer = window.setInterval(refresh, REFRESH_MS);
        return () => window.clearInterval(timer);
    }, [anyActive, refresh]);

    const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;
    return { data, error, loading, page, pages, setPage, refresh };
}
