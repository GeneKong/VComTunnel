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

- Run `dotnet restore VComTunnel.sln`.
- Run `dotnet build -c Release --no-restore VComTunnel.sln`.
- Run `dotnet run -c Release --no-build --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj`.
- Run `scripts\smoke-local.ps1 -Configuration Release -NoBuild`.
- Run the fake-server RFC2217 probe.
- For release packaging, run
  `scripts\package-release.ps1 -Version <version> -Runtime win-x64 -Restore`
  with the pinned bundled dependency archive set, or with a reviewed dependency
  archive cache when repeatability requires a separate cache.
- Confirm whether the release should be the default self-contained portable
  package or the smaller `-FrameworkDependent` package that requires target
  .NET runtimes.
- For installer-style distribution, run
  `scripts\package-velopack.ps1 -Version <version> -Runtime win-x64 -Restore -Msi`
  and confirm the Velopack `Setup.exe` and MSI assets are generated.
- For online release builds, run the GitHub Actions `Package` workflow or push
  a `v*` tag and review the uploaded artifacts before publishing.
- Generate MSI only after Windows installer output has been validated locally.
- Keep MSIX out of the main release path unless a separate store/enterprise
  packaging decision is made.
- Current WPF GUI packaging is Windows-only; do not document cross-platform
  Velopack commands until Avalonia publish output has been validated.
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

- Confirm every public GitHub Release asset name includes the release version,
  for example `VComTunnel-1.0.0.rc2-win-x64-Setup.exe`.
- Confirm the public artifact name includes `portable` for the default
  self-contained user download.
- Confirm `README-FIRST.txt` and `README-FIRST.zh-CN.txt` are generated.
- Confirm README screenshots and runtime descriptions match the current GUI
  behavior.
- Confirm `Start-VComTunnel-Portable.cmd` launches the GUI from an extracted
  writable folder and keeps app data under the package `data` directory.
- Confirm `Setup-Dependencies-Portable.cmd` uses bundled dependency archives
  when present, rejects invalid archives, and still leaves com0com driver
  installation to an elevated user approval.
- Confirm `Install-Windows-Service.cmd` and `Uninstall-Windows-Service.cmd`
  elevate through UAC and call `vcomtunnelctl service ...` instead of requiring
  users to type `sc.exe` commands.
- Confirm `LICENSE`, `README.md`, `README.zh-CN.md`, and `SECURITY.md` are
  included in the package root.
- Confirm `THIRD-PARTY-NOTICES.txt` is generated in the package.
- Confirm `SHA256SUMS.txt` is generated in the package.
- Confirm the package can run without runtime network downloads when dependency
  archives are bundled.
- Confirm GitHub tag packaging bundles the pinned `third_party/dependencies`
  archives by default and validates their SHA256 values before publishing.
- Confirm release versions containing `rc`, `alpha`, `beta`, `pre`, or
  `preview` are marked as GitHub pre-releases.
- Confirm the Velopack installer keeps com0com driver setup explicit and does
  not silently install drivers or the experimental KMDF prototype.
- Confirm GitHub Actions artifacts include only versioned public release assets
  from `artifacts\github-release\public\<runtime>`.
- Confirm generated installer/update assets are not committed to source
  control.
- Publish release notes that identify the stable backend path and experimental
  backend status.
