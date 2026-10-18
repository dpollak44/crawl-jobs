import { useEffect, useRef, useState } from "react";
import { api, errorMessage } from "../api/client";
import { isSettled, type JobSummary } from "../api/types";

export type UpdateMode = "live" | "polling";

const POLL_MS = 2000;
/** After a stream error, stay in polling at least this long before trying the stream again (no flapping). */
const REOPEN_AFTER_MS = 10_000;

/**
 * One job's summary, kept current. Live mode opens the API's event stream (no polling); any stream error drops
 * to polling every two seconds, which stays here as the safety net. Once a poll succeeds again and the job is
 * not yet settled, the stream is reopened, so recovery returns the page to live mode without a reload.
 * Updates stop once the job is settled.
 */
export function useJob(jobId: string | null) {
    const [job, setJob] = useState<JobSummary | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [mode, setMode] = useState<UpdateMode>(() => (typeof EventSource === "undefined" ? "polling" : "live"));
    const lastStreamErrorAt = useRef(0);

    // Reset when the selected job changes.
    useEffect(() => {
        setJob(null);
        setError(null);
        lastStreamErrorAt.current = 0;
        setMode(typeof EventSource === "undefined" ? "polling" : "live");
    }, [jobId]);

    useEffect(() => {
        if (!jobId || mode !== "live") return;
        const source = new EventSource(api.eventsUrl(jobId));
        let finished = false;
        source.onmessage = (e) => {
            try {
                setJob(JSON.parse(e.data) as JobSummary);
                setError(null);
            } catch {
                /* a malformed event is skipped; the next one or the safety re-read replaces it */
            }
        };
        source.addEventListener("done", () => {
            finished = true;
            source.close();
        });
        source.onerror = () => {
            source.close();
            if (!finished) {
                lastStreamErrorAt.current = Date.now();
                setMode("polling");
            }
        };
        return () => source.close();
    }, [jobId, mode]);

    useEffect(() => {
        if (!jobId || mode !== "polling") return;
        let stopped = false;
        let timer: number | undefined;
        async function tick() {
            try {
                const next = await api.getJob(jobId!);
                if (stopped) return;
                setJob(next);
                setError(null);
                if (isSettled(next)) return;
                // The API answers again: go back to the stream, unless it failed on us a moment ago.
                if (
                    typeof EventSource !== "undefined" &&
                    Date.now() - lastStreamErrorAt.current >= REOPEN_AFTER_MS
                ) {
                    setMode("live");
                    return;
                }
            } catch (err) {
                if (stopped) return;
                setError(errorMessage(err, "Something went wrong."));
                if (
                    err instanceof Object &&
                    "status" in err &&
                    (err as { status: number }).status === 404
                )
                    return;
            }
            timer = window.setTimeout(tick, POLL_MS);
        }
        tick();
        return () => {
            stopped = true;
            if (timer) window.clearTimeout(timer);
        };
    }, [jobId, mode]);

    return { job, setJob, error, mode };
}
