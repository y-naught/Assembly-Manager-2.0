using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssemblyManagerPlugin.UI;
using Eto.Forms;
using WpfButton = System.Windows.Controls.Button;

internal static class Program
{
    private static string _pluginPath = string.Empty;
    private static string _rhinoSystemPath = string.Empty;

    [STAThread]
    private static int Main(string[] args)
    {
        _pluginPath = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory,
            "../../../../../AssemblyManagerPlugin/bin/Release/net7.0/Gazelle.rhp"));
        _rhinoSystemPath = Environment.GetEnvironmentVariable("GAZELLE_TEST_RHINO_SYSTEM")
            ?? @"C:\Program Files\Rhino 8\System";
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = name.Name == "Gazelle" ? _pluginPath : Path.Combine(_rhinoSystemPath, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        try { return CheckLayout(); }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CheckLayout()
    {
        // Instantiate the real Eto/WPF dialog, but never show a window, initialize
        // RhinoCore, register a plugin, or read the operator's model/settings.
        using var app = new Eto.Forms.Application(Eto.Platforms.Wpf);
        using var dialog = new BomColumnsDialog();
        // Eto materializes table rows on preload. Exercise that normal control
        // lifecycle without showing a desktop window or starting a modal loop.
        foreach (var lifecycle in new[] { "OnPreLoad", "OnLoad", "OnLoadComplete" })
            typeof(Control).GetMethod(lifecycle, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dialog, new object[] { EventArgs.Empty });
        Require(dialog.ClientSize.Width == 390, "The initial dialog width must stay compact.");
        var outer = dialog.Content as TableLayout
            ?? throw new InvalidOperationException("The dialog must use independent single-column sections.");
        var scroll = outer.Children.OfType<Scrollable>().Single();
        Require(!scroll.Children.OfType<Button>().Any(), "Action buttons must stay outside the scrolling checklist.");
        var choices = scroll.Children.OfType<CheckBox>().ToArray();
        Require(choices.Length == 10 && dialog.SelectedColumnIds.Count == 8, "Column choices and defaults must be preserved.");
        Require(dialog.DefaultButton is { Text: "Confirm", Enabled: true } && dialog.AbortButton?.Text == "Cancel",
            "The default and escape actions must stay available.");
        var nativeWindow = (System.Windows.Window)dialog.ControlObject;
        var surface = (FrameworkElement)nativeWindow.Content;

        CheckSize(surface, dialog, 390, 480, "default");
        CheckSize(surface, dialog, 330, 280, "small");
        CheckSize(surface, dialog, 600, 600, "expanded");

        var clear = outer.Children.OfType<Button>().Single(button => button.Text == "Clear");
        ((WpfButton)clear.ControlObject).RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
        Require(dialog.SelectedColumnIds.Count == 0 && !dialog.DefaultButton!.Enabled,
            "Clear must uncheck all columns and disable Confirm.");
        choices[0].Checked = true;
        Require(dialog.SelectedColumnIds.Count == 1 && dialog.DefaultButton!.Enabled,
            "Selecting a column must re-enable Confirm.");
        var selectAll = outer.Children.OfType<Button>().Single(button => button.Text == "Select All");
        ((WpfButton)selectAll.ControlObject).RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
        Require(dialog.SelectedColumnIds.Count == 10 && dialog.DefaultButton!.Enabled,
            "Select All must check every column and enable Confirm.");
        Console.WriteLine("PASS BOM dialog: compact sizing, fixed actions, scrolling checklist and selection behavior.");
        return 0;
    }

    private static void CheckSize(FrameworkElement surface, BomColumnsDialog dialog, int width, int height, string name)
    {
        dialog.ClientSize = new Eto.Drawing.Size(width, height);
        surface.Measure(new System.Windows.Size(width, height));
        Require(surface.DesiredSize.Width <= width + 0.1,
            $"The {name} content must not request extra horizontal space ({surface.DesiredSize}).");
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        foreach (var button in ((Container)dialog.Content!).Children.OfType<Button>())
        {
            var native = (FrameworkElement)button.ControlObject;
            var bounds = native.TransformToAncestor(surface).TransformBounds(new Rect(native.RenderSize));
            Require(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Right <= width + 0.1
                && bounds.Top >= 0 && bounds.Bottom <= height + 0.1,
                $"{button.Text} must remain visible within the {name} dialog ({bounds}).");
        }
        foreach (var label in ((Container)dialog.Content!).Children.OfType<Label>())
        {
            var native = (FrameworkElement)label.ControlObject;
            Require(native.DesiredSize.Width <= native.ActualWidth + native.Margin.Left + native.Margin.Right + 0.1
                && native.DesiredSize.Height <= native.ActualHeight + native.Margin.Top + native.Margin.Bottom + 0.1,
                $"Instructions must fit without clipping at the {name} size: desired={native.DesiredSize}, actual={native.RenderSize}, margin={native.Margin}.");
        }
        // Render the actual off-screen WPF control tree for visual review.
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen())
            drawing.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var outputPath = Path.Combine(AppContext.BaseDirectory, $"bom-dialog-{name}.png");
        using var output = File.Create(outputPath);
        encoder.Save(output);
        Console.WriteLine($"PASS {name}: {width}x{height}; preview {outputPath}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
