using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Fresh.Core.Geometry;
using MAUIDesigner.Surface;

namespace MAUIDesigner.Surface.Tests;

public sealed class SurfaceViewportTests
{
    static DesignerRenderFrame Frame(int width, int height, long sequence = 1) =>
        new() { Width = width, Height = height, Sequence = sequence };

    [Fact]
    public void Fit_Scales_To_The_Tighter_Axis_And_Centres()
    {
        SurfaceViewport viewport = SurfaceViewport.Fit(Frame(400, 200), 800, 800);

        // 400x200 at scale 2 exactly fills the pane horizontally, so only the vertical axis
        // has slack: OffsetX is 0, not "centred".
        Assert.Equal(2.0, viewport.Scale, 6);
        Assert.Equal(0.0, viewport.OffsetX, 6);
        Assert.Equal(200.0, viewport.OffsetY, 6);
    }

    [Fact]
    public void Zoom_Of_One_Means_Fit_Not_One_To_One()
    {
        SurfaceViewport fit = SurfaceViewport.Fit(Frame(400, 200), 800, 800);
        Assert.Equal(2.0, fit.Scale, 6);

        // True 1:1 needs 1/fittedScale, which is the trap that makes "100%" reproduce Fit.
        SurfaceViewport oneToOne = SurfaceViewport.Fit(Frame(400, 200), 800, 800, zoom: 1.0 / fit.Scale);
        Assert.Equal(1.0, oneToOne.Scale, 6);
    }

    [Fact]
    public void Coordinates_Round_Trip_Both_Ways()
    {
        SurfaceViewport viewport = SurfaceViewport.Fit(Frame(400, 200), 800, 800);

        PointD design = viewport.SurfaceToDesign(300, 500);
        Assert.Equal(150.0, design.X, 6);
        Assert.Equal(150.0, design.Y, 6);

        PointD surface = viewport.DesignToSurface(150, 150);
        Assert.Equal(300.0, surface.X, 6);
        Assert.Equal(500.0, surface.Y, 6);
    }

    [Fact]
    public void Coordinate_Round_Trip_Survives_A_Whole_Grid()
    {
        SurfaceViewport viewport = SurfaceViewport.Fit(Frame(640, 480), 500, 900, zoom: 1.3);

        foreach (double x in new double[] { 0, 1, 37.5, 320, 639 })
        {
            foreach (double y in new double[] { 0, 2, 99.25, 240, 479 })
            {
                PointD surface = viewport.DesignToSurface(x, y);
                PointD back = viewport.SurfaceToDesign(surface.X, surface.Y);
                Assert.Equal(x, back.X, 6);
                Assert.Equal(y, back.Y, 6);
            }
        }
    }

    [Fact]
    public void Points_Outside_The_Frame_Are_Not_On_It()
    {
        SurfaceViewport viewport = SurfaceViewport.Fit(Frame(400, 200), 800, 800);

        // The frame spans x 0..800 and y 200..600, and its edges count as inside.
        Assert.True(viewport.ContainsSurfacePoint(400, 400));
        Assert.True(viewport.ContainsSurfacePoint(400, 200));
        Assert.False(viewport.ContainsSurfacePoint(400, 100));
        Assert.False(viewport.ContainsSurfacePoint(400, 700));
    }

    [Fact]
    public void A_Missing_Or_Empty_Frame_Collapses_Instead_Of_Throwing()
    {
        foreach (DesignerRenderFrame? frame in new DesignerRenderFrame?[] { null, Frame(0, 0) })
        {
            SurfaceViewport viewport = SurfaceViewport.Fit(frame, 800, 800);
            Assert.Equal(0, viewport.Scale);
            Assert.False(viewport.ContainsSurfacePoint(10, 10));
            Assert.True(double.IsNaN(viewport.SurfaceToDesign(10, 10).X));
        }
    }

    [Fact]
    public void Stale_Frames_Are_Recognised_So_They_Are_Not_Redrawn()
    {
        Assert.True(SurfaceViewport.IsStale(presentedSequence: 5, frame: Frame(400, 200, sequence: 5)));
        Assert.True(SurfaceViewport.IsStale(presentedSequence: 5, frame: Frame(400, 200, sequence: 4)));
        Assert.False(SurfaceViewport.IsStale(presentedSequence: 5, frame: Frame(400, 200, sequence: 6)));
        Assert.True(SurfaceViewport.IsStale(presentedSequence: 5, frame: null));
    }

    [Fact]
    public void Design_Bounds_Map_Through_The_Same_Transform_As_Points()
    {
        SurfaceViewport viewport = SurfaceViewport.Fit(Frame(400, 200), 800, 800);

        RectD surface = viewport.FrameToSurface(new RectD(10, 20, 30, 40));

        Assert.Equal(20.0, surface.X, 6);
        Assert.Equal(240.0, surface.Y, 6);
        Assert.Equal(60.0, surface.Width, 6);
        Assert.Equal(80.0, surface.Height, 6);
    }
}

public sealed class SurfaceAdornerTests
{
    /// <summary>Two tab pages that deliberately SHARE bounds — the shape that made the
    /// always-on overlays land on the wrong tab in this repository's WinForms surface.</summary>
    static DesignerElementNode TabbedWithHiddenSecondPage() => new()
    {
        Id = "root",
        Path = string.Empty,
        Type = "TabbedPage",
        Width = 400,
        Height = 300,
        Children =
        {
            new DesignerElementNode
            {
                Id = "page1",
                Name = "First",
                Type = "ContentPage",
                Path = "0",
                X = 0,
                Y = 0,
                Width = 400,
                Height = 300,
            },
            new DesignerElementNode
            {
                Id = "page2",
                Name = "Second",
                Type = "ContentPage",
                Path = "1",
                X = 0,
                Y = 0,
                Width = 400,
                Height = 300,
                IsVisible = false,
            },
        },
    };

    [Fact]
    public void Hidden_Nodes_Are_Skipped_So_Overlays_Do_Not_Land_On_The_Visible_Sibling()
    {
        IReadOnlyList<SurfaceAdorner> adorners = SurfaceAdorners.Build(TabbedWithHiddenSecondPage(), "page2");

        Assert.DoesNotContain(adorners, adorner => adorner.ElementId == "page2");
        Assert.Contains(adorners, adorner => adorner.ElementId == "page1");
    }

    [Fact]
    public void A_Hidden_Selection_Has_No_Adorner_To_Draw()
    {
        IReadOnlyList<SurfaceAdorner> adorners = SurfaceAdorners.Build(TabbedWithHiddenSecondPage(), "page2");

        Assert.Null(SurfaceAdorners.Find(adorners, "page2"));
    }

    [Fact]
    public void Two_Selected_Elements_Report_Their_Own_Bounds()
    {
        DesignerElementNode root = new()
        {
            Id = "root",
            Type = "Grid",
            Width = 400,
            Height = 300,
            Children =
            {
                new DesignerElementNode { Id = "a", Name = "A", Type = "Label", X = 10, Y = 20, Width = 100, Height = 30 },
                new DesignerElementNode { Id = "b", Name = "B", Type = "Button", X = 200, Y = 100, Width = 80, Height = 24 },
            },
        };

        IReadOnlyList<SurfaceAdorner> adorners = SurfaceAdorners.Build(root, "a");

        SurfaceAdorner? a = SurfaceAdorners.Find(adorners, "a");
        SurfaceAdorner? b = SurfaceAdorners.Find(adorners, "b");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(10, a!.Bounds.X);
        Assert.Equal(200, b!.Bounds.X);
    }

    [Fact]
    public void Selection_Flag_Tracks_Only_The_Selected_Element()
    {
        DesignerElementNode root = new()
        {
            Id = "root",
            Type = "Grid",
            Children =
            {
                new DesignerElementNode { Id = "a", Type = "Label" },
                new DesignerElementNode { Id = "b", Type = "Button" },
            },
        };

        IReadOnlyList<SurfaceAdorner> adorners = SurfaceAdorners.Build(root, "b");

        Assert.False(adorners.Single(a => a.ElementId == "a").IsSelected);
        Assert.True(adorners.Single(a => a.ElementId == "b").IsSelected);
    }

    [Fact]
    public void Tray_Components_Stay_Marked_So_An_Outline_Is_Not_Drawn_For_Them()
    {
        DesignerElementNode root = new()
        {
            Id = "root",
            Type = "Grid",
            Children = { new DesignerElementNode { Id = "vm", Type = "PageViewModel", IsTrayComponent = true } },
        };

        Assert.True(SurfaceAdorners.Build(root, null).Single(a => a.ElementId == "vm").IsTrayComponent);
    }

    [Fact]
    public void Paths_Are_Carried_Through_So_A_Pick_Can_Map_Back_To_The_Source()
    {
        DesignerElementNode root = new()
        {
            Id = "root",
            Type = "Grid",
            Children =
            {
                new DesignerElementNode
                {
                    Id = "deep",
                    Type = "Label",
                    Children = { new DesignerElementNode { Id = "deeper", Type = "Label", Path = "0,1,0" } },
                },
            },
        };

        Assert.Equal("0,1,0", SurfaceAdorners.Build(root, null).Single(a => a.ElementId == "deeper").Path);
    }

    [Fact]
    public void Eight_Handles_Are_Produced_And_They_Stay_Inside_The_Element()
    {
        IReadOnlyList<(ResizeHandle Handle, RectD Bounds)> handles =
            SurfaceAdorners.Handles(new RectD(100, 100, 50, 20));

        Assert.Equal(8, handles.Count);
        Assert.Equal(8, handles.Select(h => h.Handle).Distinct().Count());
        foreach ((ResizeHandle _, RectD bounds) in handles)
        {
            Assert.True(bounds.X >= 100 && bounds.Right <= 150, "handle escaped horizontally");
            Assert.True(bounds.Y >= 100 && bounds.Bottom <= 120, "handle escaped vertically");
        }
    }

    [Fact]
    public void A_Tiny_Element_Is_Flagged_As_Unable_To_Carry_A_Name_Tag()
    {
        Assert.False(new SurfaceAdorner("a", "A", "Label", "0", new RectD(0, 0, 10, 10), false, false).FitsNameTag);
        Assert.True(new SurfaceAdorner("b", "B", "Label", "1", new RectD(0, 0, 100, 20), false, false).FitsNameTag);
    }
}

public sealed class DesignSurfaceClickArbiterTests
{
    static readonly SurfaceAdorner Selected = new(
        "selected", "Sel", "Border", "0", new RectD(100, 100, 80, 40), true, false);

    static readonly DesignerElementNode Reported = new() { Id = "reported", Name = "Rep", Type = "Label" };

    [Fact]
    public void A_Corner_Press_Resizes_Even_Though_It_Is_Also_Inside_The_Element()
    {
        SurfacePressResult result = DesignSurfaceClickArbiter.Decide(
            102, 102, [Selected], "selected", [Reported]);

        Assert.Equal(SurfaceIntent.Resize, result.Intent);
        Assert.Equal(ResizeHandle.TopLeft, result.Handle);
        Assert.Equal("selected", result.ElementId);
    }

    [Fact]
    public void Every_Handle_Corner_Resizes_With_Its_Own_Handle()
    {
        RectD bounds = Selected.Bounds;
        var expected = new (double X, double Y, ResizeHandle Handle)[]
        {
            (bounds.X + 1, bounds.Y + 1, ResizeHandle.TopLeft),
            (bounds.Right - 1, bounds.Y + 1, ResizeHandle.TopRight),
            (bounds.X + 1, bounds.Bottom - 1, ResizeHandle.BottomLeft),
            (bounds.Right - 1, bounds.Bottom - 1, ResizeHandle.BottomRight),
            (bounds.X + bounds.Width / 2, bounds.Y + 1, ResizeHandle.Top),
            (bounds.X + bounds.Width / 2, bounds.Bottom - 1, ResizeHandle.Bottom),
            (bounds.Right - 1, bounds.Y + bounds.Height / 2, ResizeHandle.Right),
            (bounds.X + 1, bounds.Y + bounds.Height / 2, ResizeHandle.Left),
        };

        foreach ((double x, double y, ResizeHandle handle) in expected)
        {
            SurfacePressResult result = DesignSurfaceClickArbiter.Decide(x, y, [Selected], "selected", [Reported]);
            Assert.Equal(SurfaceIntent.Resize, result.Intent);
            Assert.Equal(handle, result.Handle);
        }
    }

    [Fact]
    public void A_Press_Inside_The_Selection_But_Away_From_Handles_Moves_It()
    {
        SurfacePressResult result = DesignSurfaceClickArbiter.Decide(
            140, 120, [Selected], "selected", [Reported]);

        Assert.Equal(SurfaceIntent.Move, result.Intent);
        Assert.Equal("selected", result.ElementId);
    }

    [Fact]
    public void A_Press_Outside_The_Selection_Selects_Whatever_The_Child_Reported()
    {
        SurfacePressResult result = DesignSurfaceClickArbiter.Decide(
            10, 10, [Selected], "selected", [Reported]);

        Assert.Equal(SurfaceIntent.Select, result.Intent);
        Assert.Equal("reported", result.ElementId);
    }

    [Fact]
    public void A_Press_On_Empty_Canvas_Does_Nothing()
    {
        Assert.Equal(SurfaceIntent.None, DesignSurfaceClickArbiter.Decide(10, 10, [Selected], "selected").Intent);
    }

    [Fact]
    public void A_Null_Or_Empty_Reported_Chain_Does_Not_Invent_A_Hit()
    {
        Assert.Equal(SurfaceIntent.None, DesignSurfaceClickArbiter.Decide(10, 10, [Selected], "selected", []).Intent);
        Assert.Equal(
            SurfaceIntent.None,
            DesignSurfaceClickArbiter.Decide(10, 10, [Selected], "selected", [new DesignerElementNode()]).Intent);
    }

    [Fact]
    public void With_No_Selection_A_Reported_Hit_Still_Selects()
    {
        SurfacePressResult result = DesignSurfaceClickArbiter.Decide(10, 10, [Selected], null, [Reported]);

        Assert.Equal(SurfaceIntent.Select, result.Intent);
        Assert.Equal("reported", result.ElementId);
    }

    [Fact]
    public void Handle_Tolerance_Makes_Small_Elements_Still_Resizable()
    {
        var tiny = new SurfaceAdorner("t", "T", "BoxView", "0", new RectD(10, 10, 4, 4), true, false);

        Assert.Equal(
            SurfaceIntent.Select,
            DesignSurfaceClickArbiter.Decide(20, 20, [tiny], "t", [Reported]).Intent);

        Assert.Equal(
            SurfaceIntent.Resize,
            DesignSurfaceClickArbiter.Decide(20, 20, [tiny], "t", [Reported], handleTolerance: 8).Intent);
    }

    [Fact]
    public void Precedence_Handles_Beat_Move_Which_Beats_Select()
    {
        // Same point, three different questions: the resolver must answer the most specific one.
        Assert.Equal(SurfaceIntent.Resize, DesignSurfaceClickArbiter.Decide(101, 101, [Selected], "selected", [Reported]).Intent);
        // (140,130) is inside the selection (100..180 x 100..140) but clear of every handle.
        Assert.Equal(SurfaceIntent.Move, DesignSurfaceClickArbiter.Decide(140, 130, [Selected], "selected", [Reported]).Intent);
        Assert.Equal(SurfaceIntent.Select, DesignSurfaceClickArbiter.Decide(300, 300, [Selected], "selected", [Reported]).Intent);
    }
}
