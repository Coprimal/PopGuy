import { Moon, Sun } from "lucide-react";
import { useState } from "react";
import { Link, NavLink } from "react-router-dom";
import { GITHUB_URL } from "../constants";
import { Brand } from "./Brand";
import { GitHubIcon } from "./GitHubIcon";

const navigation = [
  ["Actions", "/actions"],
  ["Docs", "/docs"],
  ["Privacy", "/privacy"]
];

function ThemeToggle() {
  const [theme, setTheme] = useState(
    () => document.documentElement.dataset.theme || "light"
  );
  const next = theme === "dark" ? "light" : "dark";

  function toggle() {
    document.documentElement.dataset.theme = next;
    try {
      localStorage.setItem("theme", next);
    } catch {
      // Storage blocked (private mode): the theme still applies for this visit.
    }
    setTheme(next);
  }

  return (
    <button
      type="button"
      className="icon-button theme-toggle"
      aria-label={`Switch to ${next} theme`}
      onClick={toggle}
    >
      {theme === "dark" ? <Sun size={17} aria-hidden="true" /> : <Moon size={17} aria-hidden="true" />}
    </button>
  );
}

export function Header() {
  return (
    <header className="site-header">
      <div className="site-header__inner">
        <Brand />
        <nav className="site-nav" aria-label="Primary navigation">
          <Link className="nav-link" to={{ pathname: "/", hash: "#features" }}>Features</Link>
          {navigation.map(([label, href]) => (
            <NavLink key={href} className="nav-link" to={href}>
              {label}
            </NavLink>
          ))}
        </nav>
        <div className="site-header__tools">
          <ThemeToggle />
          <a className="icon-button" href={GITHUB_URL} target="_blank" rel="noreferrer" aria-label="GitHub">
            <GitHubIcon size={17} />
          </a>
          <Link className="get-pill" to={{ pathname: "/", hash: "#download" }}>Get PopGuy</Link>
        </div>
      </div>
    </header>
  );
}
