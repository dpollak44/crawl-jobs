import { useEffect, useState } from "react";
import { api, errorMessage } from "../api/client";
import { isSettled, type JobSummary } from "../api/types";

export type UpdateMode = "live" | "polling";

const POLL_MS = 2000;

/**
 * One job's summary, kept current. Live mode opens the API's event stream (no polling); any stream error drops
 * to polling every two seconds, which stays here as the safety net. Updates stop once the job is settled.
 */
export function useJob(jobId: string | null) {
    const [job, setJob] = useState<JobSummary | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [mode, setMode] = useState<UpdateMode>("live");

    // Reset when the selected job changes.
    useEffect(() => {
        setJob(null);
        setError(null);
        setMode(typeof EventSource === "undefined" ? "polling" : "live");
    }, [jobId]);

    useEffect(() => {
        if (!jobId || mode !== "live") return;
        const source = new EventSource(api.eventsUrl(jobId));
        let finished = false;
        source.onmessage = (e) => {
            setJob(JSON.parse(e.data) as JobSummary);
            setError(null);
        };
        source.addEventListener("done", () => {
            finished = true;
            source.close();
        });
        source.onerror = () => {
            source.close();
            if (!finished) setMode("polling");
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

    return { job, setJob, error, setError, mode };
}
