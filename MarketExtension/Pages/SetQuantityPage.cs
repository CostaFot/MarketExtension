using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// The quantity editor for one instrument — the single place to add a holding to the portfolio or change
// how much of it is held. Opened from the symbol detail page's command bar: "Add to Portfolio" when the
// instrument isn't held yet, "Edit holding" when it is. The body is a single adaptive-card FormContent
// with one number input; Save writes the holding to PortfolioStore and navigates back to the detail page,
// whose PortfolioStore subscription then flips its command bar (Add → Edit/Remove).
//
// The single-FormContent auto-focus quirk that plagues SymbolDetailPage (the host focuses the card's first
// focusable element) is HARMLESS here — even helpful: it drops the cursor straight into the quantity field.
internal sealed partial class SetQuantityPage : ContentPage
{
    private const string AddGlyph = "\uE710"; // Segoe MDL2 Add
    private const string EditGlyph = "\uE70F"; // Segoe MDL2 Edit

    private readonly QuantityForm _form;

    public SetQuantityPage(DomainInstrument instrument)
    {
        var editing = PortfolioStore.Instance.Contains(instrument.Symbol);
        _form = new QuantityForm(instrument);
        // Stable, unique per symbol — also satisfies the non-empty Id a page used as a command needs.
        Id = $"com.costafotiadis.market.portfolio.setquantity.{instrument.Symbol}";
        Icon = new IconInfo(editing ? EditGlyph : AddGlyph);
        Title = editing ? $"Edit {instrument.Symbol} holding" : $"Add {instrument.Symbol} to portfolio";
        Name = editing ? "Edit holding" : "Add to Portfolio";
    }

    public override IContent[] GetContent() => [_form];

    // The adaptive-card form: a number input prefilled with the current holding (0 when adding). SubmitForm
    // validates and writes to the store. FormContent : BaseObservable, so this renders in place like the
    // chart card (TemplateJson + DataJson), the same mechanism used across the app.
    private sealed partial class QuantityForm : FormContent
    {
        private readonly DomainInstrument _instrument;

        public QuantityForm(DomainInstrument instrument)
        {
            _instrument = instrument;
            TemplateJson = Template;
            DataJson = BuildData();
        }

        private string BuildData()
        {
            PortfolioStore.Instance.TryGetQuantity(_instrument.Symbol, out var qty);
            var data = new JsonObject
            {
                ["symbol"] = _instrument.Symbol,
                ["name"] = _instrument.Name,
                // Pre-fill with the current holding (0 for a new one). Input.Number binds a number; keep it
                // decimal so the prefill round-trips exactly (no binary-float artifacts).
                ["quantity"] = qty,
            };
            return data.ToJsonString();
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            if (!TryReadQuantity(inputs, out var quantity))
                return Error("Enter a number greater than 0.");
            if (quantity <= 0m)
                return Error("Quantity must be greater than 0. To remove a holding, use Remove from Portfolio.");

            PortfolioStore.Instance.SetPosition(_instrument, quantity);
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = $"Set {_instrument.Symbol} to {quantity.ToString("0.########", CultureInfo.InvariantCulture)}",
                Result = CommandResult.GoBack(), // back to the detail page; its subscription refreshes the bar
            });
        }

        private static CommandResult Error(string message) =>
            CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });

        // Pull the "quantity" field out of the host's inputs JSON (e.g. {"quantity":"10.5"} — the value may
        // arrive as a JSON string or number depending on the host). Parse invariant first, then the current
        // culture as a fallback (a localized host may format with a comma decimal separator).
        private static bool TryReadQuantity(string inputs, out decimal quantity)
        {
            quantity = 0m;
            if (string.IsNullOrWhiteSpace(inputs))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(inputs);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("quantity", out var field))
                    return false;

                var raw = field.ValueKind == JsonValueKind.String ? field.GetString() : field.GetRawText();
                if (string.IsNullOrWhiteSpace(raw))
                    return false;

                return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out quantity)
                    || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out quantity);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // Static card structure; the symbol/name/prefilled quantity bind from DataJson. One Save action.
        private const string Template = """
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": "${symbol}", "size": "ExtraLarge", "weight": "Bolder", "wrap": true },
            { "type": "TextBlock", "text": "${name}", "isSubtle": true, "spacing": "None", "wrap": true },
            { "type": "Input.Number", "id": "quantity", "label": "Quantity held", "value": ${quantity}, "min": 0, "isRequired": true, "errorMessage": "Enter a quantity greater than 0" }
          ],
          "actions": [
            { "type": "Action.Submit", "title": "Save", "data": { "action": "save" } }
          ]
        }
        """;
    }
}
