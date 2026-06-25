using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using MarketExtension.Properties;

namespace MarketExtension;

// Removes a holding from the portfolio. Like the watchlist/favorites membership commands, it just
// mutates the store (PortfolioStore re-publishes its flows, so the Portfolio screen and the detail
// page's command bar re-render themselves) and pops a confirmation toast while keeping the palette open
// so the user knows it took and can keep editing. Adding/editing a holding is a page (SetQuantityPage),
// not a one-shot command, because it needs a quantity input.
internal sealed partial class RemoveFromPortfolioCommand : InvokableCommand
{
    private const string DeleteGlyph = "\uE74D"; // Segoe MDL2 Delete

    private readonly DomainInstrument _instrument;

    public RemoveFromPortfolioCommand(DomainInstrument instrument)
    {
        _instrument = instrument;
        Name = Resources.Cmd_RemoveFromPortfolio;
        Icon = new IconInfo(DeleteGlyph);
    }

    public override ICommandResult Invoke()
    {
        PortfolioStore.Instance.Remove(_instrument.Symbol);
        return CommandResult.ShowToast(new ToastArgs
        {
            Message = Strings.Format(Resources.Toast_RemovedFromPortfolio, _instrument.Symbol),
            Result = CommandResult.KeepOpen(),
        });
    }
}
