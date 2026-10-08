using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AccessRem.Ui;

/// <summary>
/// Gives every control its own accessible name, so assistive technology never has to infer it from the static
/// text inside the control. Buttons, radio buttons, check boxes and tabs are named from their visible text;
/// edit boxes, combo boxes, sliders and lists are named from (and linked to) the label that targets them.
/// Registered once for the whole application, so it also covers content that loads later, such as tab pages.
/// </summary>
public static class AccessibleNames
{
    /// <summary>
    /// Name every control in a window when it loads. The logical tree is walked rather than the visual tree,
    /// so pages of tab controls that are not currently shown are named too.
    /// </summary>
    public static void Attach(Window window)
    {
        window.Loaded += (_, _) => Apply(window);
    }

    public static void Apply(DependencyObject root)
    {
        switch (root)
        {
            case Label label:
                NameTarget(label);
                break;
            case TabItem tab:
                NameTab(tab);
                break;
            case GroupBox group:
                NameGroup(group);
                break;
            case ButtonBase button:
                NameFromContent(button);
                break;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            Apply(child);
    }

    /// <summary>Visible text with access key markers removed ("_Add…" → "Add…", "a__b" → "a_b").</summary>
    public static string StripAccessKey(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_')
            {
                if (i + 1 < text.Length && text[i + 1] == '_')
                {
                    sb.Append('_');
                    i++;
                }
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>The text a person sees for a piece of content.</summary>
    public static string? TextOf(object? content) => content switch
    {
        null => null,
        string s => StripAccessKey(s),
        AccessText a => StripAccessKey(a.Text),
        TextBlock t => StripAccessKey(t.Text),
        Panel p => p.Children.OfType<UIElement>().Select(TextOf).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
        ContentControl c => TextOf(c.Content),
        Decorator d => TextOf(d.Child),
        _ => null,
    };

    private static bool HasName(DependencyObject element) => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(element));

    private static void NameFromContent(FrameworkElement element)
    {
        if (HasName(element) || element is not ContentControl control)
            return;
        var text = TextOf(control.Content);
        if (!string.IsNullOrWhiteSpace(text))
            AutomationProperties.SetName(element, text);
    }

    private static void NameTab(TabItem tab)
    {
        if (HasName(tab))
            return;
        var text = TextOf(tab.Header);
        if (!string.IsNullOrWhiteSpace(text))
            AutomationProperties.SetName(tab, text);
    }

    private static void NameGroup(GroupBox group)
    {
        if (HasName(group))
            return;
        var text = TextOf(group.Header);
        if (!string.IsNullOrWhiteSpace(text))
            AutomationProperties.SetName(group, text);
    }

    private static void NameTarget(Label label, bool retry = true)
    {
        if (label.Target is null)
            System.Windows.Data.BindingOperations.GetBindingExpression(label, Label.TargetProperty)?.UpdateTarget();
        if (label.Target is not FrameworkElement target)
        {
            // ElementName bindings can resolve slightly after the window loads.
            if (retry)
                label.Dispatcher.BeginInvoke(() => NameTarget(label, retry: false), System.Windows.Threading.DispatcherPriority.ContextIdle);
            return;
        }
        var text = TextOf(label.Content)?.TrimEnd(':').Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (!HasName(target))
            AutomationProperties.SetName(target, text);
        if (AutomationProperties.GetLabeledBy(target) is null)
            AutomationProperties.SetLabeledBy(target, label);
    }

    /// <summary>
    /// Lists interactive controls under <paramref name="root"/> that lack their own accessible name
    /// (developer check, used by the snapshot mode).
    /// </summary>
    public static IEnumerable<string> Audit(DependencyObject root)
    {
        var problems = new List<string>();
        Walk(root, problems);
        return problems;
    }

    private static void Walk(DependencyObject node, List<string> problems)
    {
        if (node is FrameworkElement fe && fe.IsVisible && IsInteractive(fe) && !IsTemplatePart(fe))
        {
            var own = AutomationProperties.GetName(fe);
            var peerName = UIElementAutomationPeer.CreatePeerForElement(fe)?.GetName();
            if (string.IsNullOrWhiteSpace(own))
                problems.Add($"{fe.GetType().Name} {(string.IsNullOrEmpty(fe.Name) ? "" : "'" + fe.Name + "' ")}has no name of its own (UIA name: '{peerName}')");
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            Walk(VisualTreeHelper.GetChild(node, i), problems);
    }

    private static bool IsInteractive(FrameworkElement fe) =>
        fe is ButtonBase or TextBox or PasswordBox or ComboBox or Slider or ListBox or TabControl or TabItem or Menu
        && fe is not ListBoxItem and not ComboBoxItem;

    /// <summary>Parts of a control's own template (scroll bar buttons, a combo box's toggle) are not separate controls.</summary>
    private static bool IsTemplatePart(FrameworkElement fe) => fe.TemplatedParent is not null and not ContentPresenter;
}
