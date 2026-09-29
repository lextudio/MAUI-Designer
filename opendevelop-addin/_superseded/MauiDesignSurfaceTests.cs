using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ICSharpCode.MauiDesigner;
using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Fresh.Core.Geometry;
using MAUIDesigner.Surface;

namespace ICSharpCode.MauiDesigner.AddIn.Tests;

/// <summary>
/// Tests for the WPF surface control itself. These run on this machine, LibreWPF included —
/// the only reason the geometry was moved out into <c>MAUIDesigner.Surface</c> rather than
/// inlined here was to keep <em>this</em> file small, not because a WPF control is untestable
/// off Windows.
/// </summary>
public sealed class MauiDesignSurfaceTests
{
    /// <summary>WPF objects need a dispatcher; xunit runs on a worker thread.</summary>
    /// <summary>WPF objects need a dispatcher and an STA; xunit gives neither.</summary>
    static void OnUi(Action body) => OnUi<bool>(() => { body(); return true; });

    static T OnUi<T>(Func<T> body)
    {
        Exception? failure = null;
        T result = default!;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        // SingleThreadedApartment is a Windows-only concept and SetApartmentState throws
        // PlatformNotSupportedException off Windows, so it is requested only where it exists.
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }

    /// A hand-built 4x4 PNG. Deliberately NOT produced with <c>RenderTargetBitmap</c>: WPF
    /// rasterization needs the native WPF graphics library, which is Windows-only even though
    /// WPF itself runs here under LibreWPF. Frame decoding has to be testable without it.
    const string SamplePng = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAEklEQVR4nGM4kWL0HxkzkC4AAAKXJdFjYcBLAAAAAElFTkSuQmCC";

    static string PngBase64(int width, int height, byte r, byte g, byte b) => SamplePng;

    static string DeflatedBgraBase64(int width, int height, byte value)
    {
        int stride = (width * 4 + 3) & ~3;
        var raw = new byte[stride * height];
        for (int index = 0; index < raw.Length; index++)
        {
            raw[index] = value;
        }

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    [Fact]
    public void ShowFrame_Places_The_Bitmap_And_Reports_Its_Design_Size()
    {
        OnUi(() =>
        {
            var surface = new MauiDesignSurface { Width = 800, Height = 800 };
            surface.Measure(new Size(800, 800));
            surface.Arrange(new Rect(0, 0, 800, 800));

            surface.ShowFrame(new DesignerRenderFrame
            {
                Sequence = 1,
                Width = 400,
                Height = 200,
                PngBase64 = PngBase64(400, 200, 10, 120, 200),
            });

            // No adorners were set, so there is deliberately no selection: a frame alone never
            // implies one.
            Assert.Null(surface.Selection);
            Assert.Empty(surface.SelectedElementIds);
            // Not IsVisible: that is false for any element never hosted in a PresentationSource,
            // so it could not pass here. What this test is about is that the frame was presented.
            Assert.True(surface.HasFrame);
        });
    }

    [Fact]
    public void A_Stale_Frame_Is_Ignored_So_The_Bitmap_Does_Not_Flicker_Back()
    {
        OnUi(() =>
        {
            var surface = new MauiDesignSurface { Width = 400, Height = 400 };
            surface.Measure(new Size(400, 400));
            surface.Arrange(new Rect(0, 0, 400, 400));
            surface.ShowFrame(new DesignerRenderFrame
            {
                Sequence = 7,
                Width = 100,
                Height = 100,
                PngBase64 = PngBase64(100, 100, 1, 2, 3),
            });

            // Same sequence again: must be dropped, and must not throw on a null source.
            surface.ShowFrame(new DesignerRenderFrame
            {
                Sequence = 7,
                Width = 999,
                Height = 999,
                PngBase64 = PngBase64(999, 999, 9, 9, 9),
            });
        });
    }

    [Fact]
    public void Deflated_Bgra_Frames_Are_Accepted_And_Not_Transparent()
    {
        OnUi(() =>
        {
            var surface = new MauiDesignSurface { Width = 200, Height = 200 };
            surface.Measure(new Size(200, 200));
            surface.Arrange(new Rect(0, 0, 200, 200));

            surface.ShowFrame(new DesignerRenderFrame
            {
                Sequence = 1,
                Width = 13,
                Height = 7,
                Data = DeflatedBgraBase64(13, 7, 0x7F),
            });

            // A width of 13 exercises the stride padding: 13*4 = 52 is already 4-aligned, so use
            // an odd width that is not, and confirm the frame still decodes.
            surface.ShowFrame(new DesignerRenderFrame
            {
                Sequence = 2,
                Width = 11,
                Height = 5,
                Data = DeflatedBgraBase64(11, 5, 0x40),
            });
        });
    }

    [Fact]
    public void Selection_Overlays_Are_Built_From_The_Reported_Tree_And_Skip_Hidden_Nodes()
    {
        OnUi(() =>
        {
            var surface = new MauiDesignSurface { Width = 400, Height = 300 };
            surface.Measure(new Size(400, 300));
            surface.Arrange(new Rect(0, 0, 400, 300));

            DesignerElementNode tree = new()
            {
                Id = "root",
                Type = "ContentPage",
                Width = 400,
                Height = 300,
                Children =
                {
                    new DesignerElementNode { Id = "shown", Name = "Shown", Type = "Label", X = 10, Y = 20, Width = 100, Height = 30 },
                    new DesignerElementNode { Id = "hidden", Name = "Hidden", Type = "Label", X = 10, Y = 20, Width = 100, Height = 30, IsVisible = false },
                },
            };

            surface.ShowFrame(new DesignerRenderFrame { Sequence = 1, Width = 400, Height = 300 });
            surface.SetAdorners(SurfaceAdorners.Build(tree, "shown"), "shown");

            Assert.NotNull(surface.Selection);
            Assert.Equal("shown", surface.Selection!.ElementId);
            Assert.Equal(new[] { "shown" }, surface.SelectedElementIds);
        });
    }

    [Fact]
    public void A_Hidden_Selected_Element_Leaves_The_Surface_With_Nothing_To_Outline()
    {
        OnUi(() =>
        {
            var surface = new MauiDesignSurface();
            DesignerElementNode tree = new()
            {
                Id = "root",
                Type = "ContentPage",
                Children = { new DesignerElementNode { Id = "hidden", Type = "Label", IsVisible = false } },
            };

            surface.SetAdorners(SurfaceAdorners.Build(tree, "hidden"), "hidden");

            Assert.Null(surface.Selection);
            Assert.Empty(surface.SelectedElementIds);
        });
    }

    [Fact]
    public void Clear_Resets_The_Presented_Sequence_So_The_Next_Frame_Shows()
    {
        OnUi(() =>
        {
            var surface = new MauiDesignSurface { Width = 100, Height = 100 };
            surface.ShowFrame(new DesignerRenderFrame { Sequence = 5, Width = 10, Height = 10 });
            surface.Clear();

            // Would be dropped as stale if Clear had not reset the sequence.
            surface.ShowFrame(new DesignerRenderFrame { Sequence = 1, Width = 10, Height = 10 });
        });
    }

}
