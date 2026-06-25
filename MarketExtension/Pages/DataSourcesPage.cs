using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// A plain informational screen explaining where market data comes from and the keys-stay-on-your-machine
// model. Reached from the Markets hub. Pure static Markdown — no data fetch, no providers, no lifecycle.
internal sealed partial class DataSourcesPage : ContentPage
{
    private readonly MarkdownContent _content = new(
        """
        # Data Sources
        
        This app shows market data for informational purposes only. It is **not
        financial advice** and should not be relied on for trading or investment
        decisions. Quotes, rates, and charts may be **delayed or inaccurate**
        depending on the source.

        The app is **not affiliated with,
        endorsed by, or connected to** Finnhub, Twelve Data, Frankfurter, the
        European Central Bank, or any data provider.

        ## Where the data comes from

        Quotes, currency rates, and charts come from independent third-party
        providers. What's available depends on which keys you've added in
        Settings and the plan attached to each.

        Some data, such as currency reference rates, can come from a free
        public source that needs no key.

        ## Your keys, your data

        The keys are yours. You create them with the provider; this app keeps
        them on your own machine and sends each one
        **directly to the provider it belongs to** — nothing is routed through
        a server, because this app doesn't run one.

        Any agreement governing a key's use is **between you and that
        provider**. This app is just the surface that displays the result, and
        you are responsible for using your keys in line with whatever terms you
        agreed to when you created them.

        ## What this app does and doesn't do

        **Does:** send your queries straight to the provider you've configured,
        using your own key, and briefly caches results so it isn't making redundant requests.

        **Doesn't:** run a server, upload or proxy your keys, retain provider
        data, or share anything with anyone but you.
        """);

    public DataSourcesPage()
    {
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
        Title = "Data Sources";
        Name = "Open";
    }

    public override IContent[] GetContent() => [_content];
}
