# VComTunnel.Serial KMDF Prototype

This directory is the phase 2 driver scaffold. It is intentionally not installed or built by the default .NET solution.

Target behavior:

- Register a single visible COM port such as `COM22`
- Implement the minimum serial IOCTL/read/write surface needed by standard Windows serial clients
- Exchange bytes and line-control events with `VComTunnel.Service`
- Keep RFC2217 negotiation, reconnect behavior, logging, and configuration in user mode

Manual implementation checkpoints:

1. Create the KMDF driver project from the Microsoft virtual serial sample baseline.
2. Replace sample identifiers with `VComTunnel.Serial`.
3. Add an internal user-mode channel for `VComTunnel.Service`.
4. Implement read/write queues, cancellation, timeout handling, and line-control IOCTLs.
5. Generate a test-signed driver package.
6. Install only after manually enabling test signing and confirming the target machine is disposable or backed up.

The current service reports `kmdf` mappings as `Unsupported` so users get a clear diagnostic instead of a silent failure.
