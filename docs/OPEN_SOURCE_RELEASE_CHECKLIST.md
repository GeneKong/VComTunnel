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
- Confirm whether the release should be the default self-contained portable
  package or the smaller `-FrameworkDependent` package that requires target
  .NET runtimes.
- For installer-style distribution, run `scripts\package-velopack.ps1` and
  confirm the Velopack `Setup.exe` assets are generated.
- For online release builds, run the GitHub Actions `Package` workflow or push
  a `v*` tag and review the uploaded artifacts before publishing.
- Generate MSI with `scripts\package-velopack.ps1 -Msi` only when Windows
  Installer integration is actually needed.
- Keep MSIX out of the main release path unless a separate store/enterprise
  packaging decision is made.
- Current WPF GUI packaging is Windows-only; cross-platform Velopack packaging
  should use the future Avalonia publish output through `-PackDir` and
  `-MainExe`.
- Build macOS packages on macOS because the platform packaging and signing
  tooling is not available from Windows/Linux.

## Driver and Hardware Safety

- Keep the KMDF backend marked experimental until broader serial compatibility
  and real-device validation are complete.
- Do not ship a prototype driver as production-ready.
- Document whether test signing, administrator elevation, or reboot is required.
- Verify DTR/RTS/BREAK/purge behavior on safe hardware before recommending it
  for field use.

## Release Artifacts

- Confirm the public artifact name ends in `portable` for the default
  self-contained user download.
- Confirm `README-FIRST.txt` and `README-FIRST.zh-CN.txt` are generated.
- Confirm `Start-VComTunnel-Portable.cmd` launches the GUI from an extracted
  writable folder and keeps app data under the package `data` directory.
- Confirm `Setup-Dependencies-Portable.cmd` uses bundled dependency archives
  when present and still leaves com0com driver installation to an elevated
  user approval.
- Confirm `Install-Windows-Service.cmd` and `Uninstall-Windows-Service.cmd`
  elevate through UAC and call `vcomtunnelctl service ...` instead of requiring
  users to type `sc.exe` commands.
- Confirm `LICENSE`, `README.md`, `README.zh-CN.md`, and `SECURITY.md` are
  included in the package root.
- Confirm `THIRD-PARTY-NOTICES.txt` is generated in the package.
- Confirm `SHA256SUMS.txt` is generated in the package.
- Confirm the package can run without runtime network downloads when dependency
  archives are bundled.
- Confirm the Velopack installer keeps com0com driver setup explicit and does
  not silently install drivers or the experimental KMDF prototype.
- Confirm GitHub Actions artifacts include the Velopack release directory and
  the staged portable zip.
- Confirm generated installer/update assets are not committed to source
  control.
- Publish release notes that identify the stable backend path and experimental
  backend status.
