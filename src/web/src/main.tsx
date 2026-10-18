import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter, Link, Route, Routes } from "react-router-dom";
import { StartCrawl } from "./pages/StartCrawl";
import { JobDetails } from "./pages/JobDetails";
import { History } from "./pages/History";
import "./styles.css";

function App() {
    return (
        <BrowserRouter>
            <header>
                <h1>Crawl Jobs</h1>
                <nav>
                    <Link to="/">Start crawl</Link>
                    <Link to="/history">History</Link>
                </nav>
            </header>
            <main>
                <Routes>
                    <Route path="/" element={<StartCrawl />} />
                    <Route path="/jobs/:jobId" element={<JobDetails />} />
                    <Route path="/history" element={<History />} />
                </Routes>
            </main>
        </BrowserRouter>
    );
}

ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
        <App />
    </React.StrictMode>,
);
