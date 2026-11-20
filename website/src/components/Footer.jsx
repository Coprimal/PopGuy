import { Link } from "react-router-dom";
import { GITHUB_URL, RELEASES_URL } from "../constants";

export function Footer() {
  return (
    <footer className="site-footer">
      <div className="site-footer__inner">
        <div className="site-footer__brand">
          <img src="/mascot.png" alt="" width="28" height="28" />
          <span>PopGuy pops up wherever you select text.</span>
        </div>
        <p className="site-footer__links">
          <a href={`${GITHUB_URL}/blob/main/LICENSE`} target="_blank" rel="noreferrer">AGPLv3</a>
          {" · "}by{" "}
          <a href="https://coprimal.dev" target="_blank" rel="noreferrer">Coprimal</a>
          {" · "}
          <a href={RELEASES_URL} target="_blank" rel="noreferrer">Changelog</a>
          {" · "}
          <Link to="/privacy">Privacy</Link>
        </p>
      </div>
    </footer>
  );
}
