import { useState } from "react";
import type { TreeNode } from "../api/types";
import { StatusBadge } from "./StatusBadge";
import { ratio } from "../lib/format";

/** The discovered pages as a tree: every page under the page it was first found on, with its Domain Link Ratio. */
export function PageTree({ root, host }: { root: TreeNode; host: string }) {
    return (
        <section className="tree-section">
            <h3>Pages</h3>
            <p className="muted small">
                Each page shows its Domain Link Ratio: the share of its outgoing
                links that stay on {host}.
            </p>
            <ul className="tree">
                <Node node={root} />
            </ul>
        </section>
    );
}

function Node({ node }: { node: TreeNode }) {
    const [open, setOpen] = useState(true);
    const hasChildren = node.children.length > 0;
    return (
        <li>
            <div className="node">
                {hasChildren ? (
                    <button className="twisty" onClick={() => setOpen((o) => !o)} aria-label={open ? "Collapse" : "Expand"} aria-expanded={open}>
                        {open ? "▾" : "▸"}
                    </button>
                ) : (
                    <span className="twisty-space" />
                )}
                <StatusBadge status={node.status} />
                <a
                    className="node-url"
                    href={node.url}
                    target="_blank"
                    rel="noreferrer"
                >
                    {node.url}
                </a>
                {node.status === "Fetched" && (
                    <span
                        className="ratio"
                        title={`${node.internalLinks} of ${node.outgoingLinks} links stay on the site`}
                    >
                        <span className="ratio-bar">
                            <span
                                style={{
                                    width: `${Math.round(node.domainLinkRatio * 100)}%`,
                                }}
                            />
                        </span>
                        {ratio(node.domainLinkRatio)}{" "}
                        <span className="muted small">
                            ({node.internalLinks}/{node.outgoingLinks})
                        </span>
                    </span>
                )}
                {node.error && (
                    <span className="muted small note">{node.error}</span>
                )}
                {hasChildren && !open && (
                    <span className="muted small">
                        {node.children.length} below
                    </span>
                )}
            </div>
            {hasChildren && open && (
                <ul>
                    {node.children.map((c) => (
                        <Node key={c.pageId} node={c} />
                    ))}
                </ul>
            )}
        </li>
    );
}
