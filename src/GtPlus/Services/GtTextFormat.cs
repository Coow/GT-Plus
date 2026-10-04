using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GtPlus.Services;

//https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings)
public static class GtTextFormat
{
    private static readonly Regex Placeholder = new(@"\{0(:(?<fmt>[^{}]*))?\}", RegexOptions.Compiled);

    public static string Resolve(string text) => Resolve(text, DateTime.Now);

    public static string Resolve(string text, DateTime now)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{0", StringComparison.Ordinal))
            return text;

        return Placeholder.Replace(text, m =>
        {
            var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            try
            {
                return now.ToString(fmt, CultureInfo.CurrentCulture);
            }
            catch (FormatException)
            {
                return m.Value;
            }
        });
    }
}
