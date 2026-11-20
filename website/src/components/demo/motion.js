import { useEffect, useState } from "react";

/** True when the visitor asked the system for less motion. False where matchMedia is missing (tests). */
export function prefersReducedMotion() {
  try {
    return Boolean(window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches);
  } catch {
    return false;
  }
}

/** Whether the element is on screen. True where IntersectionObserver is missing, so content never stays hidden. */
export function useInView(ref, rootMargin = "0px") {
  const [inView, setInView] = useState(typeof IntersectionObserver === "undefined");

  useEffect(() => {
    const node = ref.current;
    if (!node || typeof IntersectionObserver === "undefined") return undefined;
    const observer = new IntersectionObserver(([entry]) => setInView(entry.isIntersecting), { rootMargin });
    observer.observe(node);
    return () => observer.disconnect();
  }, [ref, rootMargin]);

  return inView;
}

/**
 * Scroll reveal for every `.reveal` element on the page, and play/pause for every `.scene` (its CSS loops run only
 * while it is on screen). Without IntersectionObserver, or with reduced motion, everything shows at once.
 */
export function useScrollEffects(key) {
  useEffect(() => {
    const root = document.documentElement;
    const reveals = [...document.querySelectorAll(".reveal")];
    const scenes = [...document.querySelectorAll(".scene")];
    if (typeof IntersectionObserver === "undefined" || prefersReducedMotion()) {
      reveals.forEach((node) => node.classList.add("is-in"));
      scenes.forEach((node) => node.classList.add("is-playing"));
      return undefined;
    }
    root.classList.add("can-reveal");

    const revealObserver = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          entry.target.classList.add("is-in");
          revealObserver.unobserve(entry.target);
        }
      },
      { rootMargin: "0px 0px -8% 0px" }
    );
    const sceneObserver = new IntersectionObserver((entries) => {
      for (const entry of entries) entry.target.classList.toggle("is-playing", entry.isIntersecting);
    });
    reveals.forEach((node) => revealObserver.observe(node));
    scenes.forEach((node) => sceneObserver.observe(node));
    return () => {
      revealObserver.disconnect();
      sceneObserver.disconnect();
    };
  }, [key]);
}
