# ADR-0001: Store Steam Authenticator secrets as protected server data

## Status

Accepted

## Date

2026-10-16

## Context

The dashboard needs to generate Steam Guard device codes from an SDA `.maFile` and use the Steam login name to match the code source to an existing account. A MAFile contains a `shared_secret`, which is equivalent to a long-lived authentication secret. Returning it to the browser, logging it, or storing the uploaded file unchanged would create unnecessary exposure.

The referenced Steam Desktop Authenticator project is no longer supported and documents that its legacy encrypted MAFile format uses PBKDF2 plus AES-CBC without an integrity tag. Accepting that format would require reproducing a legacy file format whose security properties are weaker than the hub's existing data-protection storage.

## Decision

- Accept only an unencrypted JSON MAFile through an authenticated multipart endpoint with a 256 KiB size limit.
- Parse only `account_name` and `shared_secret`; do not persist the uploaded file, session data, identity secret or revocation code.
- Validate the account name and Base64 shared secret at the API boundary.
- Encrypt the shared secret at rest with a dedicated ASP.NET Data Protection purpose and the existing persisted key ring.
- Return only non-secret metadata from list endpoints. Generate a code only through an authenticated, rate-limited POST endpoint and never cache or persist the generated code.
- Match automatic device-code filling by normalized Steam login name. If the login name is not present in the vault, retain the manual-code path.
- Reject encrypted SDA MAFiles and require local decryption before import.

## Alternatives considered

### Store the complete MAFile

Rejected because it would retain identity secrets, revocation codes and session material that the requested code-generation feature does not need.

### Store the shared secret in plaintext

Rejected because a filesystem read or backup leak would immediately expose every imported authenticator.

### Port the SDA encrypted file format

Rejected for this scope because the format is legacy AES-CBC without an integrity tag and requires a companion manifest. Local decryption keeps the passphrase and legacy parsing outside the server.

## Consequences

- The server can generate device codes without exposing the shared secret to the browser.
- Losing the ASP.NET Data Protection key ring makes the imported authenticator records unrecoverable; the deployment must back up the key ring securely.
- Users with encrypted SDA files must decrypt them locally before import.
- This feature intentionally does not include trade confirmations, authenticator enrollment or authenticator removal.
