import {
  ArrowRight,
  Copy,
  RotateCw,
  Undo2,
  X,
  Braces,
  ClipboardPaste,
  Link2,
  MessageSquareText,
  Play,
  Plus,
  ScanText,
  Search,
  Smile,
  Sparkles,
  SquareTerminal,
  Workflow
} from "lucide-react";
import { TOOLBAR_ACTIONS, Toolbar } from "./Toolbar";

const SHORT_TOOLBAR = TOOLBAR_ACTIONS.filter(({ key }) => ["improve", "translate", "speak"].includes(key));

// Small looping drawings for the feature cards. Every one is decorative (aria-hidden): the card text says it all.
// Their CSS loops run only while on screen (.scene.is-playing, see useScrollEffects).

export function ImproveScene() {
  return (
    <div className="scene scene-improve mac-card" aria-hidden="true">
      <span className="mac-segmented">
        <span className="is-on">Diff</span>
        <span>Text</span>
      </span>
      <p className="scene-improve__text">
        <span className="d-del">Their</span><span className="d-add">They're</span> going to love the new{" "}
        <span className="d-del">desing</span><span className="d-add">design</span>, it feels{" "}
        <span className="d-del">more faster</span><span className="d-add">faster</span> everywhere.
      </p>
      <div className="mac-foot">
        <span className="mac-btn mac-btn--icon"><Copy aria-hidden="true" /></span>
        <span className="mac-btn mac-btn--primary"><Undo2 aria-hidden="true" />Paste back</span>
        <span className="mac-btn mac-btn--icon"><RotateCw aria-hidden="true" /></span>
        <span className="mac-foot__spacer" />
        <span className="mac-btn"><X aria-hidden="true" />Cancel</span>
      </div>
    </div>
  );
}

const translations = [
  ["FR", "Les bons outils savent se faire oublier."],
  ["JA", "良い道具は、邪魔をしない。"],
  ["VI", "Công cụ tốt không làm phiền bạn."],
  ["ES", "Las buenas herramientas no estorban."],
  ["DE", "Gute Werkzeuge stehen nicht im Weg."]
];

export function TranslateScene() {
  return (
    <div className="scene scene-translate" aria-hidden="true">
      <p className="scene-translate__src"><span className="tag">EN</span>Good tools stay out of the way.</p>
      <div className="scene-translate__ticker">
        <ul>
          {[...translations, translations[0]].map(([lang, text], index) => (
            <li key={index}><span className="tag tag--accent">{lang}</span>{text}</li>
          ))}
        </ul>
      </div>
      <p className="scene-translate__by">DeepL · Google Translate · any AI model</p>
    </div>
  );
}

const custom = [
  { key: "summarize", label: "Summarize", Icon: MessageSquareText },
  { key: "search", label: "Search", Icon: Search },
  { key: "emojify", label: "Emojify", Icon: Smile },
  { key: "base64", label: "Base64", Icon: Braces }
];

export function CustomScene() {
  return (
    <div className="scene scene-custom" aria-hidden="true">
      <div className="scene-custom__bar">
        <span className="tb__logo"><img src="/mascot.png" alt="" /></span>
        {custom.map(({ key, label, Icon }, index) => (
          <span key={key} className="pop-chip" style={{ "--i": index }}>
            <Icon aria-hidden="true" />{label}
          </span>
        ))}
        <span className="pop-chip pop-chip--add"><Plus aria-hidden="true" /></span>
      </div>
      <div className="scene-custom__types">
        <span><Sparkles aria-hidden="true" />Prompt</span>
        <span><Link2 aria-hidden="true" />URL</span>
        <span><SquareTerminal aria-hidden="true" />Shell</span>
        <span><Braces aria-hidden="true" />AppleScript</span>
        <span><Workflow aria-hidden="true" />Shortcut</span>
      </div>
    </div>
  );
}

export function HotkeyScene() {
  return (
    <div className="scene scene-hotkey" aria-hidden="true">
      <div className="keys">
        <kbd className="key key--wide">⌘</kbd>
        <kbd className="key key--c1">C</kbd>
        <kbd className="key key--c2">C</kbd>
      </div>
      <div className="scene-hotkey__result">
        <ArrowRight aria-hidden="true" />
        <span className="pop-chip"><Sparkles aria-hidden="true" />Improve</span>
      </div>
    </div>
  );
}

export function SpeakScene() {
  return (
    <div className="scene scene-speak" aria-hidden="true">
      <span className="play"><Play aria-hidden="true" /></span>
      <div className="wave">
        {Array.from({ length: 28 }, (_, index) => (
          <i key={index} style={{ "--i": index, "--h": 0.25 + ((index * 37) % 11) / 14 }} />
        ))}
      </div>
    </div>
  );
}

export function LookupScene() {
  return (
    <div className="scene scene-lookup" aria-hidden="true">
      <p className="scene-lookup__word">serendipity</p>
      <p className="scene-lookup__ipa">/ˌsɛr.ənˈdɪp.ɪ.ti/ · noun</p>
      <p className="scene-lookup__def">Finding something good without looking for it.</p>
    </div>
  );
}

export function OcrScene() {
  return (
    <div className="scene scene-ocr" aria-hidden="true">
      <div className="scene-ocr__shot">
        <div className="fake-img" />
        <div className="fake-lines"><i /><i /><i /></div>
        <div className="ocr-frame"><span className="ocr-scan" /></div>
      </div>
      <span className="pop-chip scene-ocr__chip"><ScanText aria-hidden="true" />3 lines captured</span>
    </div>
  );
}

export function ComposerScene() {
  return (
    <div className="scene scene-composer" aria-hidden="true">
      <div className="term">
        <p><span className="term__dim">~/app</span> claude</p>
        <p className="term__dim">╭─ Ready when you are.</p>
        <p>&gt; <span className="term__caret" /></p>
      </div>
      <div className="composer-card">
        <p className="composer-card__title">Composer</p>
        <p className="type-line" style={{ "--i": 0 }}>Refactor the settings store:</p>
        <p className="type-line" style={{ "--i": 1 }}>  - split prefs from keys</p>
        <p className="type-line" style={{ "--i": 2 }}>  - add tests for migration</p>
        <span className="composer-card__send"><ClipboardPaste aria-hidden="true" />Send</span>
      </div>
    </div>
  );
}

const emoji = ["🎉", "🥳", "🎊", "✨", "🙌", "🚀", "💡", "🔥", "👏", "😄"];

export function EmojiScene() {
  return (
    <div className="scene scene-emoji" aria-hidden="true">
      <div className="emoji-search"><kbd className="key key--sm">⌘</kbd><kbd className="key key--sm">;</kbd><span>party</span></div>
      <div className="emoji-grid">
        {emoji.map((char) => <span key={char}>{char}</span>)}
        <i className="emoji-ring" />
      </div>
    </div>
  );
}

const recent = [
  ["Improve", "We were planning to ship tomorrow…"],
  ["Translate", "Les bons outils savent se faire…"],
  ["Summarize", "Three takeaways from the call…"],
  ["Proofread", "Thanks for your patience, team."]
];
// The first three again at the end, so the ticker's last frame matches its first.
const history = [...recent, ...recent.slice(0, 3)];

export function HistoryScene() {
  return (
    <div className="scene scene-history" aria-hidden="true">
      <ul>
        {history.map(([action, text], index) => (
          <li key={index}><span className="tag tag--accent">{action}</span><span>{text}</span></li>
        ))}
      </ul>
    </div>
  );
}

export function PlatformScene() {
  return (
    <div className="scene scene-platform" aria-hidden="true">
      <div className="os-card">
        <span className="os-card__dots"><i /><i /><i /></span>
        <Toolbar actions={SHORT_TOOLBAR} />
        <span className="os-card__label">macOS 13+</span>
      </div>
      <div className="os-card os-card--win">
        <span className="os-card__winbar"><i /><i /><i /></span>
        <Toolbar actions={SHORT_TOOLBAR} />
        <span className="os-card__label">Windows 11</span>
      </div>
    </div>
  );
}
