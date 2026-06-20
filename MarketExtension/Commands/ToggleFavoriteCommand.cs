using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Stars/unstars an instrument by symbol, then asks the host page to re-list so the row jumps
// in/out of the Favorites section. Adapted from reference/commands/ToggleFavoriteCommand.cs.
internal sealed partial class ToggleFavoriteCommand : InvokableCommand
{
    private readonly DomainInstrument _instrument;
    private readonly Action _refresh;

    public ToggleFavoriteCommand(DomainInstrument instrument, Action refresh)
    {
        _instrument = instrument;
        _refresh = refresh;
        Name = FavoritesStore.Instance.IsFavorite(instrument.Symbol) ? "Remove from Favorites" : "Add to Favorites";
        Icon = new IconInfo(FavoritesStore.Instance.IsFavorite(instrument.Symbol) ? "" : ""); // FavoriteStarFill / FavoriteStar
    }

    public override ICommandResult Invoke()
    {
        FavoritesStore.Instance.Toggle(_instrument);
        _refresh();
        return CommandResult.KeepOpen();
    }
}
