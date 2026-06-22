using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// The watchlist/favorites membership actions. These now live solely on the per-symbol SymbolDetailPage
// (the single place to manage membership); list rows just navigate into it. Each one just mutates
// WatchlistStore for one instrument; the store re-publishes its observable subsets, so every live page,
// the dock, and the detail page's own command bar re-render themselves \u2014 no manual refresh callback is
// threaded through any more. It also pops a confirmation toast ("Added AAPL to watchlist") and keeps the
// palette open: the silent re-render alone left users unsure the action took, and KeepOpen lets them keep
// editing. Adapted from reference/commands/ToggleFavoriteCommand.cs.
internal abstract partial class MembershipCommand : InvokableCommand
{
    // Segoe MDL2 glyphs: Add, Delete, FavoriteStar (outline), FavoriteStarFill.
    protected const string AddGlyph = "\uE710";
    protected const string DeleteGlyph = "\uE74D";
    protected const string StarGlyph = "\uE734";
    protected const string StarFillGlyph = "\uE735";

    protected readonly DomainInstrument Instrument;

    protected MembershipCommand(DomainInstrument instrument) => Instrument = instrument;

    // Performs the mutation and returns the confirmation message to toast (so toggles can report
    // exactly what they did).
    protected abstract string Apply();

    public override ICommandResult Invoke()
    {
        var message = Apply();
        return CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });
    }
}

internal sealed partial class AddToWatchlistCommand : MembershipCommand
{
    public AddToWatchlistCommand(DomainInstrument instrument) : base(instrument)
    {
        Name = "Add to Watchlist";
        Icon = new IconInfo(AddGlyph);
    }

    protected override string Apply()
    {
        WatchlistStore.Instance.AddToWatchlist(Instrument);
        return $"Added {Instrument.Symbol} to watchlist";
    }
}

internal sealed partial class RemoveFromWatchlistCommand : MembershipCommand
{
    public RemoveFromWatchlistCommand(DomainInstrument instrument) : base(instrument)
    {
        Name = "Remove from Watchlist";
        Icon = new IconInfo(DeleteGlyph);
    }

    protected override string Apply()
    {
        WatchlistStore.Instance.RemoveFromWatchlist(Instrument.Symbol);
        return $"Removed {Instrument.Symbol} from watchlist";
    }
}

internal sealed partial class AddToFavoritesCommand : MembershipCommand
{
    public AddToFavoritesCommand(DomainInstrument instrument) : base(instrument)
    {
        Name = "Add to Favorites";
        Icon = new IconInfo(StarGlyph);
    }

    protected override string Apply()
    {
        WatchlistStore.Instance.AddToFavorites(Instrument);
        return $"Added {Instrument.Symbol} to favorites";
    }
}

internal sealed partial class RemoveFromFavoritesCommand : MembershipCommand
{
    public RemoveFromFavoritesCommand(DomainInstrument instrument) : base(instrument)
    {
        Name = "Remove from Favorites";
        Icon = new IconInfo(StarFillGlyph);
    }

    protected override string Apply()
    {
        WatchlistStore.Instance.RemoveFromFavorites(Instrument.Symbol);
        return $"Removed {Instrument.Symbol} from favorites";
    }
}
