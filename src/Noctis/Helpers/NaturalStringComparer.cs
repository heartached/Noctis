using System.Collections.Generic;

namespace Noctis.Helpers;

/// <summary>
/// Numeric-aware, case-insensitive, culture-invariant string comparison — the order
/// a file manager shows: "2 foo" sorts before "10 foo". Runs of digits are compared
/// by numeric value; everything else per-character after invariant upper-casing.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    private NaturalStringComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                // Bound each digit run, then drop leading zeros (keeping at least
                // one digit) so runs compare by numeric value: longer significant
                // run = larger number, equal lengths compare digit by digit.
                int endX = i; while (endX < x.Length && char.IsDigit(x[endX])) endX++;
                int endY = j; while (endY < y.Length && char.IsDigit(y[endY])) endY++;
                int sigX = i; while (sigX < endX - 1 && x[sigX] == '0') sigX++;
                int sigY = j; while (sigY < endY - 1 && y[sigY] == '0') sigY++;

                int lenX = endX - sigX, lenY = endY - sigY;
                if (lenX != lenY) return lenX - lenY;
                for (int k = 0; k < lenX; k++)
                {
                    int d = x[sigX + k] - y[sigY + k];
                    if (d != 0) return d;
                }

                // Same numeric value ("1" vs "01") — fewer leading zeros first,
                // so the order stays total instead of depending on input order.
                int zerosX = sigX - i, zerosY = sigY - j;
                if (zerosX != zerosY) return zerosX - zerosY;

                i = endX; j = endY;
                continue;
            }

            char cx = char.ToUpperInvariant(x[i]);
            char cy = char.ToUpperInvariant(y[j]);
            if (cx != cy) return cx - cy;
            i++; j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
