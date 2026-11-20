import { useEffect } from "react";
import {
  BrowserRouter,
  Navigate,
  Route,
  Routes,
  useLocation
} from "react-router-dom";
import { SITE_URL } from "./constants";
import { useScrollEffects } from "./components/demo/motion";
import { Footer } from "./components/Footer";
import { Header } from "./components/Header";
import { ActionsPage } from "./pages/ActionsPage";
import { DocsPage } from "./pages/DocsPage";
import { HomePage } from "./pages/HomePage";
import { PrivacyPage } from "./pages/PrivacyPage";

const titles = {
  "/": "PopGuy — A guy pops up from your selected text with magic",
  "/actions": "Actions — PopGuy",
  "/docs": "Docs — PopGuy",
  "/privacy": "Privacy — PopGuy"
};

export function AppRoutes() {
  const location = useLocation();

  useEffect(() => {
    document.title = titles[location.pathname] ?? titles["/"];
    // Not in index.html: every route serves that file, so a static tag would
    // mark each page as a copy of the home page.
    let canonical = document.querySelector('link[rel="canonical"]');
    if (!canonical) {
      canonical = document.createElement("link");
      canonical.rel = "canonical";
      document.head.append(canonical);
    }
    canonical.href = SITE_URL + (titles[location.pathname] ? location.pathname : "/");
  }, [location.pathname]);

  // React Router keeps the scroll position between routes and ignores the hash: jump to the anchor, or to the top.
  useEffect(() => {
    const id = location.hash.slice(1);
    const target = id ? document.getElementById(decodeURIComponent(id)) : null;
    if (target) target.scrollIntoView?.();
    // Instant: the page's smooth scrolling is for in-page anchors, not for a new page.
    else window.scrollTo({ top: 0, left: 0, behavior: "instant" });
  }, [location.pathname, location.hash]);

  useScrollEffects(location.pathname);

  return (
    <div className="page">
      <a className="skip-link" href="#main-content">Skip to content</a>
      <Header />
      <div id="main-content">
        <Routes>
          <Route path="/" element={<HomePage />} />
          <Route path="/actions" element={<ActionsPage />} />
          <Route path="/docs" element={<DocsPage />} />
          <Route path="/privacy" element={<PrivacyPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </div>
      <Footer />
    </div>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <AppRoutes />
    </BrowserRouter>
  );
}