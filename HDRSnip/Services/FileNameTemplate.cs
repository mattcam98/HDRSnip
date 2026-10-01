using System.IO;
using System.Text.RegularExpressions;

namespace HDRSnip.Services;

/// <summary>
/// Expands the save-name template from Settings. Tokens are written in braces:
/// <c>{date}</c>, <c>{time}</c>, <c>{year}</c>, <c>{month}</c>, <c>{day}</c>,
/// <c>{hour}</c>, <c>{minute}</c> and <c>{second}</c>. Anything else is literal.
/// </summary>
public static partial class FileNameTemplate
{
    public const string Default = "HDRSnip_{date}_{time}";

    public const string TokenHelp = "{date} {time} {year} {month} {day} {hour} {minute} {second}";

    /// <summary>A file name without extension that is always valid, whatever the template holds.</summary>
    public static string Expand(string? template, DateTime now)
    {
        string name = Token().Replace(template ?? string.Empty, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "date" => now.ToString("yyyyMMdd"),
            "time" => now.ToString("HHmmss"),
            "year" => now.ToString("yyyy"),
            "month" => now.ToString("MM"),
            "day" => now.ToString("dd"),
            "hour" => now.ToString("HH"),
            "minute" => now.ToString("mm"),
            "second" => now.ToString("ss"),
            _ => match.Value
        });

        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        // Windows drops trailing dots and spaces, which would silently change the name.
        name = name.TrimStart().TrimEnd('.', ' ');
        return name.Length == 0 ? Expand(Default, now) : name;
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Token();
}
