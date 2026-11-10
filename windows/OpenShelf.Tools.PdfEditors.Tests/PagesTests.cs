using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors.Tests;

public class PageSelectionTests
{
    private static IReadOnlyList<PdfPageEdit> Pages(int n) => Enumerable.Range(0, n).Select(i => new PdfPageEdit { SourceIndex = i }).ToList();

    [Fact]
    public void A_plain_click_selects_only_that_page()
    {
        var pages = Pages(5);
        var selection = new PageSelection();
        selection.Click(pages[1].Id, pages, SelectMode.Replace);
        selection.Click(pages[3].Id, pages, SelectMode.Replace);
        Assert.Equal([3], selection.Indices(pages));
    }

    [Fact]
    public void Ctrl_click_adds_and_removes()
    {
        var pages = Pages(5);
        var selection = new PageSelection();
        selection.Click(pages[0].Id, pages, SelectMode.Toggle);
        selection.Click(pages[2].Id, pages, SelectMode.Toggle);
        Assert.Equal([0, 2], selection.Indices(pages));
        selection.Click(pages[0].Id, pages, SelectMode.Toggle);
        Assert.Equal([2], selection.Indices(pages));
    }

    [Fact]
    public void Shift_click_selects_the_run_from_the_last_click_in_either_direction()
    {
        var pages = Pages(6);
        var selection = new PageSelection();
        selection.Click(pages[4].Id, pages, SelectMode.Replace);
        selection.Click(pages[1].Id, pages, SelectMode.Range);
        Assert.Equal([1, 2, 3, 4], selection.Indices(pages));
    }

    [Fact]
    public void Shift_click_with_nothing_clicked_before_selects_just_that_page()
    {
        var pages = Pages(3);
        var selection = new PageSelection();
        selection.Click(pages[2].Id, pages, SelectMode.Range);
        Assert.Equal([2], selection.Indices(pages));
    }

    [Fact]
    public void Pages_that_were_deleted_are_forgotten_and_the_rest_stay_selected()
    {
        var pages = Pages(4);
        var selection = new PageSelection();
        selection.SelectAll(pages);
        selection.Prune(pages.Where((_, i) => i != 1).ToList());
        Assert.Equal(3, selection.Count);
        Assert.False(selection.Contains(pages[1].Id));
    }

    [Fact]
    public void Select_all_and_clear()
    {
        var pages = Pages(4);
        var selection = new PageSelection();
        selection.SelectAll(pages);
        Assert.Equal(4, selection.Count);
        selection.Clear();
        Assert.True(selection.IsEmpty);
    }
}

public class PageReorderTests
{
    private static readonly Guid[] Ids = Enumerable.Range(1, 6).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();

    private static IReadOnlySet<Guid> Set(params int[] numbers) => numbers.Select(n => Ids[n - 1]).ToHashSet();

    private static int[] Numbers(IEnumerable<Guid> ids) => ids.Select(id => Array.IndexOf(Ids, id) + 1).ToArray();

    [Fact]
    public void Order_puts_the_moving_pages_back_as_a_block_at_the_insertion_index()
    {
        Assert.Equal([1, 3, 4, 2, 5, 6], Numbers(PageReorder.Order(Ids, Set(2), 3)));
        Assert.Equal([2, 5, 1, 3, 4, 6], Numbers(PageReorder.Order(Ids, Set(2, 5), 0)));
        Assert.Equal([1, 3, 4, 6, 2, 5], Numbers(PageReorder.Order(Ids, Set(5, 2), 99)));
    }

    [Fact]
    public void Hovering_a_page_after_the_block_puts_the_block_after_it()
    {
        // Moving page 2; the pointer is over page 4: the block lands after 4.
        var at = PageReorder.InsertionIndex(Ids, Set(2), Ids[3]);
        Assert.Equal(3, at);
        Assert.Equal([1, 3, 4, 2, 5, 6], Numbers(PageReorder.Order(Ids, Set(2), at!.Value)));
    }

    [Fact]
    public void Hovering_a_page_before_the_block_puts_the_block_before_it()
    {
        var at = PageReorder.InsertionIndex(Ids, Set(5), Ids[1]);
        Assert.Equal(1, at);
        Assert.Equal([1, 5, 2, 3, 4, 6], Numbers(PageReorder.Order(Ids, Set(5), at!.Value)));
    }

    [Fact]
    public void Hovering_the_block_itself_or_nothing_changes_nothing()
    {
        Assert.Null(PageReorder.InsertionIndex(Ids, Set(2, 3), Ids[2]));
        Assert.Null(PageReorder.InsertionIndex(Ids, Set(2), null));
    }

    [Fact]
    public void Hovering_a_page_the_block_has_just_passed_puts_it_back_before_that_page()
    {
        // After the block moves behind page 4, a pointer over page 4 (now in an earlier slot) sends it back in front of it.
        IReadOnlyList<Guid> display = Ids;
        var moving = Set(2);
        display = PageReorder.Order(display, moving, PageReorder.InsertionIndex(display, moving, Ids[3])!.Value);
        Assert.Equal([1, 3, 4, 2, 5, 6], Numbers(display));
        var again = PageReorder.InsertionIndex(display, moving, Ids[3]);
        Assert.Equal(2, again);
        Assert.Equal([1, 3, 2, 4, 5, 6], Numbers(PageReorder.Order(display, moving, again!.Value)));
    }
}

public class PagesModelTests
{
    private static async Task<PagesModel> Opened(params string[] paths)
    {
        var model = new PagesModel(PdfFileState.ReadAll(paths));
        for (var i = 0; i < paths.Length; i++) await model.LoadAsync(i);
        return model;
    }

    [Fact]
    public async Task A_single_pdf_starts_unchanged_with_a_plain_Save_button()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 4));
        Assert.Equal(4, model.Current!.Pages.Count);
        Assert.False(model.CanSave);
        Assert.Equal("Save", model.PrimaryTitle);
        Assert.Equal("4 pages · Drag to reorder, click to select", model.Summary(0));
        Assert.Equal("4 pages · 2 selected", model.Summary(2));
    }

    [Fact]
    public async Task Editing_enables_Save_and_undo_to_the_start_disables_it_again()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 4));
        model.Current!.Rotate([1], 90);
        Assert.True(model.CanSave);
        model.Current.Undo();
        Assert.False(model.CanSave);
        model.Current.Redo();
        Assert.True(model.CanSave);
    }

    [Fact]
    public async Task A_pdf_with_every_page_deleted_cannot_be_saved()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 2));
        model.Current!.Delete([0, 1]);
        Assert.False(model.CanSave);
    }

    [Fact]
    public async Task With_several_pdfs_each_keeps_its_own_edits_and_the_button_counts_them()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 3), dir.MakePdf("b.pdf", 3), dir.MakePdf("c.pdf", 3));
        Assert.Equal("Save PDFs", model.PrimaryTitle);
        model.Document(0)!.Delete([0]);
        model.Document(2)!.Rotate([1], 180);
        Assert.Equal([true, false, true], model.Edited);
        Assert.Equal("Save 2 PDFs", model.PrimaryTitle);
        Assert.Equal(2, model.Document(0)!.Pages.Count);
        Assert.Equal(3, model.Document(1)!.Pages.Count);
        Assert.True(model.Document(2)!.CanUndo);
        Assert.False(model.Document(1)!.CanUndo);
        model.Document(2)!.Undo();
        Assert.Equal("Save 1 PDF", model.PrimaryTitle);
    }

    [Fact]
    public async Task A_batch_cannot_be_saved_while_an_edited_pdf_has_no_pages()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 1), dir.MakePdf("b.pdf", 2));
        model.Document(1)!.Rotate([0], 90);
        Assert.True(model.CanSave);
        model.Document(0)!.Delete([0]);
        Assert.False(model.CanSave);
    }

    [Fact]
    public async Task Locked_and_damaged_pdfs_report_the_Mac_wording_and_never_load()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 2), dir.MakeLocked("Secret.pdf"), dir.MakeDamaged("Broken.pdf"));
        Assert.Null(model.Error(0));
        Assert.Equal("“Secret.pdf” is password-protected.", model.Error(1));
        Assert.Equal("“Broken.pdf” couldn’t be opened as a PDF.", model.Error(2));
        Assert.Null(model.Document(1));
        Assert.Null(model.Document(2));
    }

    [Fact]
    public async Task Saving_writes_Edited_copies_in_the_new_order_and_counts_the_rest()
    {
        using var dir = new TempFolder();
        var a = dir.MakePdf("a.pdf", 4);
        var b = dir.MakePdf("b.pdf", 2);
        var model = await Opened(a, b);
        model.Document(0)!.Move(0, 3);
        model.Document(0)!.Delete([0]);
        var outcome = Outcomes.From(PagesModel.Save(model.Snapshot(), null, CancellationToken.None));
        Assert.Equal([dir.File("a Edited.pdf")], outcome.Saved);
        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal("1 edited · 1 unchanged · a Edited.pdf", outcome.Message);
        Assert.Equal(3, TempFolder.PageCount(dir.File("a Edited.pdf")));
        Assert.Equal(["a Edited.pdf", "a.pdf", "b.pdf"], dir.Names());
    }

    [Fact]
    public async Task Cancelling_a_save_leaves_no_file()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 3));
        model.Current!.Rotate([0], 90);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PagesModel.Save(model.Snapshot(), null, cancel.Token));
        Assert.Equal(["a.pdf"], dir.Names());
    }

    [Fact]
    public async Task Moving_several_pages_as_a_block_is_one_undo_step()
    {
        using var dir = new TempFolder();
        var model = await Opened(dir.MakePdf("a.pdf", 6));
        var document = model.Current!;
        document.MoveMany([1, 2], 4);
        Assert.Equal([0, 3, 4, 5, 1, 2], document.Pages.Select(p => p.SourceIndex));
        document.Undo();
        Assert.Equal([0, 1, 2, 3, 4, 5], document.Pages.Select(p => p.SourceIndex));
        Assert.False(document.CanUndo);
    }
}
