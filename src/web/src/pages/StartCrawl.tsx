import { FormEvent, useState } from "react";
import { useNavigate } from "react-router-dom";
import { api, ApiError } from "../api";

export function StartCrawl() {
    const navigate = useNavigate();
    const [url, setUrl] = useState("");
    const [maxDepth, setMaxDepth] = useState("");
    const [submitting, setSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    async function onSubmit(e: FormEvent) {
        e.preventDefault();
        setError(null);
        setSubmitting(true);
        try {
            const { jobId } = await api.createJob(
                url.trim(),
                maxDepth === "" ? undefined : Number(maxDepth),
            );
            navigate(`/jobs/${jobId}`);
        } catch (err) {
            setError(
                err instanceof ApiError
                    ? err.message
                    : "Something went wrong. Try again.",
            );
        } finally {
            setSubmitting(false);
        }
    }

    return (
        <section>
            <h2>Start a crawl</h2>
            <form onSubmit={onSubmit} className="stack">
                <label>
                    Website address
                    <input
                        type="url"
                        required
                        placeholder="https://example.com"
                        value={url}
                        onChange={(e) => setUrl(e.target.value)}
                        disabled={submitting}
                    />
                </label>
                <label>
                    Depth (optional, default 2)
                    <input
                        type="number"
                        min={0}
                        max={5}
                        value={maxDepth}
                        onChange={(e) => setMaxDepth(e.target.value)}
                        disabled={submitting}
                    />
                </label>
                <button
                    type="submit"
                    disabled={submitting || url.trim() === ""}
                >
                    {submitting ? "Starting…" : "Start Crawl"}
                </button>
                {error && (
                    <p className="error" role="alert">
                        {error}
                    </p>
                )}
            </form>
        </section>
    );
}
