# Open Source Release Checklist

Use this checklist before tagging a public VComTunnel release.

## Repository

- Confirm the repository `LICENSE` file still matches the intended release
  terms.
- Confirm third-party notices for bundled `com0com` and `hub4com` archives.
- Confirm the GitHub repository description and topics match the current scope.
- Keep generated release archives, driver build outputs, logs, and local smoke
  artifacts out of source control.

Suggested repository description:

```text
Windows virtual COM to RFC2217 bridge manager with GUI, service, CLI, and experimental KMDF backend.
```

Suggested topics:

```text
windows, serial-port, virtual-com-port, rfc2217, dotnet, wpf, kmdf, com0com, hub4com, embedded-tools
```

## Build Validation

- Run `dotnet build VComTunnel.sln`.
- Run `dotnet run --no-build --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj`.
- Run `scripts\smoke-local.ps1`.
- Run the fake-server RFC2217 probe.
- For release packaging, run `scripts\package-release.ps1` with a reviewed
  dependency archive cache when repeatability matters.

## Driver and Hardware Safety

- Keep the KMDF backend marked experimental until broader serial compatibility
  and real-device validation are complete.
- Do not ship a prototype driver as production-ready.
- Document whether test signing, administrator elevation, or reboot is required.
- Verify DTR/RTS/BREAK/purge behavior on safe hardware before recommending it
  for field use.

## Release Artifacts

- Confirm `THIRD-PARTY-NOTICES.txt` is generated in the package.
- Confirm `SHA256SUMS.txt` is generated in the package.
- Confirm the package can run without runtime network downloads when dependency
  archives are bundled.
- Publish release notes that identify the stable backend path and experimental
  backend status.
