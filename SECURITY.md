# Security Policy

## Reporting a vulnerability
Do not publish exploitable security details in a public GitHub Issue.

Use GitHub's private vulnerability reporting feature when enabled. If that feature is unavailable, contact the repository owner privately.

Please include:
- affected component;
- reproduction steps;
- potential impact;
- suggested mitigation, if known.

## Scope
Security reports may cover authentication/authorization, secret exposure, injection, XSS/CSRF/CORS, data leakage, provider-input abuse, AI prompt injection, model/data poisoning, supply-chain issues and audit tampering.

## Secrets
BetStats secrets must never be committed to the repository. Rotate any credential that is accidentally exposed.
