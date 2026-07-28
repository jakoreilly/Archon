using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
/// </summary>
public sealed class SourceCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public string Hash = "";
        public string Text = "";
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
        string hash = Hash(text);
        _entries.AddOrUpdate(
            path,
            _ => new Entry { Hash = hash, Text = text },
            (_, existing) => existing.Hash == hash ? existing : new Entry { Hash = hash, Text = text });
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
            SyntaxTree tree = CSharpSyntaxTree.ParseText(entry.Text, path: path);
            entry.CSharp = new ParsedCSharp(tree, tree.GetRoot());
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
            var parser = new TSql150Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(entry.Text);
            TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);
            entry.Sql = errors.Count > 0
                ? new ParsedSql(null, errors.ToList())
                : new ParsedSql(fragment, Array.Empty<ParseError>());
        }
        return entry.Sql;
    }

    private Entry? Load(string path)
    {
        if (_entries.TryGetValue(path, out Entry? cached))
        {
            return cached;
        }
        try
        {
            string text = File.ReadAllText(path);
            var entry = new Entry { Hash = Hash(text), Text = text };
            return _entries.GetOrAdd(path, entry);
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

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
