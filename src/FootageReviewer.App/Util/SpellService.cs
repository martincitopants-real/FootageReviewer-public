using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using WeCantSpell.Hunspell;

namespace FootageReviewer.App.Util;

/// <summary>
/// Lazy Hunspell spell checker over an embedded en_US dictionary. Loads off the UI thread; until ready,
/// <see cref="Check"/> treats every word as correct (so nothing is wrongly flagged during startup). A
/// user allowlist (right-click → "Add to dictionary") suppresses domain words (game names, usernames).
/// </summary>
public sealed class SpellService
{
    private WordList? _dict;
    private volatile bool _ready;
    private bool _loading;
    private readonly object _gate = new();           // guards _allow (read on bg recheck, written on UI)
    private readonly HashSet<string> _allow = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the UI thread once the dictionary has loaded.</summary>
    public event Action? Ready;

    public bool IsReady => _ready;

    public void SetAllowlist(IEnumerable<string>? words)
    {
        lock (_gate)
        {
            _allow.Clear();
            if (words == null) return;
            foreach (var w in words)
                if (!string.IsNullOrWhiteSpace(w)) _allow.Add(w.Trim());
        }
    }

    /// <summary>A point-in-time snapshot of the allowlist (safe to enumerate).</summary>
    public IReadOnlyCollection<string> Allowlist
    {
        get { lock (_gate) return _allow.ToArray(); }
    }

    public void AddToAllowlist(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        lock (_gate) _allow.Add(word.Trim());
    }

    public void BeginLoad()
    {
        if (_ready || _loading) return;
        _loading = true;
        Task.Run(() =>
        {
            try
            {
                var asm = typeof(SpellService).Assembly;
                using var dic = asm.GetManifestResourceStream("FootageReviewer.App.Dictionaries.en_US.dic");
                using var aff = asm.GetManifestResourceStream("FootageReviewer.App.Dictionaries.en_US.aff");
                if (dic == null || aff == null) return;
                var wl = WordList.CreateFromStreams(dic, aff);
                _dict = wl;
                _ready = true;
                Dispatcher.UIThread.Post(() => Ready?.Invoke());
            }
            catch { /* leave spell-check disabled if the dictionary can't load */ }
        });
    }

    /// <summary>True if the word is spelled correctly (or the checker isn't ready / word is allowlisted).</summary>
    public bool Check(string word)
    {
        if (!_ready || _dict == null) return true;
        if (string.IsNullOrEmpty(word)) return true;
        lock (_gate) { if (_allow.Contains(word)) return true; }
        try { return _dict.Check(word); }   // WordList is immutable → safe to call off the UI thread
        catch { return true; }
    }

    /// <summary>Up to <paramref name="max"/> spelling suggestions for a word.</summary>
    public IList<string> Suggest(string word, int max = 7)
    {
        if (!_ready || _dict == null || string.IsNullOrWhiteSpace(word)) return Array.Empty<string>();
        try { return _dict.Suggest(word).Take(max).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
