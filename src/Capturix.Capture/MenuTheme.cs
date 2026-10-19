using System.Runtime.InteropServices;

namespace Capturix.Capture;

/// <summary>Light/dark colours for the tray menu (WinForms has no built-in dark mode).</summary>
internal static class MenuTheme
{
    public static void Apply(ToolStrip strip, bool dark)
    {
        strip.Renderer = dark ? new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false } : new ToolStripProfessionalRenderer();
        ApplyColors(strip.Items, dark);
    }

    private static void ApplyColors(ToolStripItemCollection items, bool dark)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = dark ? Color.FromArgb(232, 232, 232) : SystemColors.ControlText;
            if (item is ToolStripMenuItem { HasDropDownItems: true } menu)
            {
                menu.DropDown.Renderer = dark ? new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false } : new ToolStripProfessionalRenderer();
                ApplyColors(menu.DropDownItems, dark);
            }
        }
    }

    /// <summary>Dark title bar for the few WinForms dialogs Capturix shows.</summary>
    public static void ApplyTitleBar(Form form, bool dark)
    {
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(43, 43, 43);
        private static readonly Color Hover = Color.FromArgb(62, 62, 64);
        private static readonly Color Line = Color.FromArgb(70, 70, 70);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Color.FromArgb(0, 120, 200);
        public override Color CheckSelectedBackground => Color.FromArgb(0, 140, 230);
        public override Color CheckPressedBackground => Color.FromArgb(0, 140, 230);
    }
}
