# Security Policy

VComTunnel is designed for trusted Windows lab and development environments.
It bridges local serial clients to remote RFC2217 endpoints and can optionally
interact with Windows virtual serial drivers.

## Supported Versions

The project is still pre-1.0. Security fixes are expected to target the latest
commit on the main development branch unless a release branch is created.

## Reporting a Vulnerability

Please report security issues privately to the repository owner instead of
opening a public issue with exploit details. Include:

- Affected version or commit
- Backend in use: `com0comHub4com`, `com0comService`, or `kmdf`
- Local service exposure details
- RFC2217 endpoint exposure details
- Reproduction steps and expected impact

## Deployment Guidance

- Keep the local VComTunnel API bound to loopback.
- Do not expose RFC2217 endpoints or VComTunnel service ports to untrusted
  networks without a separate trusted transport layer.
- Treat RFC2217 peers as trusted devices. RFC2217 does not provide built-in
  authentication or encryption.
- Run the service with the minimum privileges required for the selected backend.
- Install the KMDF prototype only on test machines where recovery is possible.
  It is an experimental/test-signed driver path; Windows may require Test Mode
  and a reboot, and Secure Boot or driver signing policy can block installation.
- Review DTR/RTS/BREAK/purge behavior before connecting boards that reset or
  enter boot modes through serial-control lines.

## Out of Scope

The current prototype does not claim to provide:

- Network authentication or authorization
- TLS or encrypted RFC2217 transport
- Production EV/attestation-signed driver distribution
- Hard multi-tenant isolation between users on the same Windows machine
