using System.Globalization;

namespace DeltaForceMode.Domain;

public static class MoneyFormatter
{
    public static string FormatCents(long cents)
    {
        return cents % 100 == 0
            ? $"${(cents / 100).ToString("N0", CultureInfo.InvariantCulture)}"
            : $"${(cents / 100m).ToString("N2", CultureInfo.InvariantCulture)}";
    }
}
