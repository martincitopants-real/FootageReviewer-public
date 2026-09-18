using System;
using System.Collections.Generic;

namespace FootageReviewer.App.Util;

/// <summary>
/// Orders strings the way a person reads them: digit runs compare numerically, so "Day 2" sorts
/// before "Day 10" instead of after it. Ordinal comparison scrambles any folder or clip naming that
/// counts without zero-padding, which is most of them.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                // Compare the whole digit run as a number, ignoring leading zeros.
                var si = i; var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                var da = a.AsSpan(si, i - si).TrimStart('0');
                var db = b.AsSpan(sj, j - sj).TrimStart('0');
                if (da.Length != db.Length) return da.Length - db.Length;
                var cmp = da.SequenceCompareTo(db);
                if (cmp != 0) return cmp;
                continue;
            }

            var ca = char.ToUpperInvariant(a[i]);
            var cb = char.ToUpperInvariant(b[j]);
            if (ca != cb) return ca - cb;
            i++; j++;
        }
        return (a.Length - i) - (b.Length - j);
    }
}
