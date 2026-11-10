using OpenShelf.Core;
using static OpenShelf.Tour.Tests.Events;

namespace OpenShelf.Tour.Tests;

public class StepTests
{
    [Fact]
    public void The_tour_has_welcome_eight_practice_steps_and_a_finish_in_order()
    {
        Assert.Equal(10, TourSteps.All.Count);
        Assert.Equal(TourStep.Welcome, TourSteps.All[0]);
        Assert.Equal(TourStep.Finish, TourSteps.All[^1]);
        Assert.Equal(8, TourSteps.Practice.Count);
        Assert.Equal(
            new[] { "Convert a photo", "Markdown to PDF", "Edit with Tools", "Work with PDFs", "Trim a video", "Keep files on a shelf", "No drag needed", "Choose the settings" },
            TourSteps.Practice.Select(s => s.Title()));
    }

    [Theory]
    [InlineData("convert.png", "photo.JPG", true)]
    [InlineData("convert.pdf", "photo.png", true)]
    [InlineData("convert.png", "notes.md", false)]       // wrong kind of file
    [InlineData("tool.crop", "photo.jpg", false)]        // wrong wheel's wedge
    public void Convert_needs_a_convert_wedge_run_on_an_image(string id, string file, bool expected) =>
        Assert.Equal(expected, TourStep.Convert.IsCompleted(Ran(WheelKind.Convert, id, file)));

    [Fact]
    public void Convert_counts_any_image_among_the_dropped_files() =>
        Assert.True(TourStep.Convert.IsCompleted(Ran(WheelKind.Convert, "convert.jpg", @"C:\a\notes.txt", @"C:\a\pic.webp")));

    [Fact]
    public void Markdown_needs_PDF_from_a_markdown_file()
    {
        Assert.True(TourStep.Markdown.IsCompleted(Ran(WheelKind.Convert, "convert.pdf", "Trip Notes.md")));
        Assert.True(TourStep.Markdown.IsCompleted(Ran(WheelKind.Convert, "convert.pdf", "x.markdown")));
        Assert.False(TourStep.Markdown.IsCompleted(Ran(WheelKind.Convert, "convert.docx", "Trip Notes.md")));
        Assert.False(TourStep.Markdown.IsCompleted(Ran(WheelKind.Convert, "convert.pdf", "photo.jpg")));
    }

    [Fact]
    public void Tools_needs_a_tool_on_an_image_and_Pdf_needs_a_tool_on_a_pdf()
    {
        Assert.True(TourStep.Tools.IsCompleted(Ran(WheelKind.Tools, "tool.crop", "a.png")));
        Assert.False(TourStep.Tools.IsCompleted(Ran(WheelKind.Tools, "tool.crop", "a.pdf")));
        Assert.True(TourStep.Pdf.IsCompleted(Ran(WheelKind.Tools, "tool.split", "a.pdf")));
        Assert.True(TourStep.Pdf.IsCompleted(Ran(WheelKind.Tools, "tool.pages", "a.png", "a.pdf")));
        Assert.False(TourStep.Pdf.IsCompleted(Ran(WheelKind.Tools, "tool.split", "a.png")));
        Assert.False(TourStep.Pdf.IsCompleted(Ran(WheelKind.Convert, "convert.png", "a.pdf")));
    }

    [Fact]
    public void Video_accepts_a_tool_or_GIF_on_video_or_audio()
    {
        Assert.True(TourStep.Video.IsCompleted(Ran(WheelKind.Tools, "tool.trim", "Sunset.avi")));
        Assert.True(TourStep.Video.IsCompleted(Ran(WheelKind.Tools, "tool.mute", "clip.mp4")));
        Assert.True(TourStep.Video.IsCompleted(Ran(WheelKind.Tools, "tool.trim", "song.mp3")));
        Assert.True(TourStep.Video.IsCompleted(Ran(WheelKind.Convert, "convert.gif", "clip.mov")));
        Assert.False(TourStep.Video.IsCompleted(Ran(WheelKind.Convert, "convert.mp4", "clip.mov")));
        Assert.False(TourStep.Video.IsCompleted(Ran(WheelKind.Tools, "tool.trim", "photo.jpg")));
    }

    [Fact]
    public void Shelf_needs_the_shelf_or_add_to_shelf_wedge()
    {
        Assert.True(TourStep.Shelf.IsCompleted(Ran(WheelKind.Actions, "action.shelf", "a.md")));
        Assert.True(TourStep.Shelf.IsCompleted(Ran(WheelKind.Actions, "action.addtoshelf", "a.md")));
        Assert.False(TourStep.Shelf.IsCompleted(Ran(WheelKind.Actions, "action.copy", "a.md")));
        Assert.False(TourStep.Shelf.IsCompleted(Opened(WheelKind.Actions)));
    }

    [Fact]
    public void Keyboard_is_done_when_the_keyboard_wheel_opens_not_when_a_drag_wheel_does()
    {
        Assert.True(TourStep.Keyboard.IsCompleted(Opened(WheelKind.Convert, keyboard: true)));
        Assert.False(TourStep.Keyboard.IsCompleted(Opened(WheelKind.Convert, keyboard: false)));
        Assert.False(TourStep.Keyboard.IsCompleted(Ran(WheelKind.Convert, "convert.png", "a.jpg")));
    }

    [Fact]
    public void Options_needs_a_convert_wedge_run_with_Alt()
    {
        Assert.True(TourStep.Options.IsCompleted(Ran(WheelKind.Convert, "convert.mp4", "Sunset.avi", options: true)));
        Assert.False(TourStep.Options.IsCompleted(Ran(WheelKind.Convert, "convert.mp4", "Sunset.avi", options: false)));
        Assert.False(TourStep.Options.IsCompleted(Ran(WheelKind.Tools, "tool.trim", "Sunset.avi", options: true)));
    }

    [Fact]
    public void Welcome_and_Finish_never_complete_by_events()
    {
        foreach (var step in new[] { TourStep.Welcome, TourStep.Finish })
        {
            Assert.False(step.IsCompleted(Ran(WheelKind.Convert, "convert.png", "a.jpg")));
            Assert.False(step.IsUnderWay(Opened(WheelKind.Convert)));
        }
    }

    [Fact]
    public void A_step_is_under_way_when_its_own_wheel_opens_mid_drag()
    {
        Assert.True(TourStep.Convert.IsUnderWay(Opened(WheelKind.Convert)));
        Assert.True(TourStep.Options.IsUnderWay(Opened(WheelKind.Convert)));
        Assert.True(TourStep.Pdf.IsUnderWay(Opened(WheelKind.Tools)));
        Assert.True(TourStep.Shelf.IsUnderWay(Opened(WheelKind.Actions)));
        Assert.False(TourStep.Convert.IsUnderWay(Opened(WheelKind.Tools)));
        Assert.False(TourStep.Convert.IsUnderWay(Opened(WheelKind.Convert, keyboard: true)));
        Assert.False(TourStep.Convert.IsUnderWay(Ran(WheelKind.Convert, "convert.png", "a.jpg")));
        Assert.False(TourStep.Keyboard.IsUnderWay(Opened(WheelKind.Convert)));
    }

    [Fact]
    public void Every_practice_step_has_a_file_and_every_wheel_step_a_target()
    {
        foreach (var step in TourSteps.Practice)
        {
            Assert.NotNull(step.File());
            if (step.Wheel() is not null) Assert.NotEqual("", step.Target());
        }
    }

    [Theory]
    [InlineData("a.JPG", FileKind.Image)]
    [InlineData("a.heic", FileKind.Image)]
    [InlineData("a.mov", FileKind.Video)]
    [InlineData("a.avi", FileKind.Video)]
    [InlineData("a.flac", FileKind.Audio)]
    [InlineData("a.PDF", FileKind.Pdf)]
    [InlineData("a.md", FileKind.Markdown)]
    [InlineData("a.txt", FileKind.Other)]
    [InlineData("noextension", FileKind.Other)]
    public void File_kinds(string path, FileKind kind) => Assert.Equal(kind, FileKinds.Of(path));
}
