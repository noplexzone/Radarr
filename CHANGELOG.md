# Changelog

Downstream multi-edition hardening changes are recorded here. This file does not replace upstream release history.

## [Unreleased]

### Fixed

- Preserve the outgoing import backup when the committed replacement is missing or corrupt during finalization or restart recovery. Revalidate destination path ancestors, size, and SHA-256 before recycling the backup.
- Add crash-recovery regression tests for missing and truncated committed replacement files.
