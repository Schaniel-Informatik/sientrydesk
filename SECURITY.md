# Security Policy

SI EntryDesk can open doors. Please report vulnerabilities privately, not as a public issue.

**Contact:** marcel@schaniel.com (Schaniel Informatik). Please include steps to reproduce and the affected version.
You will get an answer within a few working days. Please give us reasonable time to fix the issue before disclosure.

## Supported versions
Only the latest release is supported.

## Security model (short)
- The Windows service holds the UniFi credentials, encrypted with DPAPI (machine key) in
  `C:\ProgramData\SIEntryDesk\secrets.dat`, readable only by SYSTEM, Administrators and the service account.
  A **local administrator can read them**; standard users cannot. The tray app never sees credentials.
- All HTTPS/WSS/RTSPS connections to the UniFi console are **certificate-pinned** (SHA-256 fingerprint in the
  configuration). TLS validation is never disabled.
- The service opens a door only during a ring (and up to 10 s after, for specific end reasons), and allows talkback
  only during a ring. Anyone who obtains the Access token itself is not bound by these rules.
- Talkback audio to the door station is unencrypted RTP over UDP in the local network; this is how the official
  UniFi Protect API works.
- Only official UniFi Access / Protect APIs are used. No telemetry.

Details (German): [docs/admin/installation.md](docs/admin/installation.md), [docs/admin/betrieb.md](docs/admin/betrieb.md).
