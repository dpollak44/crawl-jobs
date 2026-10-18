import type { ReactNode } from "react";

/** The one red paragraph for anything that went wrong: same look, same role, everywhere. */
export function ErrorText({ children }: { children: ReactNode }) {
    return (
        <p className="error" role="alert">
            {children}
        </p>
    );
}
