using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Archon.Core.Sources;

/// <summary>A file selected for analysis, with its language decided by extension.</summary>
public sealed record SourceFile(string Path, string Language)
{
    public string Extension => System.IO.Path.GetExtension(Path);
}

/// <summary>A parsed C# file. <see cref="Tree"/> is always present; parse errors are tolerated.</summary>
public sealed record ParsedCSharp(SyntaxTree Tree, SyntaxNode Root);

/// <summary>
/// A parsed T-SQL file. <see cref="Fragment"/> is <c>null</c> when the text did not parse, in
/// which case <see cref="Errors"/> explains why and SQL rules are skipped for that file.
/// </summary>
public sealed record ParsedSql(TSqlFragment? Fragment, IReadOnlyList<ParseError> Errors);

/// <summary>
/// Parses each file at most once per content revision and hands the result to every rule that
/// needs it. This is the whole reason many rules cost little more than one: a saved file is
/// re-read and re-parsed a single time regardless of how many rules consume it.
///
/// A syntax tree costs several times what its source does, so the cache is bounded and evicts the
/// least recently used file once it is full: a long-lived process over a large repository would
/// otherwise grow until it held every file it had ever touched. Text registered by an editor is
/// never evicted, because it is the only copy — re-reading that file would silently substitute
/// what is on disk for what the user is looking at.
/// </summary>
public sealed class SourceCache
{
    /// <summary>Files held before eviction begins. Roughly 200 MB of trees for average C# files.</summary>
    public const int DefaultCapacity = 2048;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;
    private long _clock;

    public SourceCache(int capacity = DefaultCapacity) => _capacity = Math.Max(16, capacity);

    private sealed class Entry
    {
        public string Text = "";

        /// <summary>Set for editor-supplied text, which has no copy on disk to fall back to.</summary>
        public bool Pinned;

        public long LastUsed;
        public ParsedCSharp? CSharp;
        public ParsedSql? Sql;
    }

    /// <summary>Number of files currently held, exposed for diagnostics.</summary>
    public int Count => _entries.Count;

    /// <summary>Drops a file so the next request re-reads it from disk.</summary>
    public void Invalidate(string path) => _entries.TryRemove(path, out _);

    public void Clear() => _entries.Clear();

    /// <summary>
    /// Registers in-memory text for a file, used when an editor holds unsaved changes. The text
    /// is treated exactly like file content, so rules never need to know the difference.
    /// </summary>
    public void SetText(string path, string text)
    {
        _entries.AddOrUpdate(
            path,
            _ => NewEntry(text, pinned: true),
            (_, existing) =>
            {
                // Comparing the text directly beats hashing it: ordinal equality rejects on length
                // before it reads a character, and this runs on every keystroke.
                if (existing.Pinned && string.Equals(existing.Text, text, StringComparison.Ordinal))
                {
                    Touch(existing);
                    return existing;
                }
                return NewEntry(text, pinned: true);
            });
    }

    public string? GetText(string path) => Load(path)?.Text;

    public ParsedCSharp? GetCSharp(string path)
    {
        Entry? entry = Load(path);
        if (entry is null)
        {
            return null;
        }
        if (entry.CSharp is null)
        {
            lock (entry)
            {
                if (entry.CSharp is null)
                {
                    SyntaxTree tree = CSharpSyntaxTree.ParseText(entry.Text, path: path);
                    entry.CSharp = new ParsedCSharp(tree, tree.GetRoot());
                }
            }
        }
        return entry.CSharp;
    }

    public ParsedSql? GetSql(string path)
    {
        Entry? entry = Load(path);
        if (entry is null)
        {
            return null;
        }
        if (entry.Sql is null)
        {
            lock (entry)
            {
                if (entry.Sql is null)
                {
                    var parser = new TSql150Parser(initialQuotedIdentifiers: true);
                    using var reader = new StringReader(entry.Text);
                    TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);
                    entry.Sql = errors.Count > 0
                        ? new ParsedSql(null, errors.ToList())
                        : new ParsedSql(fragment, Array.Empty<ParseError>());
                }
            }
        }
        return entry.Sql;
    }

    private Entry? Load(string path)
    {
        if (_entries.TryGetValue(path, out Entry? cached))
        {
            Touch(cached);
            return cached;
        }
        try
        {
            Entry added = _entries.GetOrAdd(path, key => NewEntry(File.ReadAllText(key), pinned: false));
            Touch(added);
            EvictIfFull();
            return added;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Entry NewEntry(string text, bool pinned) =>
        new() { Text = text, Pinned = pinned, LastUsed = Interlocked.Increment(ref _clock) };

    private void Touch(Entry entry) => entry.LastUsed = Interlocked.Increment(ref _clock);

    /// <summary>
    /// Drops the coldest unpinned files once the cache is over capacity, in one batch so that a
    /// steady stream of new files does not evict on every single read.
    /// </summary>
    private void EvictIfFull()
    {
        if (_entries.Count <= _capacity)
        {
            return;
        }

        int target = _capacity * 9 / 10;
        List<KeyValuePair<string, Entry>> candidates = _entries
            .Where(pair => !pair.Value.Pinned)
            .OrderBy(pair => pair.Value.LastUsed)
            .Take(Math.Max(0, _entries.Count - target))
            .ToList();

        foreach (KeyValuePair<string, Entry> candidate in candidates)
        {
            _entries.TryRemove(candidate);
        }
    }
}
