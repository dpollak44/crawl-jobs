import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter, Link, Route, Routes } from "react-router-dom";
import { JobsPage } from "./pages/JobsPage";
import { JobPage } from "./pages/JobPage";
import "./styles.css";

function App() {
    return (
        <BrowserRouter>
            <header className="topbar">
                <Link className="brand" to="/">
                    Crawl Jobs
                </Link>
                <span className="muted small">
                    Submit a site, watch it crawl, see where its links go.
                </span>
            </header>
            <Routes>
                <Route path="/" element={<JobsPage />} />
                <Route path="/history" element={<JobsPage />} />
                <Route path="/jobs/:jobId" element={<JobPage />} />
            </Routes>
        </BrowserRouter>
    );
}

ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
        <App />
    </React.StrictMode>,
);
