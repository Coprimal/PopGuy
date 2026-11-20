import {
  ArrowRight,
  BookOpen,
  Cloud,
  Cpu,
  EyeOff,
  KeyRound,
  Languages,
  MousePointer2,
  ShieldCheck,
  SquareTerminal,
  TextSelect,
  UserX,
  Volume2,
  Zap
} from "lucide-react";
import { Link } from "react-router-dom";
import { Downloads } from "../components/Downloads";
import { HeroDemo } from "../components/demo/HeroDemo";
import {
  ComposerScene,
  CustomScene,
  EmojiScene,
  HistoryScene,
  HotkeyScene,
  ImproveScene,
  LookupScene,
  OcrScene,
  PlatformScene,
  SpeakScene,
  TranslateScene
} from "../components/demo/Scenes";
import { libraryPresets, libraryStats } from "../data/actionLibrary";

const steps = [
  {
    Icon: TextSelect,
    title: "Select",
    text: "Select text in any app, or capture text from the screen."
  },
  {
    Icon: MousePointer2,
    title: "Pick",
    text: "Pick an action from the toolbar that pops up."
  },
  {
    Icon: Zap,
    title: "Done",
    text: "Paste the result back, or copy it."
  }
];

// Bento grid: `size` is the column span on wide screens (of 6).
const features = [
  {
    size: 4,
    title: "Improve, with an inline diff",
    text: "Rewrite for clarity and see every change in green and red before it lands. Your text never changes until you accept.",
    Scene: ImproveScene
  },
  {
    size: 2,
    title: "Translate",
    text: "DeepL, Google Translate, or any AI model.",
    Scene: TranslateScene
  },
  {
    size: 2,
    title: "Custom actions",
    text: "Your own prompts, URLs, scripts, and Shortcuts.",
    Scene: CustomScene
  },
  {
    size: 2,
    title: "Hotkeys",
    text: "Run any action from the keyboard. Cmd+C+C improves the selection.",
    Scene: HotkeyScene
  },
  {
    size: 2,
    title: "Speech",
    text: "Hear the selected text, with system or cloud voices.",
    Scene: SpeakScene
  },
  {
    size: 3,
    title: "Screen text",
    text: "Grab text from anywhere on screen, even inside images.",
    Scene: OcrScene
  },
  {
    size: 3,
    title: "Dictionary",
    text: "Look up any word without leaving what you read.",
    Scene: LookupScene
  },
  {
    size: 4,
    title: "Composer",
    text: "Write long prompts for Claude Code, Codex, and Gemini CLI in a real editor window, then send them to the terminal.",
    Scene: ComposerScene
  },
  {
    size: 2,
    title: "Emoji picker",
    text: "Press Cmd+; (Ctrl+; on Windows) to search and insert emoji and symbols anywhere.",
    Scene: EmojiScene
  },
  {
    size: 3,
    title: "History",
    text: "Reuse recent results. Kept on your computer, and you can turn it off.",
    Scene: HistoryScene
  },
  {
    size: 3,
    title: "Mac and PC",
    text: "The same toolbar, actions, and providers on macOS and Windows. Actions move between them as JSON.",
    Scene: PlatformScene
  }
];

const providers = [
  {
    Icon: Cloud,
    title: "AI",
    items: ["OpenAI", "Anthropic", "Gemini", "OpenRouter", "GLM", "Any OpenAI-compatible API"]
  },
  {
    Icon: Cpu,
    title: "Local models",
    items: ["MLX on Mac", "llama.cpp on Windows", "Ollama", "LM Studio"],
    note: "Your text never leaves your computer."
  },
  {
    Icon: SquareTerminal,
    title: "CLI",
    items: ["Claude", "Codex", "Gemini CLI"],
    note: "Uses your subscription. No API key needed."
  },
  { Icon: Languages, title: "Translate", items: ["DeepL", "Google Translate"] },
  { Icon: Volume2, title: "Speech", items: ["System voices", "Google Cloud TTS", "Azure Speech"] },
  { Icon: BookOpen, title: "Dictionary", items: ["macOS Dictionary (Mac)", "Wiktionary", "and more"] }
];

const promises = [
  { Icon: UserX, text: "No account, no sign-up." },
  { Icon: EyeOff, text: "No analytics, telemetry, or tracking in the app." },
  { Icon: ShieldCheck, text: "Text leaves your computer only when you run an action, and only to the provider you picked." },
  { Icon: KeyRound, text: "API keys stay in the system keychain." }
];

function LibraryMarquee() {
  const half = Math.ceil(libraryPresets.length / 2);
  const rows = [libraryPresets.slice(0, half), libraryPresets.slice(half)];
  return (
    <div className="marquee scene" aria-hidden="true">
      {rows.map((row, rowIndex) => (
        <div key={rowIndex} className={`marquee__row ${rowIndex ? "marquee__row--reverse" : ""}`}>
          {[...row, ...row].map((preset, index) => {
            const Icon = preset.icon;
            return (
              <span key={`${preset.id}-${index}`} className="preset-chip">
                <Icon aria-hidden="true" />
                {preset.name}
              </span>
            );
          })}
        </div>
      ))}
    </div>
  );
}

export function HomePage() {
  return (
    <main className="home">
      <section className="hero" id="download" aria-labelledby="hero-title">
        <div className="hero__bg" aria-hidden="true" />
        <div className="container hero__grid">
          <div className="hero__copy">
            <p className="eyebrow">
              <span className="eyebrow__dot" />
              Free and open source · macOS &amp; Windows
            </p>
            <h1 id="hero-title">
              A guy pops up from your selected text with <span className="magic">magic</span>.
            </h1>
            <p className="lead">
              Select text in any app on your Mac or PC, pick an action, and get the result right there.
            </p>
            <Downloads />
          </div>
          <div className="hero__visual">
            <img className="hero__mascot" src="/mascot.png" alt="" width="160" height="160" />
            <span className="sparkle sparkle--1" aria-hidden="true" />
            <span className="sparkle sparkle--2" aria-hidden="true" />
            <span className="sparkle sparkle--3" aria-hidden="true" />
            <HeroDemo />
          </div>
        </div>
      </section>

      <section className="section container" aria-labelledby="how-title">
        <div className="section-head reveal">
          <p className="kicker">How it works</p>
          <h2 id="how-title">Three steps, zero app switching</h2>
        </div>
        <ol className="flow scene">
          {steps.map(({ Icon, title, text }, index) => (
            <li key={title} className="flow__step reveal" style={{ "--d": index }}>
              <span className="flow__icon"><Icon aria-hidden="true" /></span>
              <span className="flow__num">{index + 1}</span>
              <h3>{title}</h3>
              <p>{text}</p>
            </li>
          ))}
        </ol>
      </section>

      <section className="section container" id="features" aria-labelledby="features-title">
        <div className="section-head reveal">
          <p className="kicker">Features</p>
          <h2 id="features-title">Everything happens where your text is</h2>
          <p>
            One small toolbar, many tricks. Use the built-in actions or <Link to="/actions">make your own</Link>.
          </p>
        </div>
        <div className="bento">
          {features.map(({ size, title, text, Scene }) => (
            <article key={title} className={`bento__card bento__card--${size} reveal`}>
              <div className="bento__visual">
                <Scene />
              </div>
              <h3>{title}</h3>
              <p>{text}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="section container" aria-labelledby="providers-title">
        <div className="section-head reveal">
          <p className="kicker">Providers</p>
          <h2 id="providers-title">Bring the brain you like</h2>
          <p>Every action picks its own provider and model. Mix cloud, local, and your CLI subscription.</p>
        </div>
        <div className="signal scene reveal" aria-hidden="true">
          <span className="signal__node">Your text</span>
          <span className="signal__wire"><i /></span>
          <span className="signal__node signal__node--accent">The provider you pick</span>
          <span className="signal__wire"><i /></span>
          <span className="signal__node">Result, in place</span>
        </div>
        <div className="providers">
          {providers.map(({ Icon, title, items, note }) => (
            <div key={title} className="provider-card reveal">
              <h3><Icon aria-hidden="true" />{title}</h3>
              <ul>
                {items.map((item) => <li key={item}>{item}</li>)}
              </ul>
              {note ? <p className="provider-card__note">{note}</p> : null}
            </div>
          ))}
        </div>
      </section>

      <section className="section container" aria-labelledby="privacy-title">
        <div className="privacy-band reveal">
          <div className="privacy-band__copy">
            <p className="kicker kicker--light">Privacy</p>
            <h2 id="privacy-title">Your text stays yours</h2>
            <ul>
              {promises.map(({ Icon, text }) => (
                <li key={text}><Icon aria-hidden="true" />{text}</li>
              ))}
            </ul>
            <Link className="text-link text-link--light" to="/privacy">
              Read the privacy policy <ArrowRight aria-hidden="true" />
            </Link>
          </div>
          <div className="privacy-band__art" aria-hidden="true">
            <div className="vault scene">
              <span className="vault__ring vault__ring--1" />
              <span className="vault__ring vault__ring--2" />
              <img src="/mascot.png" alt="" />
              <span className="vault__lock"><ShieldCheck /></span>
            </div>
          </div>
        </div>
      </section>

      <section className="section" aria-labelledby="library-title">
        <div className="container section-head reveal">
          <p className="kicker">Action library</p>
          <h2 id="library-title">{libraryStats.total} ready-made actions, one click away</h2>
          <p>
            Search engines, maps, AI chats, text tools, developer helpers, and your favorite apps. Install them from the
            app, or import PopClip extensions.
          </p>
        </div>
        <LibraryMarquee />
        <div className="container center">
          <Link className="text-link" to="/actions">
            Browse all actions <ArrowRight aria-hidden="true" />
          </Link>
        </div>
      </section>

      <section className="section container" aria-labelledby="video-title">
        <div className="section-head reveal">
          <p className="kicker">Demo</p>
          <h2 id="video-title">See it in action</h2>
        </div>
        <div className="video-frame reveal">
          <iframe
            src="https://www.youtube-nocookie.com/embed/-ctYYSC4bWM?rel=0"
            title="PopGuy Demo"
            loading="lazy"
            allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share"
            referrerPolicy="strict-origin-when-cross-origin"
            allowFullScreen
          />
        </div>
      </section>

      <section className="section container" aria-labelledby="cta-title">
        <div className="cta scene reveal">
          <img className="cta__mascot" src="/mascot.png" alt="" width="120" height="120" />
          <h2 id="cta-title">Select something. He'll pop up.</h2>
          <p>Free, open source, and no account needed.</p>
          <Downloads tone="inverse" />
        </div>
      </section>
    </main>
  );
}
