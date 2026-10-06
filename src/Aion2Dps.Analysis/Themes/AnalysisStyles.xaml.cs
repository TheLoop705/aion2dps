using System.Windows;

namespace Aion2Dps.Analysis;

/// <summary>
/// Control styles (scrollbars, buttons, tabs, combo boxes, check boxes, tooltips) used inside the analysis views.
/// They reference the app theme only through <c>{DynamicResource Theme.*}</c> and define no theme keys,
/// so each root view merges its own instance without shadowing the theme.
/// </summary>
public partial class AnalysisStyles : ResourceDictionary
{
    public AnalysisStyles()
    {
        InitializeComponent();
    }
}
