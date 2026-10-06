namespace Aion2Dps.App.Theming;

/// <summary>Implicit and named control styles (buttons, combo boxes, sliders, scroll bars, menus) that read every colour
/// from the active palette through DynamicResource. Loadable without an Application instance (offscreen renders, tests).</summary>
public partial class ControlStyles : ResourceDictionary
{
    public ControlStyles()
    {
        InitializeComponent();
    }
}
