import { useCallback, useRef, useState } from "react";

/**
 * aria-live announcer. Clears then re-sets the message on a short delay so
 * repeated identical announcements ("Availability block deleted") are still
 * spoken by screen readers.
 */
export function useAnnouncer(): [string, (msg: string) => void] {
  const [msg, setMsg] = useState("");
  const timer = useRef<number | undefined>(undefined);

  const announce = useCallback((m: string) => {
    setMsg("");
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setMsg(m), 50);
  }, []);

  return [msg, announce];
}

export function LiveRegion({ message }: { message: string }) {
  return (
    <div role="status" aria-live="polite" className="visually-hidden">
      {message}
    </div>
  );
}
