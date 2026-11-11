using System.Globalization;

namespace WinNewsWire.Core;

/// <summary>Localized, case-insensitive comparison with numeric ordering, like localizedStandardCompare.</summary>
public sealed class LocalizedStandardComparer : IComparer<string>
{
    public static LocalizedStandardComparer Shared { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            bool numberX = char.IsAsciiDigit(x[i]), numberY = char.IsAsciiDigit(y[j]);
            int endX = i + 1, endY = j + 1;
            while (endX < x.Length && char.IsAsciiDigit(x[endX]) == numberX) endX++;
            while (endY < y.Length && char.IsAsciiDigit(y[endY]) == numberY) endY++;
            int comparison;
            if (numberX && numberY)
            {
                int startX = i, startY = j;
                while (startX < endX && x[startX] == '0') startX++;
                while (startY < endY && y[startY] == '0') startY++;
                comparison = (endX - startX).CompareTo(endY - startY);
                if (comparison == 0)
                    comparison = x.AsSpan(startX, endX - startX).SequenceCompareTo(y.AsSpan(startY, endY - startY));
            }
            else
            {
                comparison = CultureInfo.CurrentCulture.CompareInfo.Compare(
                    x.AsSpan(i, endX - i), y.AsSpan(j, endY - j), CompareOptions.IgnoreCase);
            }
            if (comparison != 0) return comparison;
            i = endX;
            j = endY;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
