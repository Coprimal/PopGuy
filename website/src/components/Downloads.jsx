import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { ButtonLink } from "./ButtonLink";
import {
  DOWNLOAD_URL,
  WINDOWS_DOWNLOAD_ARM64,
  WINDOWS_DOWNLOAD_X64,
  WINDOWS_RELEASE_URL
} from "../constants";
import { detectOs, detectWindowsArch } from "../platform";

const windowsSetup = { x64: WINDOWS_DOWNLOAD_X64, arm64: WINDOWS_DOWNLOAD_ARM64 };

/**
 * The two download buttons on one line, one short note below. The visitor's system comes first, as the primary
 * button. On a Windows PC the button takes the installer for its chip; the other builds are on the release page.
 */
export function Downloads({ tone = "default" }) {
  const os = detectOs();
  const [arch, setArch] = useState("x64");

  useEffect(() => {
    // Only a Windows PC's chip matters: a Mac's (Apple Silicon reports "arm") must not pick the ARM64 build.
    if (os !== "windows") return undefined;
    let live = true;
    detectWindowsArch().then((found) => {
      if (live) setArch(found);
    });
    return () => {
      live = false;
    };
  }, [os]);

  const windowsFirst = os === "windows";

  const macButton = (
    <ButtonLink href={DOWNLOAD_URL} icon="download" variant={windowsFirst ? "secondary" : "primary"} key="mac">
      Download for macOS
    </ButtonLink>
  );
  const windowsButton = (
    <ButtonLink href={windowsSetup[arch]} icon="download" variant={windowsFirst ? "primary" : "secondary"} key="windows">
      Download for Windows
    </ButtonLink>
  );
  // One short line: the systems, the Windows build's missing signature (SmartScreen warns), every other build.
  const windowsLabel = arch === "arm64" ? "Windows 11 ARM64" : "Windows 11";

  return (
    <div className={`downloads downloads--${tone}`}>
      <div className="download-buttons">{windowsFirst ? [windowsButton, macButton] : [macButton, windowsButton]}</div>
      <p className="note">
        macOS 13+ · {windowsLabel}, unsigned (<Link to="/docs#windows">why</Link>) ·{" "}
        <a href={WINDOWS_RELEASE_URL}>All downloads</a>
      </p>
    </div>
  );
}
