# Contributing to PopGuy

Thanks for your interest — contributions are welcome!

## How to contribute

- **Found a bug or have an idea?** Open an issue to discuss it first.
- **Sending code?**
  1. Build from source and make sure `xcodebuild test -project macos/PopGuy.xcodeproj -scheme PopGuy -destination 'platform=macOS'` passes.
  2. Match the existing style — the project is Swift 6 with strict concurrency.
  3. Keep pull requests focused, and describe what changed and why.
- **Changing the Windows port (`windows/`)?**
  1. Run its tests: `dotnet test windows/tests/PopGuy.Tests/PopGuy.Tests.csproj` (.NET 10 SDK; works on macOS
     and Windows).
  2. The app itself (WinUI 3) is built by GitHub Actions (`.github/workflows/windows.yml`) on every pull request that
     touches `windows/`; check that the run is green. See [windows/README.md](windows/README.md).
  3. UI text is English only; tooltips go through the app's `HoverTooltip`, never the default WinUI tooltip.

## Update feed

- New macOS builds (1.4.12 and later) read the Sparkle feed https://popguy.coprimal.dev/appcast.xml. It is deployed
  with the website by `.github/workflows/deploy-pages.yml` from `appcast.xml` at the repo root.
- macOS builds 1.4.11 and earlier read https://dinhanhthi.github.io/PopGuy/appcast.xml. GitHub stopped serving that
  address when the repo moved, so the user site repo `dinhanhthi/dinhanhthi.github.io` serves a copy at
  `PopGuy/appcast.xml`. `scripts/sync-feed-mirror.sh` updates it, and `scripts/publish-appcast.sh` runs it on every
  release. Never edit the mirror by hand.
- Windows builds update through Velopack from the GitHub releases API. Builds 1.4.11 and earlier call
  `api.github.com/repos/dinhanhthi/PopGuy`, which GitHub redirects to `Coprimal/PopGuy`.

Never:

- create a repo named `dinhanhthi/PopGuy`: it would take over the old path and kill GitHub's redirects that old builds
  rely on;
- delete the mirror file or stop syncing it;
- move `appcast.xml` from the repo root, or change the Pages source from "GitHub Actions";
- change the bundle ID, the Sparkle EdDSA public key or the signing identity.

Check that all three paths work:

```sh
curl -fsSL https://popguy.coprimal.dev/appcast.xml | grep shortVersionString
curl -fsSL https://dinhanhthi.github.io/PopGuy/appcast.xml | cmp - appcast.xml
curl -sIL 'https://api.github.com/repos/dinhanhthi/PopGuy/releases?per_page=1' | grep -E '^HTTP|^location'
```

## Old addresses

- https://pg.dinhanhthi.com 301-redirects to the same path on https://popguy.coprimal.dev, query string kept, through a
  Cloudflare redirect rule in the zone `dinhanhthi.com` (ruleset `http_request_dynamic_redirect`) on a proxied `pg`
  DNS record. Never delete that rule or un-proxy the record.
- https://github.com/dinhanhthi/PopGuy redirects to https://github.com/Coprimal/PopGuy (GitHub's transfer redirect).

## License

PopGuy is open source under the **GNU Affero General Public License v3.0**. By
submitting a contribution, you agree that it is licensed under the same terms. You
confirm that the contribution is your own original work, or that you are authorized to
submit it.

No Contributor License Agreement is required.
