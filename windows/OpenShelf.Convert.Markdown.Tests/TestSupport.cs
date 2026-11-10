using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using OpenShelf.Convert.Markdown;

namespace OpenShelf.Convert.Markdown.Tests;

/// <summary>A fresh folder deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-md-test-" + Guid.NewGuid().ToString("N")[..10]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name, string text)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(p => System.IO.Path.GetFileName(p)!).OrderBy(n => n).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public static class Samples
{
    /// <summary>Pandoc's own sample document, attached to issue #5: the conversion that printed its Markdown source as plain text.</summary>
    public const string Pandoc = """
        An h1 header
        ============

        Paragraphs are separated by a blank line.

        2nd paragraph. *Italic*, **bold**, and `monospace`. Itemized lists
        look like:

          * this one
          * that one
          * the other one

        > Block quotes are
        > written like so.
        >
        > They can span multiple paragraphs,
        > if you like.

        Use 3 dashes for an em-dash. Use 2 dashes for ranges (ex., "it's all
        in chapters 12--14"). Three dots ... will be converted to an ellipsis.

        An h2 header
        ------------

         1. first item
         2. second item

        Code:

            # Let me re-iterate ...
            for i in 1 .. 10 { do-something(i) }

        ~~~python
        import time
        ~~~

        ### An h3 header ###

         1. First, get these ingredients:

              * carrots
              * celery

         2. Dump everything in the pot and follow
            this algorithm:

                find wooden spoon
                stir

            Do not bump wooden spoon or it will fall.

        Here's a link to [a website](http://foo.bar), to a [section heading in the current
        doc](#an-h2-header). Here's a footnote [^1].

        [^1]: Footnote text goes here.

        size  material      color
        ----  ------------  ------------
        9     leather       brown
        10    hemp canvas   natural

        Table: Shoes, their sizes, and what they're made of

        --------  -----------------------
        keyword   text
        --------  -----------------------
        red       Sunsets, apples, and
                  other red or reddish
                  things.

        green     Leaves, grass, frogs
        --------  -----------------------

        ***

        apples
          : Good for making applesauce.
        oranges
          : Citrus!

        | Line one
        |   Line too

        Inline math: $\omega = d\phi / dt$.

        $$I = \int \rho R^{2} dV$$

        Escapes: \`foo\`, \*bar\*.
        """;

    /// <summary>Every construct once, for golden checks of the renderers.</summary>
    public static readonly string Everything = """
        ---
        title: Everything
        ---

        # Top {#top}

        Text with *emphasis*, **strong**, ~~gone~~, `code`, a [link](https://example.com "Tip"), "quotes" and 1--2 -- 3---4...
        a soft break and a hard break@@
        after it. Math $x^2$ and a footnote.[^n] Also ^[an inline note].

        > quoted

        - [ ] open
        - [x] done

        3. three
        4. four

        | Left | Right |
        |:-----|------:|
        | a    | 1     |

        Term
        : Definition

        | line one
        |   line two

        $$a = \frac{b}{2}$$

        ```python
        print("hi <&>")
        ```

        ---

        [^n]: The note.
        """.Replace("@@", "  ");

    public static string Dump(MarkdownText text) => string.Join("|", text.Select(Dump));

    public static string Dump(MarkdownInline inline) => inline switch
    {
        MarkdownInline.Text t => $"'{t.Value}'",
        MarkdownInline.Emphasis e => $"em({Dump(e.Content)})",
        MarkdownInline.Strong s => $"strong({Dump(s.Content)})",
        MarkdownInline.Strikethrough s => $"del({Dump(s.Content)})",
        MarkdownInline.Code c => $"code({c.Value})",
        MarkdownInline.Math m => $"math({m.Tex})",
        MarkdownInline.Link l => $"link({l.Url},{l.Title},{Dump(l.Content)})",
        MarkdownInline.Image i => $"image({i.Source},{i.Title},{Dump(i.Alt)})",
        MarkdownInline.Footnote f => $"fn({f.Number})",
        MarkdownInline.LineBreak => "br",
        _ => "soft",
    };
}

public static class Zips
{
    public static List<string> Names(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    public static byte[]? Entry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static string Text(string path, string name) => Encoding.UTF8.GetString(Entry(path, name) ?? throw new InvalidOperationException($"missing {name}"));

    public static XDocument Xml(string path, string name) => XDocument.Parse(Text(path, name));
}
