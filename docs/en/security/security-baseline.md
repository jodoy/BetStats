# Security Baseline

- HTTPS/TLS in public environments.
- Policy-based authorization.
- MFA for administrative access before public production.
- No secrets in repository or logs.
- Safe production errors.
- Rate limiting for public/auth/admin endpoint classes.
- Provider/news/files treated as untrusted input.
- Dependency, secret, code and container scanning.
- PostgreSQL and internal services are not publicly exposed.
- Backups are protected and restore-tested.
- AI receives sanitized context and is tested against prompt injection.
