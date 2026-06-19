# Third-party dependency archives

This directory stores pinned, unmodified upstream release archives that are
copied into VComTunnel release packages.

They are not authored by VComTunnel. Keep the archive names, upstream sources,
and SHA256 values aligned with `scripts/package-release.ps1`.

## Archives

| Archive | Source | SHA256 |
| --- | --- | --- |
| `hub4com-2.1.0.0-386.zip` | <https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download> | `24CCA36CCF0CAB0F988BB59851B5EC947667EFE53C4F43F290392AD308AC0E01` |
| `com0com-3.0.0.0-i386-and-x64-signed.zip` | <https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download> | `6E5D4359865277430D4AE88C73FB7E648A0ED8E81AEA5002478179CFCB0BB0E1` |

`hub4com` is extracted into the VComTunnel tools cache during dependency setup.
`com0com` is a Windows driver package and still requires an interactive elevated
installer run; bundling the archive does not bypass Windows driver signing or
installation policy.
