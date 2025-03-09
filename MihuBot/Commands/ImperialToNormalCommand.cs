﻿using System.Globalization;
using System.Text.RegularExpressions;

namespace MihuBot.Commands;

public sealed partial class ImperialToNormalCommand : CommandBase
{
    public override string Command => "imperialtonormal";
    public override string[] Aliases => ["normaltoimperial"];

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        bool imperialToNormal = ctx.Command == "imperialtonormal";

        string[] parts = ctx.ArgumentString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var quantifierMatch = QuantifierRegex().Match(ctx.ArgumentString);
        if (!quantifierMatch.Success)
            return;

        decimal value = 1;
        if (quantifierMatch.Length != 0)
        {
            var quantifier = quantifierMatch.Value.Replace(',', '.');
            if (!decimal.TryParse(quantifier, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return;
        }

        string type = ctx.ArgumentString.Substring(quantifierMatch.Length).Trim().ToLowerInvariant();
        if (type.Length == 0)
            return;

        Func<decimal, decimal> conversion = null;
        string format = null;
        bool appendS = true;

        if (imperialToNormal)
        {
            switch (type)
            {
                case "f":
                    if (value is > 0 and < 10) goto case "feet";
                    else goto case "fahrenheit";

                case "fahrenheit":
                    conversion = v => (v - 32) / 1.8m;
                    format = "°C";
                    appendS = false;
                    break;

                case "ft": case "feet":
                    conversion = v => v * 0.3048m;
                    format = "meter";
                    break;

                case "i": case "inch": case "inchs": case "inches":
                    conversion = v => v * 0.0254m;
                    format = "meter";
                    break;

                case "gln": case "glns": case "gallon": case "gallons":
                    conversion = v => v * 3.785411784m;
                    format = "liter";
                    break;

                case "oz": case "ounces":
                    conversion = v => v * 0.02957352965m;
                    format = "liter";
                    break;
            }
        }
        else
        {
            switch (type)
            {
                case "m": case "ms": case "mtr": case "mtrs": case "meter": case "meters":
                    conversion = v => v * 117.64705882353m;
                    format = "barleycorn";
                    break;

                case "l": case "ltr": case "ltrs": case "liter": case "liters":
                    conversion = v => v * 0.2641720524m;
                    format = "gallon";
                    break;

                case "c": case "celsius":
                    conversion = v => (v * 1.8m) + 32;
                    format = " fahrenheit";
                    appendS = false;
                    break;
            }
        }

        if (conversion is null)
            return;

        value = conversion(value);

        if (appendS && value % 1 != 0)
            format += 's';

        await ctx.ReplyAsync($"{decimal.Round(value, 2):N2} {format}");
    }

    [GeneratedRegex(@"^-?[\d\.,]*")]
    private static partial Regex QuantifierRegex();
}
