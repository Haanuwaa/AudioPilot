using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioPilot.Controls;

/// <summary>Exposes realized group-header controls without realizing additional mixer rows.</summary>
public sealed class MixerListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new MixerListBoxAutomationPeer(this);

    private sealed class MixerListBoxAutomationPeer(MixerListBox owner) : ListBoxAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetChildrenCore()
        {
            List<AutomationPeer> children = base.GetChildrenCore() ?? [];
            foreach (MixerGroupHeader header in FindHeaders(owner))
            {
                if (!header.IsVisible || UIElementAutomationPeer.CreatePeerForElement(header) is not { } peer) continue;
                if (children.Contains(peer)) continue;
                DependencyObject? group = VisualTreeHelper.GetParent(header);
                while (group != null && group is not GroupItem) group = VisualTreeHelper.GetParent(group);
                int index = children.FindIndex(child => child is FrameworkElementAutomationPeer element && ReferenceEquals(element.Owner, group));
                children.Insert(index < 0 ? children.Count : index, peer);
            }
            return children;
        }
    }

    private static IEnumerable<MixerGroupHeader> FindHeaders(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListBoxItem) continue;
            if (child is MixerGroupHeader header) yield return header;
            else foreach (MixerGroupHeader nested in FindHeaders(child)) yield return nested;
        }
    }
}

public sealed class MixerGroupHeader : StackPanel
{
    public MixerGroupHeader()
    {
        Loaded += InvalidateListPeer;
        Unloaded += InvalidateListPeer;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeaderAutomationPeer(this);

    private sealed class HeaderAutomationPeer(MixerGroupHeader owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }

    private void InvalidateListPeer(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is MixerListBox list)
            {
                UIElementAutomationPeer.FromElement(list)?.InvalidatePeer();
                return;
            }
    }
}
