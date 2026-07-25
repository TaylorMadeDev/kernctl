# Security policy

kernctl is pre-release software and currently performs no privileged or destructive
system actions.

## Reporting

Do not open a public issue for a suspected vulnerability. Contact the repository
owner privately through the security-reporting mechanism configured on GitHub.
Include reproduction steps, impact, and the affected revision. Do not include
passwords, authentication material, personal files, or other secrets.

## Security boundaries

- The desktop UI is an unelevated process.
- Future privileged operations will be narrowly allow-listed in a restricted broker.
- Logs must be structured and must not contain credentials, tokens, cookies, or file
  contents.
- Optimizations require explicit detection, explanation, verification, and rollback.
