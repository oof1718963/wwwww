using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace LiquidLauncher.Controls;

/// <summary>
/// A glass "card" with true superellipse (squircle) corners instead of arc-based
/// rounded corners. All the actual glass rendering (blur brush, specular border,
/// sheen gradient) lives in the ControlTheme in GlassStyles.axaml — this class's
/// only job is computing the squircle clip path whenever the control resizes,
/// and exposing the shape parameters as styleable properties.
/// </summary>
public class SquircleGlassCard : ContentControl
{
    public static readonly StyledProperty<double> CornerSmoothnessProperty =
        AvaloniaProperty.Register<SquircleGlassCard, double>(nameof(CornerSmoothness), 4.2);

    public static readonly StyledProperty<double> GlassRadiusProperty =
        AvaloniaProperty.Register<SquircleGlassCard, double>(nameof(GlassRadius), 22);

    /// <summary>
    /// When true (default) the control renders as real frosted glass — acrylic blur,
    /// sheen, specular border — for chrome surfaces (nav, search, panels, hero card).
    /// When false it renders as a plain solid squircle with no translucency, which is
    /// what real app icons look like (see Continue Playing tiles) — glass is chrome,
    /// not content.
    /// </summary>
    public static readonly StyledProperty<bool> IsGlassProperty =
        AvaloniaProperty.Register<SquircleGlassCard, bool>(nameof(IsGlass), true);

    /// <summary>
    /// When true, renders as a near-opaque modal sheet (same backing as menus/
    /// popovers) instead of the ~7%-alpha glass fill. Use this for editor/settings
    /// panels that sit over the rest of the UI — real translucent glass over
    /// arbitrary scrolled content behind it reads as a rendering bug, not a design
    /// choice, once there's text back there. Takes priority over IsGlass.
    /// </summary>
    public static readonly StyledProperty<bool> IsSheetProperty =
        AvaloniaProperty.Register<SquircleGlassCard, bool>(nameof(IsSheet), false);

    public bool IsSheet
    {
        get => GetValue(IsSheetProperty);
        set => SetValue(IsSheetProperty, value);
    }

    public bool IsGlass
    {
        get => GetValue(IsGlassProperty);
        set => SetValue(IsGlassProperty, value);
    }

    public double CornerSmoothness
    {
        get => GetValue(CornerSmoothnessProperty);
        set => SetValue(CornerSmoothnessProperty, value);
    }

    public double GlassRadius
    {
        get => GetValue(GlassRadiusProperty);
        set => SetValue(GlassRadiusProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        if (finalSize.Width > 0 && finalSize.Height > 0)
        {
            Clip = SuperellipseGeometry.Create(finalSize.Width, finalSize.Height, GlassRadius, CornerSmoothness);
        }

        return result;
    }

    static SquircleGlassCard()
    {
        AffectsArrange<SquircleGlassCard>(GlassRadiusProperty, CornerSmoothnessProperty);
    }
}
