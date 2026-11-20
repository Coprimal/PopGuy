import { Copy, MessageSquareText, Pencil, RotateCw, Square, Undo2, X } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { Toolbar } from "./Toolbar";
import { prefersReducedMotion, useInView } from "./motion";

// Two scenes play in turn: Improve (with its inline diff), then Translate.
const SCENES = [
  {
    action: "improve",
    file: "launch-update.txt",
    heading: "Launch update",
    lead: "Hi team, quick news on the release: ",
    selected: "we was planning to ship tomorrow, but its delayed untill Friday.",
    tail: " Thanks for bearing with us!",
    footnote: "Next steps: final QA on Thursday, release notes on Friday morning.",
    diff: [
      ["del", "we was"],
      ["add", "We were"],
      ["eq", " planning to ship tomorrow, but "],
      ["del", "its"],
      ["add", "it's"],
      ["eq", " delayed "],
      ["del", "untill"],
      ["add", "until"],
      ["eq", " Friday."]
    ],
    after: "We were planning to ship tomorrow, but it's delayed until Friday."
  },
  {
    action: "translate",
    file: "note-to-self.txt",
    heading: "Note to self",
    lead: "Pinned above my desk: ",
    selected: "Good tools stay out of the way.",
    tail: " A reminder for every app I build, and every email I write.",
    footnote: "Also: ship smaller changes, and drink more water.",
    after: "Les bons outils savent se faire oublier."
  }
];

// Phase start times in a scene (ms). "end" moves on to the next scene.
const TIMELINE = [
  [0, "rest"],
  [700, "select"],
  [1800, "toolbar"],
  [2500, "hover"],
  [2900, "working"],
  [4000, "result"],
  [6500, "hoverPaste"],
  [6900, "pasted"],
  [9400, "end"]
];
const ORDER = TIMELINE.map(([, phase]) => phase);

function ResultFooter({ pasteRef, pasteHover }) {
  return (
    <div className="mac-foot">
      <span className="mac-btn mac-btn--icon"><Copy aria-hidden="true" /></span>
      <span ref={pasteRef} className={`mac-btn mac-btn--primary ${pasteHover ? "is-hover" : ""}`}>
        <Undo2 aria-hidden="true" />Paste back
      </span>
      <span className="mac-btn mac-btn--icon"><Pencil aria-hidden="true" /></span>
      <span className="mac-btn mac-btn--icon"><RotateCw aria-hidden="true" /></span>
      <span className="mac-btn mac-btn--icon"><MessageSquareText aria-hidden="true" /></span>
      <span className="mac-foot__spacer" />
      <span className="mac-btn"><X aria-hidden="true" />Cancel</span>
    </div>
  );
}

/**
 * The hero's looping demo, drawn after the real app: a note in a text editor, a selection sweeping over a sentence,
 * PopGuy's toolbar appearing under the pointer, the action running (spinner and Stop), its result (an inline diff
 * for Improve), and the result pasted back in place. Decorative: screen readers skip it.
 */
export function HeroDemo() {
  const stageRef = useRef(null);
  const selectionRef = useRef(null);
  const panelRef = useRef(null);
  const pasteRef = useRef(null);
  const buttonRefs = useRef({});
  const inView = useInView(stageRef);
  const [reduced] = useState(prefersReducedMotion);
  const [sceneIndex, setSceneIndex] = useState(0);
  const [phase, setPhase] = useState(reduced ? "result" : "rest");
  const [pointer, setPointer] = useState(null);
  const [panelPos, setPanelPos] = useState(null);

  const scene = SCENES[sceneIndex];
  const reached = (name) => ORDER.indexOf(phase) >= ORDER.indexOf(name);
  const running = inView && !reduced;

  useEffect(() => {
    if (!running) return undefined;
    setPhase("rest");
    const timers = TIMELINE.map(([time, name]) =>
      setTimeout(() => {
        // Both in one render, or the next scene shows for a frame with this one's paste highlight.
        if (name === "end") {
          setPhase("rest");
          setSceneIndex((index) => (index + 1) % SCENES.length);
        } else setPhase(name);
      }, time)
    );
    return () => timers.forEach(clearTimeout);
  }, [running, sceneIndex]);

  useLayoutEffect(() => {
    const stage = stageRef.current;
    if (!stage) return;
    const box = stage.getBoundingClientRect();
    const rects = selectionRef.current?.getClientRects?.() ?? [];
    const first = rects[0];
    const last = rects[rects.length - 1];

    // Like the app, the toolbar shows just under where the drag ended, kept inside the window.
    if ((phase === "toolbar" || (reduced && phase === "result")) && last && panelRef.current) {
      const width = panelRef.current.offsetWidth;
      const end = last.right - box.left;
      const left = Math.max(10, Math.min(end - width * 0.35, box.width - width - 10));
      setPanelPos({ left, top: last.bottom - box.top + 8 });
    }
    if (reduced) return;

    // The pointer follows the story: the start of the sentence, its end, the action button, then Paste back.
    const point = (rect, fx, fy) =>
      rect ? { x: rect.left - box.left + rect.width * fx, y: rect.top - box.top + rect.height * fy } : null;
    let next = null;
    if (phase === "rest") next = point(first, 0, 0.6);
    else if (phase === "select" || phase === "toolbar") next = point(last, 1, 0.6);
    else if (phase === "hover" || phase === "working" || phase === "result")
      next = point(buttonRefs.current[scene.action]?.getBoundingClientRect(), 0.55, 0.6);
    else if (phase === "hoverPaste") next = point(pasteRef.current?.getBoundingClientRect(), 0.5, 0.6);
    else if (phase === "pasted") next = { x: box.width * 0.86, y: box.height * 0.9 };
    if (next) setPointer(next);
  }, [phase, sceneIndex, reduced, scene.action]);

  const showPanel = reached("toolbar") && !reached("pasted") && panelPos;
  const pasted = reached("pasted");

  return (
    <div className="hero-demo scene" ref={stageRef} aria-hidden="true">
      <div className="hero-demo__inner">
        <div className="mock-window">
          <div className="mock-window__bar">
            <span className="dots"><i /><i /><i /></span>
            <span className="mock-window__title">{scene.file}</span>
          </div>
          <div className="mock-doc">
            <p className="mock-doc__heading">{scene.heading}</p>
            <p className="mock-doc__text">
              {scene.lead}
              {pasted ? (
                <span className="pasted" key={`pasted-${sceneIndex}`}>{scene.after}</span>
              ) : (
                <span ref={selectionRef} className={`sel ${reached("select") ? "is-on" : ""}`}>{scene.selected}</span>
              )}
              {scene.tail}
            </p>
            <p className="mock-doc__text mock-doc__text--faint">{scene.footnote}</p>
          </div>
        </div>

        <div
          className={`float-panel ${showPanel ? "is-open" : ""}`}
          ref={panelRef}
          style={panelPos ? { left: panelPos.left, top: panelPos.top } : undefined}
        >
          <Toolbar
            hover={reached("hover") && !reached("result") ? scene.action : null}
            running={phase === "working" ? scene.action : null}
            buttonRefs={buttonRefs}
          />
          <div className={`float-panel__result ${reached("working") ? "is-open" : ""}`}>
            <div>
              {phase === "working" ? (
                <div className="mac-running">
                  <span className="mac-spinner" />
                  <span className="mac-btn"><Square aria-hidden="true" className="filled" />Stop</span>
                </div>
              ) : (
                <div className="mac-result" key={`result-${sceneIndex}`}>
                  {scene.diff ? (
                    <>
                      <span className="mac-segmented">
                        <span className="is-on">Diff</span>
                        <span>Text</span>
                      </span>
                      <p className="mac-result__body">
                        {scene.diff.map(([kind, text], index) => (
                          <span key={index} className={`mdiff-${kind}`}>{text}</span>
                        ))}
                      </p>
                    </>
                  ) : (
                    <p className="mac-result__body">{scene.after}</p>
                  )}
                  <ResultFooter pasteRef={pasteRef} pasteHover={phase === "hoverPaste"} />
                </div>
              )}
            </div>
          </div>
        </div>

        {pointer ? (
          <svg
            className={`pointer ${phase === "hover" || phase === "hoverPaste" ? "is-clicking" : ""}`}
            style={{ transform: `translate(${pointer.x}px, ${pointer.y}px)` }}
            viewBox="0 0 24 24"
            width="20"
            height="20"
          >
            <path d="M5 3.2v16.1l4.3-4.1 2.7 6.1 3-1.3-2.7-6h6z" />
          </svg>
        ) : null}
      </div>
    </div>
  );
}
