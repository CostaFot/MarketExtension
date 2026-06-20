# Market Extension

A [PowerToys Command Palette](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview) extension.

> Scaffolded from the **AdbExtension** template. Replace this README, the sample command/page,
> and the assets in `MarketExtension/Assets/` with your own.

## Requirements

- Windows 11
- [PowerToys](https://github.com/microsoft/PowerToys) with Command Palette enabled

## Development

```powershell
dotnet build MarketExtension.sln
```

Then deploy the MSIX package and reload Command Palette to pick up changes.

- **Conventions, the Command Palette toolkit quick-reference, and the release process** are in [`CLAUDE.md`](CLAUDE.md).
- **Worked examples** (real commands/pages from the AdbExtension this was derived from) live in [`reference/`](reference/) — not compiled; copy and adapt.

## License

[MIT](LICENSE)
