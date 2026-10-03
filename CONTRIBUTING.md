# Contributing

Contributions are welcome: bug reports, testing with other UniFi door stations, documentation and code.
Issues and pull requests may be written in English or German. We usually answer within a week.

## Ground rules
These rules are fixed because SI EntryDesk can open doors:
- **Official UniFi APIs only** (UniFi Access Developer API, UniFi Protect Integration API). No undocumented endpoints.
- **No secrets or site data** in code, tests, logs, issues or screenshots: no tokens, API keys, IP addresses, device IDs,
  company or door names. Use placeholders.
- **Pin certificates**, never disable TLS validation.
- Changes to **opening doors** or **talkback** need tests for the rules (when it is allowed) and a careful review.
- Security vulnerabilities go to [SECURITY.md](SECURITY.md), not to public issues.

## Build and test
Requires the .NET 10 SDK. Windows binaries also build on macOS and Linux (`EnableWindowsTargeting`); running them
needs Windows 11.
```
dotnet test tests/SIEntryDesk.Core.Tests     # no UniFi hardware needed
./build/publish.sh                           # tests, then both packages in artifacts/
```
Against a real console (VPN or local network), credentials only from environment variables:
`tools/SIEntryDesk.DevCli` (`listen`, `video`, `talk`, `setup-check`). Never commit `*.env` files.

## Structure
- `src/SIEntryDesk.Core`: shared logic (platform independent, tested)
- `src/SIEntryDesk.Audio`: talkback audio (Opus)
- `src/SIEntryDesk.Service`: Windows service, holds the credentials
- `src/SIEntryDesk.App`: tray app (WPF), never sees credentials
- `deploy/`: files shipped in the packages; `docs/admin`: admin guides; `docs/entwicklung`: design notes (German)

## Ideas where help is welcome
- Testing with other door stations (e.g. G4 Doorbell, G6 Entry variants) and other UniFi versions
- English user interface (the app is German today)
- Microphone selection in the app
- Reports from deployments with Intune or other tools

## Pull requests
Keep them focused, add tests for logic changes, update the docs in `docs/admin` if admins are affected, and check the
diff for site data before pushing.
