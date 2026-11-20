import {
  BookA,
  Languages,
  ListMinus,
  MessageCircleCheck,
  Settings,
  SquarePen,
  Volume2,
  WandSparkles
} from "lucide-react";
import { forwardRef } from "react";

// The app's default toolbar (macos ToolbarView): icon-only buttons in the default order, a divider between
// actions, a language pill after Translate and Look up, and the Settings gear at the end.
export const TOOLBAR_ACTIONS = [
  { key: "improve", label: "Improve", Icon: WandSparkles },
  { key: "shorten", label: "Shorten", Icon: ListMinus },
  { key: "proofread", label: "Proofread", Icon: MessageCircleCheck },
  { key: "prompt", label: "Prompt", Icon: SquarePen },
  { key: "translate", label: "Translate", Icon: Languages, pill: "fr" },
  { key: "lookup", label: "Look up", Icon: BookA, pill: "en" },
  { key: "speak", label: "Speak", Icon: Volume2 }
];

/** A drawing of PopGuy's floating toolbar, sized in em (1em ≈ 13 pt in the app). */
export const Toolbar = forwardRef(function Toolbar(
  { actions = TOOLBAR_ACTIONS, hover = null, running = null, buttonRefs, gear = true, stretch = false },
  ref
) {
  return (
    <div className={`tb ${stretch ? "tb--stretch" : ""}`} ref={ref}>
      <span className="tb__logo">
        <img src="/mascot.png" alt="" />
      </span>
      {actions.map(({ key, Icon, pill }, index) => (
        <span key={key} className="tb__group">
          {index > 0 ? <span className="tb__sep" /> : null}
          <span
            ref={(node) => {
              if (buttonRefs) buttonRefs.current[key] = node;
            }}
            className={`tb__btn ${hover === key ? "is-hover" : ""}`}
          >
            {running === key ? <span className="mac-spinner" /> : <Icon aria-hidden="true" strokeWidth={1.6} />}
          </span>
          {pill ? <span className="tb__pill">{pill}</span> : null}
        </span>
      ))}
      {gear ? (
        <>
          <span className="tb__spacer" />
          <span className="tb__btn">
            <Settings aria-hidden="true" strokeWidth={1.6} />
          </span>
        </>
      ) : null}
    </div>
  );
});
