using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Stars/unstars an instrument by symbol, then asks the host page to re-list so the row jumps
// in/out of the Favorites section. Adapted from reference/commands/ToggleFavoriteCommand.cs.
internal sealed partial class ToggleFavoriteCommand : InvokableCommand
{
    private readonly string _symbol;
    private readonly Action _refresh;

    public ToggleFavoriteCommand(string symbol, Action refresh)
    {
        _symbol = symbol;
        _refresh = refresh;
        Name = FavoritesStore.Instance.IsFavorite(symbol) ? "Remove from Favorites" : "Add to Favorites";
        Icon = new IconInfo(FavoritesStore.Instance.IsFavorite(symbol) ? "" : ""); // FavoriteStarFill / FavoriteStar
    }

    public override ICommandResult Invoke()
    {
        FavoritesStore.Instance.Toggle(_symbol);
        _refresh();
        return CommandResult.KeepOpen();
    }
}
