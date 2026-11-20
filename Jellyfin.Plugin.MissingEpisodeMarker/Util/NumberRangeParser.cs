using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Util;

/// <summary>
/// Parses lists like "1,3,5-7" into sets of numbers.
/// </summary>
public static class NumberRangeParser
{
    private const int MaxRangeSize = 10000;

    private static readonly char[] Separators = new[] { ',', ';', ' ', '\n', '\r', '\t' };

    /// <summary>Parses a comma separated list of numbers and ranges.</summary>
    /// <param name="text">The text, e.g. "26, 97-106".</param>
    /// <returns>The numbers. Invalid parts are ignored.</returns>
    public static HashSet<int> Parse(string? text)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        // Normalize "5 - 7" to "5-7" so spaces can be used as separators too.
        var compact = text.Replace(" - ", "-", StringComparison.Ordinal).Replace(" -", "-", StringComparison.Ordinal).Replace("- ", "-", StringComparison.Ordinal);
        foreach (var rawPart in compact.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            AddPart(rawPart.Trim(), result);
        }

        return result;
    }

    /// <summary>Adds a single "5" or "5-7" part to a set.</summary>
    /// <param name="part">The part.</param>
    /// <param name="target">The target set.</param>
    public static void AddPart(string part, HashSet<int> target)
    {
        // Accept normal hyphen and en/em dashes.
        var normalized = part.Replace('–', '-').Replace('—', '-');
        var dash = normalized.Length > 1 ? normalized.IndexOf('-', 1) : -1;
        if (dash > 0)
        {
            if (int.TryParse(normalized.AsSpan(0, dash).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                && int.TryParse(normalized.AsSpan(dash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                && end >= start
                && end - start <= MaxRangeSize)
            {
                for (var i = start; i <= end; i++)
                {
                    target.Add(i);
                }
            }

            return;
        }

        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
        {
            target.Add(single);
        }
    }
}
