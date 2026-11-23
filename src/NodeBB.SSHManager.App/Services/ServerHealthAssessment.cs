using System.Globalization;
using System.Text.RegularExpressions;

namespace NodeBB.SSHManager.Services;

public static class ServerHealthAssessment
{
    private static readonly Regex DiskUsagePattern = new(@"\((\d{1,3})%\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LoadPattern = new(@"^\s*(\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Evaluate(string loadAverage, string disk, int? memoryUsagePercent, int cpuCount,
        int? swapUsagePercent = null, string diskInodes = "", int? cpuUsagePercent = null)
    {
        var alerts = new List<string>();
        var diskPercentages = DiskUsagePattern.Matches(disk ?? string.Empty)
            .Select(match => int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1)
            .Where(value => value is >= 0 and <= 100);
        if (diskPercentages.Any(value => value >= 90))
            alerts.Add("אחת ממחיצות הדיסק בשימוש של 90% ומעלה.");

        if (cpuUsagePercent is >= 90 and <= 100)
            alerts.Add("ניצול המעבד גבוה (90% ומעלה).");

        if (memoryUsagePercent is >= 90 and <= 100)
            alerts.Add("השימוש בזיכרון הגיע ל-90% ומעלה.");

        if (swapUsagePercent is >= 90 and <= 100)
            alerts.Add("השימוש בזיכרון ההחלפה (Swap) הגיע ל-90% ומעלה.");

        var inodePercentages = DiskUsagePattern.Matches(diskInodes ?? string.Empty)
            .Select(match => int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1)
            .Where(value => value is >= 0 and <= 100);
        if (inodePercentages.Any(value => value >= 90))
            alerts.Add("השימוש במספר קובצי inode באחת המחיצות הגיע ל-90% ומעלה.");

        var loadMatch = LoadPattern.Match(loadAverage ?? string.Empty);
        if (loadMatch.Success &&
            double.TryParse(loadMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var load) &&
            double.IsFinite(load) && cpuCount > 0 && load >= cpuCount * 1.5)
            alerts.Add($"עומס המערכת גבוה (ממוצע דקה של {load.ToString("0.##", CultureInfo.InvariantCulture)} על {cpuCount} ליבות).");

        return alerts;
    }
}
