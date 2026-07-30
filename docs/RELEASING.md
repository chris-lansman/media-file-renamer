# Releasing Media File Renamer

## Release checklist

1. Move completed entries from `Unreleased` in `CHANGELOG.md` into a new version section.
2. Confirm the `VersionPrefix` in `MediaFileRenamer.App.csproj` matches the intended release line.
3. Run the validation commands from the README on Windows.
4. Complete the [real-world acceptance matrix](ACCEPTANCE.md) against the packaged release candidate.
5. Push a semantic-version tag such as `v1.1.0`.
6. Confirm that the `Validate`, `Package Windows x64`, and `Publish GitHub release` jobs all pass.
7. Download the release ZIP and checksum, verify the checksum, and launch the packaged executable on a clean Windows user profile.

The workflow will reject a tag that does not resolve to a three-part semantic version.

## Optional Authenticode signing

Unsigned builds remain fully supported. To sign future builds, configure these GitHub Actions secrets:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64`: Base64 text of a code-signing PFX.
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`: Password for that PFX.

Both secrets must be present or both absent. The workflow imports the certificate only into the ephemeral runner user's certificate store, signs `MediaFileRenamer.exe`, verifies the signature, and then removes the imported certificate and temporary PFX.

An optional `WINDOWS_TIMESTAMP_URL` repository variable can override the default RFC 3161 timestamp service. The certificate must permit code signing, its private key must be exportable into the supplied PFX before upload, and it should chain to a CA trusted by target Windows systems.

## Artifact provenance

GitHub provenance attestation is automatic for public repositories. A private repository can opt in with the repository variable `ENABLE_PRIVATE_ATTESTATION=true`, but private/internal attestation requires a compatible GitHub Enterprise Cloud plan. GitHub Enterprise Server does not support this attestation path.

## MSIX status

The production artifact is currently a portable, self-contained ZIP. Shipping an MSIX additionally requires decisions that cannot be safely inferred in CI:

- a stable package identity name;
- a Publisher distinguished name that exactly matches the signing certificate or Microsoft Store identity;
- Store display identity if Store distribution is desired;
- an MSIX-compatible certificate and long-term certificate renewal process;
- installer upgrade, downgrade, and data-retention testing.

After those values exist, add a Windows Application Packaging project or a reviewed package manifest and sign the resulting MSIX in the same gated release path. Do not invent a temporary Publisher identity for public releases because changing it later breaks the Windows package upgrade relationship.

## Credentials

Release packages never contain TMDB or TVDB credentials. Every user supplies and manages their own provider credentials in the application.
