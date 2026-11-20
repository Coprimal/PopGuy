import {
  BookA,
  BookOpen,
  Braces,
  FileJson,
  Languages,
  Link2,
  ListMinus,
  MessageCircleCheck,
  Settings,
  Sparkles,
  SquarePen,
  SquareTerminal,
  Volume2,
  WandSparkles,
  Workflow
} from "lucide-react";
import { useMemo, useState } from "react";
import { PageHero } from "../components/PageHero";
import { RichText } from "../components/RichText";
import { libraryCategories, libraryPresets, libraryStats } from "../data/actionLibrary";

const builtIn = [
  ["Improve", "rewrite for clarity.", WandSparkles],
  ["Shorten", "cut the fluff, keep the meaning.", ListMinus],
  ["Proofread", "fix grammar and spelling.", MessageCircleCheck],
  ["Translate", "any language, any provider.", Languages],
  ["Prompt", "ask anything with your own prompt.", SquarePen],
  ["Speak", "read the text aloud.", Volume2],
  ["Look up", "look up the word.", BookA]
];

const actionTypes = [
  ["AI", "send text to any AI model with your prompt.", Sparkles],
  ["Translate", "DeepL, Google Translate, or any AI model.", Languages],
  ["Speech", "system or cloud voices.", Volume2],
  ["Dictionary", "macOS Dictionary, Free Dictionary API, and more.", BookOpen],
  ["Open URL", "build a URL from the text.", Link2],
  ["Shell Script", "run a command. The text comes in `$POPGUY_TEXT`.", SquareTerminal],
  ["AppleScript", "run AppleScript. The text is safely quoted.", Braces],
  ["Run Shortcut", "run a macOS Shortcut.", Workflow]
];

export function ActionsPage() {
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("all");
  const q = query.trim().toLowerCase();

  const groups = useMemo(
    () =>
      libraryCategories
        .filter((item) => category === "all" || item.id === category)
        .map((item) => ({
          category: item,
          presets: libraryPresets.filter(
            (preset) =>
              preset.category === item.id &&
              (!q ||
                preset.name.toLowerCase().includes(q) ||
                preset.description.toLowerCase().includes(q))
          )
        }))
        .filter((group) => group.presets.length > 0),
    [q, category]
  );

  return (
    <main>
      <PageHero
        kicker="Actions"
        title="Actions"
        lead="Everything the toolbar can do. Use the built-in ones, or make your own."
      />

      <div className="container">
        <section className="section section--tight" aria-labelledby="builtin-title">
          <div className="section-head section-head--left">
            <h2 id="builtin-title">Built-in</h2>
          </div>
          <ul className="tile-grid">
            {builtIn.map(([name, text, Icon]) => (
              <li key={name} className="tile">
                <span className="tile__icon"><Icon aria-hidden="true" /></span>
                <strong>{name}</strong>
                <span>{text}</span>
              </li>
            ))}
          </ul>
        </section>

        <section className="section section--tight" aria-labelledby="types-title">
          <div className="section-head section-head--left">
            <h2 id="types-title">Action types</h2>
            <p>Four powered by AI and providers, four scriptable.</p>
          </div>
          <ul className="tile-grid tile-grid--4">
            {actionTypes.map(([name, text, Icon]) => (
              <li key={name} className="tile">
                <span className="tile__icon"><Icon aria-hidden="true" /></span>
                <strong>{name}</strong>
                <span><RichText text={text} /></span>
              </li>
            ))}
          </ul>
        </section>

        <section className="section section--tight" aria-labelledby="library-title">
          <div className="section-head section-head--left">
            <h2 id="library-title">Action library</h2>
            <p className="note">
              {libraryStats.total} ready-made actions in {libraryStats.categories} groups. Install them from the app.
            </p>
          </div>

          <div className="library-tools">
            <input
              type="search"
              className="search"
              placeholder="Search actions…"
              aria-label="Search actions"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
            />
            <div className="filter-chips" role="group" aria-label="Filter by group">
              <button
                type="button"
                className="filter-chip"
                aria-pressed={category === "all"}
                onClick={() => setCategory("all")}
              >
                All
              </button>
              {libraryCategories.map(({ id, name, icon: Icon }) => (
                <button
                  key={id}
                  type="button"
                  className="filter-chip"
                  aria-pressed={category === id}
                  onClick={() => setCategory(id)}
                >
                  <Icon aria-hidden="true" />
                  {name}
                </button>
              ))}
            </div>
          </div>

          {groups.length === 0 ? (
            <p className="note">No actions match "{query.trim()}".</p>
          ) : (
            groups.map(({ category: group, presets }) => (
              <div key={group.id} className="library-group">
                <h3>
                  {group.name} <span className="count">{presets.length}</span>
                </h3>
                <ul className="preset-grid">
                  {presets.map((preset) => {
                    const Icon = preset.icon;
                    return (
                      <li key={preset.id} className="preset">
                        <span className="preset__icon"><Icon aria-hidden="true" /></span>
                        <span className="preset__text">
                          <strong>{preset.name}</strong>
                          <span>{preset.description}</span>
                        </span>
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))
          )}
        </section>

        <section className="section section--tight" aria-labelledby="own-title">
          <div className="section-head section-head--left">
            <h2 id="own-title">Make your own</h2>
          </div>
          <div className="make-grid">
            <div className="make-card">
              <span className="tile__icon"><Settings aria-hidden="true" /></span>
              <h3>In the app</h3>
              <p>Settings → Actions → Add. Set a name, icon, type, and content.</p>
            </div>
            <div className="make-card">
              <span className="tile__icon"><FileJson aria-hidden="true" /></span>
              <h3>From a file</h3>
              <p>Export any action as <code>.json</code>, share it, and others import it.</p>
            </div>
          </div>
        </section>
      </div>
    </main>
  );
}
