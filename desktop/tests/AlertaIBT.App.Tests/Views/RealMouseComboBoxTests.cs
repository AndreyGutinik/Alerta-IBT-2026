using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AlertaIBT.App.Tests.Views;

// Every list box (ComboBox) of the Som tab clicked with the real input path in a real, shown window. The owner's bug: "os menus em cascata nao
// estao clicaveis, eu clico e nao aparece nada mesmo tendo a lista cadastrada".
public sealed class RealMouseComboBoxTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void Reveal(RealInputRig rig, FrameworkElement element)
    {
        element.BringIntoView();
        RealInputRig.Pump();
    }

    private static List<ComboBox> Combos(RealInputRig rig) => Descendants<ComboBox>(rig.Page).ToList();

    [Fact]
    public void The_Som_page_has_the_expected_list_boxes_and_they_all_have_items()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var combos = Combos(rig);

            // Lista de musicas, fade-in, fade-out, mescla, suavizar volume, the three outputs and the Lista of each of the two alarms.
            Assert.Equal(10, combos.Count);
            Assert.All(combos, combo => Assert.True(combo.Items.Count > 0, combo.GetType().Name + " " + combo.Name));
        });
    }

    [Fact]
    public void A_click_opens_every_list_box_shows_its_items_and_a_click_on_an_item_chooses_it_and_closes()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            foreach (var combo in Combos(rig))
            {
                var name = System.Windows.Automation.AutomationProperties.GetName(combo) + " " + combo.Name;
                Reveal(rig, combo);
                Assert.False(combo.IsDropDownOpen, name);

                rig.Click(rig.CenterOf(combo));
                RealInputRig.Pump();
                Assert.True(combo.IsDropDownOpen, "should open: " + name);

                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                Assert.True(popup.IsOpen, name);
                var items = Descendants<ComboBoxItem>(popup.Child).ToList();
                Assert.Equal(combo.Items.Count, items.Count);
                Assert.All(items, item => Assert.True(item.ActualHeight > 0 && item.ActualWidth > 0, name));

                // choose the last item that is not the selected one
                var target = items.Last(item => !item.IsSelected);
                target.BringIntoView(); // a long list (8 rows, then it scrolls) shows the item before it is clicked, as a person scrolls to it
                RealInputRig.Pump();
                var wanted = combo.ItemContainerGenerator.ItemFromContainer(target);
                rig.Click(rig.CenterOf(target));
                RealInputRig.Pump();
                Assert.False(combo.IsDropDownOpen, "should close after a choice: " + name);
                Assert.True(ReferenceEquals(wanted, combo.SelectedItem), "should choose the clicked item: " + name + " wanted " + wanted + " got " + combo.SelectedItem);
            }
        });
    }

    [Fact]
    public void Assistive_technology_can_expand_and_collapse_every_list_box()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            foreach (var combo in Combos(rig))
            {
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(combo);
                var pattern = (System.Windows.Automation.Provider.IExpandCollapseProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse);
                pattern.Expand();
                Assert.True(combo.IsDropDownOpen);
                pattern.Collapse();
                Assert.False(combo.IsDropDownOpen);
            }
        });
    }

    [Fact]
    public void Escape_and_a_click_outside_close_an_open_list_box()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var combo = Combos(rig).First(c => c.Name == "PlayerOutputCombo");
            Reveal(rig, combo);

            rig.Click(rig.CenterOf(combo));
            RealInputRig.Pump();
            Assert.True(combo.IsDropDownOpen);
            rig.Key(0x1B); // Escape
            RealInputRig.Pump();
            Assert.False(combo.IsDropDownOpen);

            rig.Click(rig.CenterOf(combo));
            RealInputRig.Pump();
            Assert.True(combo.IsDropDownOpen);
            var outside = rig.At(rig.Page.ElapsedLabel, 0.5, 0.5);
            Reveal(rig, combo);
            rig.Click(new Point(5, 5));
            RealInputRig.Pump();
            Assert.False(combo.IsDropDownOpen);
        });
    }

    [Fact]
    public void A_long_list_shows_eight_rows_then_scrolls_and_the_row_under_the_mouse_and_the_chosen_one_are_marked()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new RealInputRig();
            var combo = new ComboBox
            {
                Style = (Style)Application.Current.FindResource("IbtComboBox"),
                Width = 210,
                ItemsSource = Enumerable.Range(1, 15).Select(i => "Musica " + i).ToList(),
                SelectedIndex = 0,
            };
            var host = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 120,
                Top = 120,
                Width = 360,
                Height = 200,
                ShowActivated = false,
                ShowInTaskbar = false,
                Content = new StackPanel { Margin = new Thickness(20), Children = { combo } },
            };
            try
            {
                host.Show();
                rig.Target = host;
                RealInputRig.Pump();
                var screen = combo.PointToScreen(new Point(combo.ActualWidth / 2, combo.ActualHeight / 2));
                rig.ClickAtScreen(screen);
                RealInputRig.Pump();
                Assert.True(combo.IsDropDownOpen);

                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                var items = Descendants<ComboBoxItem>(popup.Child).Where(item => item.ActualHeight > 0).ToList();
                var scroller = Descendants<ScrollViewer>(popup.Child).First();
                Assert.Equal(Visibility.Visible, scroller.ComputedVerticalScrollBarVisibility);
                Assert.InRange(scroller.ActualHeight / items[0].ActualHeight, 7.9, 8.1); // eight rows, then the rest scrolls

                Assert.NotEqual(Colors.Transparent, RowColor(items[0])); // the chosen one is marked
                rig.MouseMoveAtScreen(items[2].PointToScreen(new Point(20, 10)));
                RealInputRig.Pump();
                Assert.True(items[2].IsHighlighted);
                Assert.NotEqual(Colors.Transparent, RowColor(items[2])); // and so is the row under the mouse
                Assert.Equal(Colors.Transparent, RowColor(items[4]));
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static Color RowColor(ComboBoxItem item) =>
        ((item.Template.FindName("Item", item) as Border)?.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
}
