using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Pan-and-zoom portrait cropper.  The crop frame is fixed at a 3:4 aspect ratio
/// (matching the portrait zone in the entity edit dialog). The user drags the image
/// to position it, scrolls or uses the slider to zoom, and confirms to crop.
/// </summary>
public class PortraitCropDialog : Window
{
    // ── Constants ──────────────────────────────────────────────────────────
    private const double FrameW     = 210;
    private const double FrameH     = 280;   // 3:4
    private const double CanvasW    = 520;
    private const double CanvasH    = 400;
    private const double FrameLeft  = (CanvasW - FrameW) / 2;   // 155
    private const double FrameTop   = (CanvasH - FrameH) / 2;   // 60
    private const double ZoomMin    = 0.05;
    private const double ZoomMax    = 8.0;

    // ── State ──────────────────────────────────────────────────────────────
    private BitmapImage _source;
    private double _zoom;
    private double _panX, _panY;
    private bool   _dragging;
    private Point  _dragStart;
    private double _panXAtDrag, _panYAtDrag;

    // ── UI elements (updated on layout change) ─────────────────────────────
    private System.Windows.Controls.Image _imgEl   = null!;
    private System.Windows.Controls.Image _preview = null!;
    private Slider   _zoomSlider  = null!;
    private TextBlock _zoomLabel  = null!;
    private Canvas   _canvas      = null!;

    // ── Result ─────────────────────────────────────────────────────────────
    public BitmapSource? CroppedResult { get; private set; }

    // ── Static entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Opens the crop dialog for <paramref name="imagePath"/>.
    /// Returns a <see cref="BitmapSource"/> on confirm, or null on cancel.
    /// </summary>
    public static BitmapSource? Show(string imagePath, Window owner, string? themePath)
    {
        var dlg = new PortraitCropDialog(imagePath, owner, themePath);
        return dlg.ShowDialog() == true ? dlg.CroppedResult : null;
    }

    // ── Constructor ────────────────────────────────────────────────────────

    private PortraitCropDialog(string imagePath, Window owner, string? themePath)
    {
        // Load source
        _source = new BitmapImage();
        _source.BeginInit();
        _source.UriSource     = new Uri(imagePath);
        _source.CacheOption   = BitmapCacheOption.OnLoad;
        _source.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        _source.EndInit();
        _source.Freeze();

        // Compute initial zoom so the image fills the frame snugly
        double scaleX = FrameW / _source.PixelWidth;
        double scaleY = FrameH / _source.PixelHeight;
        _zoom = Math.Max(scaleX, scaleY);   // fill (not fit) so no empty margins
        _zoom = Math.Clamp(_zoom, ZoomMin, ZoomMax);

        // Centre the image on the frame
        double imgW = _source.PixelWidth  * _zoom;
        double imgH = _source.PixelHeight * _zoom;
        _panX = FrameLeft - (imgW - FrameW) / 2;
        _panY = FrameTop  - (imgH - FrameH) / 2;

        // Window setup
        Title                 = "Crop Portrait";
        Width                 = 800;
        Height                = 560;
        MinWidth              = 700;
        MinHeight             = 480;
        ResizeMode            = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner                 = owner;
        ShowInTaskbar         = false;

        if (!string.IsNullOrWhiteSpace(themePath))
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
        SetResourceReference(BackgroundProperty, "ContentBgBrush");
        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(this);

        BuildUI();
    }

    // ── UI ─────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        Content = root;

        // ── Left: canvas ───────────────────────────────────────────────────
        var canvasBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(20, 20, 24)), Margin = new Thickness(0) };
        Grid.SetColumn(canvasBorder, 0);
        root.Children.Add(canvasBorder);

        _canvas = new Canvas { Width = CanvasW, Height = CanvasH, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvasBorder.Child = _canvas;

        // Image element
        _imgEl = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Fill,
            Width   = _source.PixelWidth  * _zoom,
            Height  = _source.PixelHeight * _zoom
        };
        _imgEl.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        _imgEl.Source = _source;
        Canvas.SetLeft(_imgEl, _panX);
        Canvas.SetTop (_imgEl, _panY);
        _canvas.Children.Add(_imgEl);

        // Dark overlay with frame cut-out (EvenOdd rule)
        var overlayPath = new System.Windows.Shapes.Path
        {
            Fill     = new SolidColorBrush(Color.FromArgb(170, 0, 0, 24)),
            IsHitTestVisible = false
        };
        var outerRect = new RectangleGeometry(new Rect(0, 0, CanvasW, CanvasH));
        var frameRect  = new RectangleGeometry(new Rect(FrameLeft, FrameTop, FrameW, FrameH));
        var geoGroup   = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geoGroup.Children.Add(outerRect);
        geoGroup.Children.Add(frameRect);
        overlayPath.Data = geoGroup;
        _canvas.Children.Add(overlayPath);

        // Frame border (dashed white)
        var frameBorder = new Rectangle
        {
            Width           = FrameW,
            Height          = FrameH,
            Stroke          = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 8, 4 },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(frameBorder, FrameLeft);
        Canvas.SetTop (frameBorder, FrameTop);
        _canvas.Children.Add(frameBorder);

        // Corner ticks (L-shaped) for clarity
        foreach (var (cx, cy, sw, sh) in new (double,double,double,double)[]
            { (FrameLeft, FrameTop, 1, 1), (FrameLeft+FrameW, FrameTop, -1, 1),
              (FrameLeft, FrameTop+FrameH, 1, -1), (FrameLeft+FrameW, FrameTop+FrameH, -1, -1) })
        {
            foreach (var (dx, dy, lw, lh) in new (double,double,double,double)[]
                { (0, 0, sw * 14, 2.5), (0, 0, 2.5, sh * 14) })
            {
                var tick = new Rectangle { Width = Math.Abs(lw), Height = Math.Abs(lh), Fill = Brushes.White, IsHitTestVisible = false };
                Canvas.SetLeft(tick, lw < 0 ? cx + lw : cx);
                Canvas.SetTop (tick, lh < 0 ? cy + lh : cy);
                _canvas.Children.Add(tick);
            }
        }

        // Hint label
        var hintTb = new TextBlock
        {
            Text = "Drag to position  ·  Scroll to zoom",
            FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(hintTb, 8);
        Canvas.SetBottom(hintTb, 6);
        _canvas.Children.Add(hintTb);

        // Canvas interaction
        _canvas.Cursor    = Cursors.SizeAll;
        _canvas.MouseDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseUp   += OnCanvasMouseUp;
        _canvas.MouseLeave += (_, _) => { _dragging = false; _canvas.ReleaseMouseCapture(); };
        _canvas.MouseWheel += OnCanvasMouseWheel;

        // ── Right: controls ────────────────────────────────────────────────
        var sidePanel = new StackPanel { Margin = new Thickness(16, 16, 16, 16) };
        Grid.SetColumn(sidePanel, 1);
        root.Children.Add(sidePanel);

        // Preview label
        var prevLbl = new TextBlock { Text = "PREVIEW", FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 6) };
        prevLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        sidePanel.Children.Add(prevLbl);

        // Preview image (3:4 portrait, same ratio as frame)
        var previewBorder = new Border
        {
            Width = 140, Height = 187, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true
        };
        previewBorder.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        previewBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        _preview = new System.Windows.Controls.Image { Stretch = Stretch.Fill, Width = 140, Height = 187 };
        _preview.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        previewBorder.Child = _preview;
        sidePanel.Children.Add(previewBorder);

        // Zoom label
        var zoomLbl = new TextBlock { Text = "ZOOM", FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4) };
        zoomLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        sidePanel.Children.Add(zoomLbl);

        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        sidePanel.Children.Add(zoomRow);

        _zoomSlider = new Slider
        {
            Minimum = ZoomMin, Maximum = ZoomMax, Value = _zoom,
            Width = 130, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = false
        };
        _zoomLabel = new TextBlock
        {
            Text = FormatZoom(_zoom), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Width = 46
        };
        _zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        _zoomSlider.ValueChanged += (_, e) =>
        {
            _zoom = e.NewValue;
            _zoomLabel.Text = FormatZoom(_zoom);
            ApplyLayout();
        };
        zoomRow.Children.Add(_zoomSlider);
        zoomRow.Children.Add(_zoomLabel);

        // Reset button
        var resetBtn = MakeBtn("↺ Reset", false);
        resetBtn.Margin = new Thickness(0, 0, 0, 24);
        resetBtn.Click += (_, _) => ResetZoomPan();
        sidePanel.Children.Add(resetBtn);

        // Spacer
        sidePanel.Children.Add(new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 16) });

        // Buttons
        var cancelBtn = MakeBtn("Cancel", false);
        cancelBtn.Margin = new Thickness(0, 0, 0, 8);
        cancelBtn.Click += (_, _) => DialogResult = false;
        sidePanel.Children.Add(cancelBtn);

        var cropBtn = MakeBtn("✓  Use This Crop", true);
        cropBtn.Click += (_, _) =>
        {
            CroppedResult = ComputeCrop();
            if (CroppedResult is not null) DialogResult = true;
        };
        sidePanel.Children.Add(cropBtn);

        UpdatePreview();
    }

    // ── Interaction ────────────────────────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _dragging   = true;
        _dragStart  = e.GetPosition(_canvas);
        _panXAtDrag = _panX;
        _panYAtDrag = _panY;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos  = e.GetPosition(_canvas);
        _panX = _panXAtDrag + (pos.X - _dragStart.X);
        _panY = _panYAtDrag + (pos.Y - _dragStart.Y);
        ApplyLayout();
        e.Handled = true;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _canvas.ReleaseMouseCapture();
    }

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor  = e.Delta > 0 ? 1.12 : 1.0 / 1.12;
        var mousePos = e.GetPosition(_canvas);

        // Zoom toward mouse position
        var newZoom  = Math.Clamp(_zoom * factor, ZoomMin, ZoomMax);
        var ratio    = newZoom / _zoom;
        _panX        = mousePos.X + (_panX - mousePos.X) * ratio;
        _panY        = mousePos.Y + (_panY - mousePos.Y) * ratio;
        _zoom        = newZoom;

        _zoomSlider.Value = _zoom;   // will trigger ApplyLayout via ValueChanged
        e.Handled = true;
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private void ApplyLayout()
    {
        double imgW = _source.PixelWidth  * _zoom;
        double imgH = _source.PixelHeight * _zoom;
        _imgEl.Width  = imgW;
        _imgEl.Height = imgH;
        Canvas.SetLeft(_imgEl, _panX);
        Canvas.SetTop (_imgEl, _panY);
        _zoomLabel.Text = FormatZoom(_zoom);
        UpdatePreview();
    }

    private void ResetZoomPan()
    {
        double scaleX = FrameW / _source.PixelWidth;
        double scaleY = FrameH / _source.PixelHeight;
        _zoom = Math.Clamp(Math.Max(scaleX, scaleY), ZoomMin, ZoomMax);
        double imgW = _source.PixelWidth  * _zoom;
        double imgH = _source.PixelHeight * _zoom;
        _panX = FrameLeft - (imgW - FrameW) / 2;
        _panY = FrameTop  - (imgH - FrameH) / 2;
        _zoomSlider.Value = _zoom;   // triggers ApplyLayout
    }

    private void UpdatePreview()
    {
        var bmp = ComputeCrop();
        _preview.Source = bmp;
    }

    // ── Crop computation ───────────────────────────────────────────────────

    private CroppedBitmap? ComputeCrop()
    {
        if (_zoom <= 0) return null;

        // Frame region in image-pixel coordinates
        double cropX = (FrameLeft - _panX) / _zoom;
        double cropY = (FrameTop  - _panY) / _zoom;
        double cropW = FrameW / _zoom;
        double cropH = FrameH / _zoom;

        // Clamp to image bounds
        int x = (int)Math.Max(0, Math.Round(cropX));
        int y = (int)Math.Max(0, Math.Round(cropY));
        int w = (int)Math.Min(_source.PixelWidth  - x, Math.Round(cropW));
        int h = (int)Math.Min(_source.PixelHeight - y, Math.Round(cropH));

        if (w <= 0 || h <= 0) return null;
        try { return new CroppedBitmap(_source, new Int32Rect(x, y, w, h)); }
        catch { return null; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string FormatZoom(double z) => $"{z * 100:F0} %";

    private Button MakeBtn(string label, bool isPrimary)
    {
        var btn = new Button
        {
            Content = label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        btn.SetResourceReference(Button.BackgroundProperty,  isPrimary ? "ControlHoverBrush"    : "ControlBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty,  isPrimary ? "AccentHighlightBrush" : "SidebarTextBrush");
        btn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        btn.MouseEnter += (_, _) => btn.Opacity = 0.80;
        btn.MouseLeave += (_, _) => btn.Opacity = 1.00;
        return btn;
    }

    // ── Static helper: save a BitmapSource to a file ───────────────────────

    public static void SaveBitmap(BitmapSource bitmap, string destPath)
    {
        BitmapEncoder encoder = System.IO.Path.GetExtension(destPath).ToLowerInvariant() switch
        {
            ".png"  => new PngBitmapEncoder(),
            ".bmp"  => new BmpBitmapEncoder(),
            _       => new JpegBitmapEncoder { QualityLevel = 92 }
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(destPath);
        encoder.Save(stream);
    }
}
