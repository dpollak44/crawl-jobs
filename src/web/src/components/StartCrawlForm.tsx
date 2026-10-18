import { type FormEvent, useState } from "react";
import { api, errorMessage } from "../api/client";

interface Props {
    onStarted: (jobId: string) => void;
    onClose: () => void;
}

export function StartCrawlForm({ onStarted, onClose }: Props) {
    const [url, setUrl] = useState("");
    const [maxDepth, setMaxDepth] = useState("2");
    const [submitting, setSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    async function onSubmit(e: FormEvent) {
        e.preventDefault();
        setError(null);
        setSubmitting(true);
        try {
            const { jobId } = await api.createJob(
                url.trim(),
                maxDepth === "" ? undefined : Math.round(Number(maxDepth)),
            );
            onStarted(jobId);
        } catch (err) {
            setError(errorMessage(err, "Something went wrong. Try again."));
        } finally {
            setSubmitting(false);
        }
    }

    return (
        <form
            className="card start-form"
            onSubmit={onSubmit}
            aria-label="Start a crawl"
        >
            <div className="form-row">
                <label className="field grow">
                    <span>Website address</span>
                    <input
                        type="text"
                        required
                        autoFocus
                        placeholder="example.com"
                        value={url}
                        onChange={(e) => setUrl(e.target.value)}
                        disabled={submitting}
                    />
                </label>
                <label className="field depth">
                    <span>Depth</span>
                    <input type="number" min={0} max={5} step={1}
                        value={maxDepth}
                        onChange={(e) => setMaxDepth(e.target.value)}
                        disabled={submitting}
                    />
                </label>
            </div>
            <div className="form-actions">
                <button
                    type="submit"
                    className="primary"
                    disabled={submitting || url.trim() === ""}
                >
                    {submitting ? "Starting…" : "Start crawl"}
                </button>
                <button
                    type="button"
                    className="ghost"
                    onClick={onClose}
                    disabled={submitting}
                >
                    Cancel
                </button>
                <span className="hint">
                    Depth 0 is the start page only; 2 follows links two levels
                    down. Up to 200 pages.
                </span>
            </div>
            {error && (
                <p className="error" role="alert">
                    {error}
                </p>
            )}
        </form>
    );
}
