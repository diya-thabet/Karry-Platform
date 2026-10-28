import { useEffect, useRef } from "react";

/** How often the idle time is checked: a timer would fire late after the tablet's screen sleeps, the clock does not. */
const checkEveryMs = 15_000;

/**
 * Locks the tablet after this many minutes without a touch or a key (US-P1-OFF-04 AC3), also when it comes back from
 * sleep past that time.
 */
export function useAutoLock(minutes: number, lock: () => void) {
  // Set when the effect starts: the home screen just opened.
  const last = useRef(0);
  useEffect(() => {
    last.current = Date.now();
    const touched = () => { last.current = Date.now(); };
    const check = () => {
      if (Date.now() - last.current >= minutes * 60_000) {
        lock();
      }
    };
    const events = ["pointerdown", "keydown", "wheel"] as const;
    for (const name of events) {
      window.addEventListener(name, touched, { passive: true });
    }

    document.addEventListener("visibilitychange", check);
    const timer = setInterval(check, checkEveryMs);
    return () => {
      for (const name of events) {
        window.removeEventListener(name, touched);
      }

      document.removeEventListener("visibilitychange", check);
      clearInterval(timer);
    };
  }, [minutes, lock]);
}
