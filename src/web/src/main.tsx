import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import { Dashboard } from "./pages/Dashboard";
import "./styles.css";

function App() {
    return (
        <BrowserRouter>
            <header className="topbar">
                <span className="brand">Crawl Jobs</span>
                <span className="muted small">
                    Submit a site, watch it crawl, see where its links go.
                </span>
            </header>
            <Routes>
                <Route path="/" element={<Dashboard />} />
                <Route path="/jobs/:jobId" element={<Dashboard />} />
                <Route path="/history" element={<Dashboard />} />
            </Routes>
        </BrowserRouter>
    );
}

ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
        <App />
    </React.StrictMode>,
);
