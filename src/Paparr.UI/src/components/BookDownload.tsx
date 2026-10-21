import { useEffect, useRef, useState } from "react";
import { downloadBook } from "../api";

export default function BookDownload({
  id,
  title,
}: {
  id: number;
  title: string;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const active = useRef<AbortController | null>(null);
  useEffect(() => () => active.current?.abort(), []);

  async function download() {
    if (active.current) return;
    const controller = new AbortController();
    active.current = controller;
    setBusy(true);
    setError("");
    try {
      await downloadBook(id, controller.signal);
    } catch (reason) {
      if (!controller.signal.aborted)
        setError(
          reason instanceof Error
            ? reason.message
            : "The book could not be downloaded.",
        );
    } finally {
      active.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  return (
    <>
      <button
        className="text-button library-download"
        aria-label={`Download ${title}`}
        disabled={busy}
        onClick={() => void download()}
      >
        {busy ? "Verifying download…" : "Download"}
      </button>
      {error && (
        <small className="library-message" role="alert">
          {error}
        </small>
      )}
    </>
  );
}
