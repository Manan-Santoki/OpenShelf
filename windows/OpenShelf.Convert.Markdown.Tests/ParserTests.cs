namespace OpenShelf.Convert.Markdown.Tests;

using B = MarkdownBlock<MarkdownText>;

/// <summary>Port of MarkdownParserTests (MarkdownConversionTests.swift).</summary>
public class ParserTests
{
    private readonly MarkdownDocument _document = MarkdownDocument.Parse(Samples.Pandoc);

    private static MarkdownText T(string text) => MarkdownText.OfString(text);

    private static MarkdownText Inlines(string source) =>
        MarkdownDocument.Parse(source).Blocks.FirstOrDefault() is B.Paragraph p ? p.Text : new MarkdownText();

    [Fact]
    public void SetextAndAtxHeadingsGetPandocIdentifiers()
    {
        var headings = _document.Blocks.OfType<B.Heading>().ToList();
        Assert.Equal([1, 2, 3], headings.Select(h => h.Level));
        Assert.Equal(["An h1 header", "An h2 header", "An h3 header"], headings.Select(h => h.Text.PlainText()));
        Assert.Equal(["an-h1-header", "an-h2-header", "an-h3-header"], headings.Select(h => h.Id));
    }

    [Fact]
    public void InlineStylesAndSmartPunctuation()
    {
        var text = Assert.IsType<B.Paragraph>(_document.Blocks[2]).Text;
        Assert.Contains(new MarkdownInline.Emphasis(T("Italic")), text);
        Assert.Contains(new MarkdownInline.Strong(T("bold")), text);
        Assert.Contains(new MarkdownInline.Code("monospace"), text);
        var dashes = _document.Blocks.OfType<B.Paragraph>().Select(p => p.Text.PlainText()).First(t => t.StartsWith("Use 3 dashes"));
        // Kept as typed unless smart punctuation is asked for.
        Assert.DoesNotContain("“", dashes);
        Assert.Contains("Three dots ...", dashes);
        var smart = MarkdownDocument.Parse(Samples.Pandoc, smartPunctuation: true).Blocks.OfType<B.Paragraph>().Select(p => p.Text.PlainText()).First(t => t.StartsWith("Use 3 dashes"));
        Assert.Contains("“it’s all in chapters 12–14”", smart);
        Assert.Contains("Three dots …", smart);
    }

    [Fact]
    public void IndentedBulletsQuotesAndNestedLists()
    {
        var lists = _document.Blocks.OfType<B.ListBlock>().ToList();
        var bullets = lists.First().List;
        var nested = lists.Last().List;
        Assert.True(!bullets.Ordered && bullets.Tight && bullets.Items.Count == 3);
        Assert.True(nested.Ordered && !nested.Tight && nested.Items.Count == 2);
        var inner = Assert.IsType<B.ListBlock>(nested.Items[0].Last()).List;
        Assert.Equal(2, inner.Items.Count);
        // Item 2 continues lazily, then holds indented code and a paragraph.
        Assert.Equal(3, nested.Items[1].Count);
        Assert.Equal(new B.Code("find wooden spoon\nstir", null), nested.Items[1][1]);

        var quoted = _document.Blocks.OfType<B.Quote>().First();
        Assert.Equal(2, quoted.Blocks.Count);
    }

    [Fact]
    public void CodeBlocksKeepTheirTextAndLanguage()
    {
        Assert.Contains(new B.Code("# Let me re-iterate ...\nfor i in 1 .. 10 { do-something(i) }", null), _document.Blocks);
        Assert.Contains(new B.Code("import time", "python"), _document.Blocks);
        Assert.Equal("swift", MarkdownParser.LanguageFromInfo("{.swift .numberLines}"));
        Assert.Null(MarkdownParser.LanguageFromInfo(""));
    }

    [Fact]
    public void LinksFootnotesAndEscapes()
    {
        var text = _document.Blocks.OfType<B.Paragraph>().Select(p => p.Text).First(t => t.PlainText().StartsWith("Here's a link"));
        Assert.Contains(new MarkdownInline.Link("http://foo.bar", null, T("a website")), text);
        Assert.Contains(text, i => i is MarkdownInline.Link l && l.Url == "#an-h2-header");
        Assert.Contains(new MarkdownInline.Footnote(1), text);
        Assert.Single(_document.Footnotes);
        Assert.Equal([new B.Paragraph(T("Footnote text goes here."))], _document.Footnotes[0]);
        Assert.Equal(new B.Paragraph(T("Escapes: `foo`, *bar*.")), _document.Blocks.Last());
    }

    [Fact]
    public void PandocSimpleAndMultilineTables()
    {
        var tables = _document.Blocks.OfType<B.Table>().Select(t => t.Value).ToList();
        Assert.Equal(2, tables.Count);
        Assert.Equal(["size", "material", "color"], tables[0].Header!.Select(c => c.PlainText()));
        Assert.Equal(["10", "hemp canvas", "natural"], tables[0].Rows.Last().Select(c => c.PlainText()));
        Assert.Equal("Shoes, their sizes, and what they're made of", tables[0].Caption!.PlainText());
        Assert.Equal(2, tables[1].Rows.Count);
        Assert.Equal("Sunsets, apples, and other red or reddish things.", tables[1].Rows[0][1].PlainText());
        Assert.Equal(2, tables[1].Widths!.Count);
    }

    /// <summary>Pandoc itself runs "oranges" into the definition above it.</summary>
    [Fact]
    public void CompactDefinitionListsKeepEachTerm()
    {
        var definitions = _document.Blocks.OfType<B.Definitions>().First().Items;
        Assert.Equal(["apples", "oranges"], definitions.Select(d => d.Term.PlainText()));
        Assert.Equal([new B.Paragraph(T("Citrus!"))], definitions[1].Definitions.Single());
    }

    [Fact]
    public void RuleLineBlockAndMath()
    {
        Assert.Contains(new B.Rule(), _document.Blocks);
        var lines = _document.Blocks.OfType<B.Lines>().Single();
        Assert.Equal(["Line one", "  Line too"], lines.Items.Select(l => l.PlainText()));
        Assert.Contains(new B.Math(@"I = \int \rho R^{2} dV"), _document.Blocks);
        Assert.Equal("I = ∫ ρ R² dV", MarkdownMath.Unicode(@"I = \int \rho R^{2} dV"));
        Assert.Equal("(a+b)/2", MarkdownMath.Unicode(@"\frac{a+b}{2}"));
    }

    [Fact]
    public void GitHubFlavour()
    {
        var document = MarkdownDocument.Parse("""
            | Name | Price |
            |:-----|------:|
            | `a|b` | 1 |
            | Tea \| cake | 2 |

            - [ ] todo
            - [x] done

            ~~gone~~ and https://example.com/a_(b). Also <me@example.com>.
            """);
        var table = Assert.IsType<B.Table>(document.Blocks[0]).Value;
        Assert.Equal([MarkdownAlignment.Left, MarkdownAlignment.Right], table.Alignments);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal([MarkdownText.Of(new MarkdownInline.Code("a|b")), T("1")], table.Rows[0]);
        Assert.Equal([T("Tea | cake"), T("2")], table.Rows[1]);
        var tasks = Assert.IsType<B.ListBlock>(document.Blocks[1]).List;
        Assert.Equal([new B.Paragraph(T("☐ todo")), new B.Paragraph(T("☑ done"))], tasks.Items.Select(i => i.First()));
        var text = Assert.IsType<B.Paragraph>(document.Blocks[2]).Text;
        Assert.Equal(new MarkdownInline.Strikethrough(T("gone")), text.First());
        Assert.Contains(new MarkdownInline.Link("https://example.com/a_(b)", null, T("https://example.com/a_(b)")), text);
        Assert.Contains(new MarkdownInline.Link("mailto:me@example.com", null, T("me@example.com")), text);
    }

    [Fact]
    public void EmphasisEdgeCases()
    {
        Assert.Equal(MarkdownText.Of(new MarkdownInline.Emphasis(MarkdownText.Of(new MarkdownInline.Strong(T("both"))))), Inlines("***both***"));
        Assert.Equal(T("snake_case_name"), Inlines("snake_case_name"));
        Assert.Equal(T("2 * 3 * 4"), Inlines("2 * 3 * 4"));
        Assert.Equal(
            MarkdownText.Of(new MarkdownInline.Strong(MarkdownText.Of(new MarkdownInline.Text("bold "), new MarkdownInline.Emphasis(T("and italic"))))),
            Inlines("**bold *and italic***"));
        // Money isn't math.
        Assert.Equal(T("It costs $5 or $10."), Inlines("It costs $5 or $10."));
    }

    [Fact]
    public void ReferenceLinksImagesAndTitles()
    {
        var document = MarkdownDocument.Parse("""
            ---
            title: "Field notes"
            author: Someone
            ---

            See [the docs][docs] and [Docs] and ![a cat](cat.png "Cat"){width=50%}.

            [docs]: https://example.com/docs "Docs"
            """);
        Assert.Equal("Field notes", document.Title);
        var text = Assert.IsType<B.Paragraph>(document.Blocks.First()).Text;
        Assert.Contains(new MarkdownInline.Link("https://example.com/docs", "Docs", T("the docs")), text);
        Assert.Contains(new MarkdownInline.Link("https://example.com/docs", "Docs", T("Docs")), text);
        Assert.Contains(new MarkdownInline.Image("cat.png", "Cat", T("a cat")), text);
        Assert.Equal(new MarkdownInline.Text("."), text.Last());
    }

    [Fact]
    public void HeadingIdsAreUnique()
    {
        var document = MarkdownDocument.Parse("# Notes\n\n# Notes\n\n## 2. Next steps {#next}\n");
        Assert.Equal(["notes", "notes-1", "next"], document.Blocks.OfType<B.Heading>().Select(h => h.Id));
    }

    [Fact]
    public void PandocTitleBlockAndWindowsLineEndings()
    {
        var document = MarkdownDocument.Parse("% A title\r\n% Author\r\n\r\nBody\r\nmore\r\n");
        Assert.Equal("A title", document.Title);
        Assert.Equal("Body more", Assert.IsType<B.Paragraph>(Assert.Single(document.Blocks)).Text.PlainText());
    }

    [Fact]
    public void InlineFootnotesNumberInOrderOfUse()
    {
        var document = MarkdownDocument.Parse("A^[inline note] and B[^b] and C[^a].\n\n[^a]: First defined.\n[^b]: Second defined.\n");
        Assert.Equal(3, document.Footnotes.Count);
        Assert.Equal("inline note", Assert.IsType<B.Paragraph>(document.Footnotes[0][0]).Text.PlainText());
        Assert.Equal("Second defined.", Assert.IsType<B.Paragraph>(document.Footnotes[1][0]).Text.PlainText());
        Assert.Equal("First defined.", Assert.IsType<B.Paragraph>(document.Footnotes[2][0]).Text.PlainText());
    }

    [Fact]
    public void DisplayMathAcrossLinesAndHtmlComments()
    {
        var document = MarkdownDocument.Parse("<!-- hidden\nstill hidden -->\n\n$$\nx = 1\n$$\n\nAfter <br> break &amp; &copy;\n");
        Assert.Equal(new B.Math("x = 1"), document.Blocks[0]);
        var paragraph = Assert.IsType<B.Paragraph>(document.Blocks[1]);
        Assert.Contains(new MarkdownInline.LineBreak(), paragraph.Text);
        Assert.Contains("& ©", paragraph.Text.PlainText());
    }

    [Fact]
    public void MathUnicodeCoversFractionsRootsAndScripts()
    {
        Assert.Equal("√2", MarkdownMath.Unicode(@"\sqrt{2}"));
        Assert.Equal("x₁ + y²", MarkdownMath.Unicode("x_1 + y^2"));
        Assert.Equal("α ≤ β", MarkdownMath.Unicode(@"\alpha \leq \beta"));
        Assert.Equal("sin", MarkdownMath.Unicode(@"\sin"));
    }
}

public class InlineHtmlTests
{
    private static MarkdownText Inlines(string source) =>
        MarkdownDocument.Parse(source).Blocks.FirstOrDefault() is MarkdownBlock<MarkdownText>.Paragraph p ? p.Text : new MarkdownText();

    private static MarkdownText T(string text) => MarkdownText.OfString(text);

    [Fact]
    public void FormattingTagsKeepTheirLook()
    {
        var text = Inlines("a <u>under</u> <b>bold</b> <i>it</i> x<sup>2</sup> H<sub>2</sub>O <mark>hi</mark> <kbd>Ctrl</kbd> <s>old</s>");
        Assert.Contains(new MarkdownInline.Styled(InlineStyle.Underline, T("under")), text);
        Assert.Contains(new MarkdownInline.Strong(T("bold")), text);
        Assert.Contains(new MarkdownInline.Emphasis(T("it")), text);
        Assert.Contains(new MarkdownInline.Styled(InlineStyle.Superscript, T("2")), text);
        Assert.Contains(new MarkdownInline.Styled(InlineStyle.Subscript, T("2")), text);
        Assert.Contains(new MarkdownInline.Styled(InlineStyle.Highlight, T("hi")), text);
        Assert.Contains(new MarkdownInline.Styled(InlineStyle.Keyboard, T("Ctrl")), text);
        Assert.Contains(new MarkdownInline.Strikethrough(T("old")), text);
    }

    [Fact]
    public void MarkdownInsideTagsAndTagsInsideMarkdown()
    {
        var text = Inlines("<u>an *important* note</u> and **bold <sup>up</sup>**");
        var underline = Assert.IsType<MarkdownInline.Styled>(text.First(i => i is MarkdownInline.Styled));
        Assert.Contains(new MarkdownInline.Emphasis(T("important")), underline.Content);
        var strong = Assert.IsType<MarkdownInline.Strong>(text.First(i => i is MarkdownInline.Strong));
        Assert.Contains(new MarkdownInline.Styled(InlineStyle.Superscript, T("up")), strong.Content);
    }

    [Fact]
    public void LinksPicturesAndUnknownTags()
    {
        var text = Inlines("<a href=\"https://example.com\">site</a> <img src=\"a.png\" alt=\"Logo\"> <span class=\"x\">kept</span> <u>unclosed");
        Assert.Contains(new MarkdownInline.Link("https://example.com", null, T("site")), text);
        Assert.Contains(text, i => i is MarkdownInline.Image { Source: "a.png" } image && image.Alt.PlainText() == "Logo");
        Assert.Contains("kept", text.PlainText());
        Assert.Contains("unclosed", text.PlainText());
        Assert.DoesNotContain("<", text.PlainText());
    }

    [Fact]
    public void EveryFormatWritesTheStyle()
    {
        var document = MarkdownDocument.Parse("<u>under</u> x<sup>2</sup> <mark>hi</mark>");
        var html = new MarkdownHtml().Body(document);
        Assert.Contains("<u>under</u>", html);
        Assert.Contains("<sup>2</sup>", html);
        Assert.Contains("<mark>hi</mark>", html);
        var rtf = MarkdownRtf.Render(document, null, PaperSize.A4);
        Assert.Contains(@"\ul ", rtf);
        Assert.Contains(@"\super ", rtf);
        Assert.Contains(@"\highlight6 ", rtf);
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "a.docx");
        MarkdownDocx.Write(document, folder.Path, path);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("<w:u w:val=\"single\"/>", xml);
        Assert.Contains("<w:vertAlign w:val=\"superscript\"/>", xml);
        Assert.Contains("<w:highlight w:val=\"yellow\"/>", xml);
    }
}
