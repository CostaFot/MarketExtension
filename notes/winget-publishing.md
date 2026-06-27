# Publishing to WinGet

How `MarketExtension` gets onto `winget install`. Distinct from the Microsoft Store / Partner Center path
(that's `notes/releasing.md`) — though the two are linked by the **signing caveat** below.

## Package identity

| Thing | Value |
|---|---|
| WinGet `PackageIdentifier` | `CostaFotiadis.MarketsExtensionForCmdPalette` |
| MSIX identity (inside the bundle) | `CostaFotiadis.MarketsExtensionforCommandPalette` |
| `PackageFamilyName` | `CostaFotiadis.MarketsExtensionforCommandPalette_zb8zfd5z3ypd4` |
| Discovery tag | `windows-commandpalette-extension` |

The WinGet `PackageIdentifier` is **not** the MSIX identity — it's WinGet's own catalog key. They're allowed
to differ (and do here). Install-state tracking uses the `PackageFamilyName`, which wingetcreate reads from
the MSIX automatically.

⚠️ **The 32-char gotcha.** WinGet's `PackageIdentifier` allows max **32 characters per dot-separated
segment**. The natural `…MarketsExtensionforCommandPalette` is **33** → rejected with *"Input does not match
the valid format pattern."* That's why the catalog ID is the shortened `…MarketsExtensionForCmdPalette` (29).

⚠️ **The signing caveat (the big one).** WinGet's automated PR validation installs the package in a Windows
Sandbox, which only trusts publicly-rooted certificates. Our `release-msix.yml` bundle is **self-signed** →
its sandbox install fails the cert check. The **Store-signed** bundle (downloaded back from Partner Center
after a Store submission) chains to Microsoft's trusted root → installs cleanly. **Always point WinGet at the
Store-signed bundle, never the raw self-signed CI artifact.**

Confirmed against the **AdbExtension precedent** (winget PR #375465, merged): its published MSIX
(`github.com/CostaFot/AdbExtension/releases/download/v1.0.11.0/AdbExtension_1.0.11.0_x64.msix`) is signed by
`CN=Microsoft Marketplace CA G 027` → `Microsoft Root Certificate Authority 2011` — i.e. a **Store-signed**
package hosted on a GitHub release, not the self-signed CI artifact. That's the pattern that passes
validation; Markets v1.2.0.0 (PR #394493) follows it.

## Discovery tag

In `…locale.en-US.yaml`, the `Tags:` list **must** include `windows-commandpalette-extension`. That's what
makes Command Palette surface a one-click `winget install` for the extension. (Verified against real CmdPal
extensions in `microsoft/winget-pkgs`.)

## First submission — done manually (v1.2.0.0)

The first version can't use the automated `update` flow (the package doesn't exist in `winget-pkgs` yet), and
`wingetcreate new` is interactive. The flow that was used:

1. Store-signed `MarketExtension_1.2.0.0_Bundle.Msixbundle` attached to GitHub Release `v1.2.0.0`.
2. `wingetcreate new "<release-asset-url>" --out .\winget-manifests`
   — accept auto-detected values; **No** to optional-field prompts; **No** to submit (we edit first).
3. Add the discovery tag + Publisher/Author/Package URLs + Description to `…locale.en-US.yaml`.
4. `winget validate --manifest .\winget-manifests\manifests\c\CostaFotiadis\MarketsExtensionForCmdPalette\1.2.0.0`
5. `wingetcreate submit --token <GH_PAT> <that folder>`  ← PAT = classic token, `public_repo` scope.

`submit` forks `microsoft/winget-pkgs`, pushes a branch, opens the PR. Bot validates → moderator review →
merge → live within a few hours.

`winget-manifests/` is `.gitignore`d (it's a PR payload, not source).

## Future updates — `.github/workflows/update-winget.yml`

Manual `workflow_dispatch` **on purpose** — it is NOT auto-triggered on `release: published`, because that
would fire on the self-signed CI bundle and fail validation (signing caveat). Run it only **after** a
Store-signed bundle is hosted at the release URL:

1. Bump version + run `release-msix.yml` (creates the GitHub Release; bundle is self-signed).
2. Submit that bundle to Partner Center → Store re-signs.
3. Download the Store-signed bundle from Partner Center, upload it to the same GitHub Release.
4. Actions → **Update WinGet Manifest** → run with `version` = e.g. `1.2.0.1`
   (pass `bundle_url` if the hosted asset name/casing differs from the default).

The workflow runs `wingetcreate update … --submit` and opens the PR. Requires repo secret **`WINGET_TOKEN`**
(classic PAT, `public_repo` scope).

## Verify after merge

```
winget show CostaFotiadis.MarketsExtensionForCmdPalette
winget install CostaFotiadis.MarketsExtensionForCmdPalette
```
