using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// The watchlist/favorites membership actions surfaced on list rows. Each one mutates WatchlistStore
// for one instrument, then asks the host page to re-list so the row reflects its new membership (or
// drops out of the list). KeepOpen keeps the user on the page so they can act on several rows in a
// row — matching the project's star-in-place UX. Adapted from reference/commands/ToggleFavoriteCommand.cs.
internal abstract partial class MembershipCommand : InvokableCommand
{
    // Segoe MDL2 glyphs: Add, Delete, FavoriteStar (outline), FavoriteStarFill.
    protected const string AddGlyph = "\uE710";
    protected const string DeleteGlyph = "\uE74D";
    protected const string StarGlyph = "\uE734";
    protected const string StarFillGlyph = "\uE735";

    protected readonly DomainInstrument Instrument;
    private readonly Action _refresh;

    protected MembershipCommand(DomainInstrument instrument, Action refresh)
    {
        Instrument = instrument;
        _refresh = refresh;
    }

    protected abstract void Apply();

    public override ICommandResult Invoke()
    {
        Apply();
        _refresh();
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class AddToWatchlistCommand : MembershipCommand
{
    public AddToWatchlistCommand(DomainInstrument instrument, Action refresh) : base(instrument, refresh)
    {
        Name = "Add to Watchlist";
        Icon = new IconInfo(AddGlyph);
    }

    protected override void Apply() => WatchlistStore.Instance.AddToWatchlist(Instrument);
}

internal sealed partial class RemoveFromWatchlistCommand : MembershipCommand
{
    public RemoveFromWatchlistCommand(DomainInstrument instrument, Action refresh) : base(instrument, refresh)
    {
        Name = "Remove from Watchlist";
        Icon = new IconInfo(DeleteGlyph);
    }

    protected override void Apply() => WatchlistStore.Instance.RemoveFromWatchlist(Instrument.Symbol);
}

internal sealed partial class AddToFavoritesCommand : MembershipCommand
{
    public AddToFavoritesCommand(DomainInstrument instrument, Action refresh) : base(instrument, refresh)
    {
        Name = "Add to Favorites";
        Icon = new IconInfo(StarGlyph);
    }

    protected override void Apply() => WatchlistStore.Instance.AddToFavorites(Instrument);
}

internal sealed partial class RemoveFromFavoritesCommand : MembershipCommand
{
    public RemoveFromFavoritesCommand(DomainInstrument instrument, Action refresh) : base(instrument, refresh)
    {
        Name = "Remove from Favorites";
        Icon = new IconInfo(StarFillGlyph);
    }

    protected override void Apply() => WatchlistStore.Instance.RemoveFromFavorites(Instrument.Symbol);
}

// Star/unstar in place — used as the Ctrl+Enter secondary on the Watchlist screen so the user can
// favorite a tracked instrument without re-searching. Name/icon reflect the current state.
internal sealed partial class ToggleFavoriteCommand : MembershipCommand
{
    public ToggleFavoriteCommand(DomainInstrument instrument, Action refresh) : base(instrument, refresh)
    {
        var isFavorite = WatchlistStore.Instance.IsFavorite(instrument.Symbol);
        Name = isFavorite ? "Remove from Favorites" : "Add to Favorites";
        Icon = new IconInfo(isFavorite ? StarFillGlyph : StarGlyph);
    }

    protected override void Apply()
    {
        if (WatchlistStore.Instance.IsFavorite(Instrument.Symbol))
            WatchlistStore.Instance.RemoveFromFavorites(Instrument.Symbol);
        else
            WatchlistStore.Instance.AddToFavorites(Instrument);
    }
}
