import { Link } from "react-router-dom";

export function Brand() {
  return (
    <Link className="brand" to="/">
      <img src="/mascot.png" alt="" width="30" height="30" />
      <span>PopGuy</span>
      {__APP_VERSION__ && <span className="brand-version">v{__APP_VERSION__}</span>}
    </Link>
  );
}
