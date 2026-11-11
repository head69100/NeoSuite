using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using NeoBoost.Services;

namespace NeoBoost.Views;

/// <summary>Santé du PC : disques (type, santé, usure), batterie, mémoire.</summary>
public sealed partial class HealthPage : Page
{
    public HealthPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        var disksTask = HealthReport.DisksAsync();
        var batteryTask = HealthReport.BatteryAsync();
        var snapshot = await Task.Run(SystemMonitor.Sample);
        var disks = await disksTask;
        var battery = await batteryTask;

        BuildDisks(disks);
        BuildBattery(battery);
        BuildMemory(snapshot);

        LoadingRing.IsActive = false;
        ContentPanel.Visibility = Visibility.Visible;
    }

    private void BuildDisks(List<DiskHealth> disks)
    {
        DisksPanel.Children.Clear();
        if (disks.Count == 0)
        {
            DisksPanel.Children.Add(new TextBlock
            {
                Text = "Aucun disque détecté (les informations ne sont pas accessibles sur ce PC).",
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            });
            return;
        }

        foreach (var disk in disks)
        {
            var card = new Border { Style = (Style)Application.Current.Resources["CardStyle"] };
            var panel = new StackPanel { Spacing = 6 };

            var head = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(new TextBlock
            {
                Text = disk.Model,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });
            head.Children.Add(new Border
            {
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources[
                    disk.Healthy == false ? "SystemFillColorCriticalBrush"
                    : disk.Healthy == true ? "SystemFillColorSuccessBrush"
                    : "SubtleFillColorSecondaryBrush"],
                Child = new TextBlock
                {
                    Text = disk.HealthText,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                },
            });
            panel.Children.Add(head);

            var facts = new List<string>
            {
                SystemMonitor.FormatBytes(disk.Size),
                disk.Kind == DiskKind.Ssd ? "SSD" : disk.Kind == DiskKind.Hdd ? "Disque dur" : "Type inconnu",
            };
            if (disk.Interface is { Length: > 0 } bus)
                facts.Add(bus);
            if (disk.PowerOnHours is { } hours)
                facts.Add($"{hours:N0} h de fonctionnement");
            if (disk.WearPercent is { } wear)
                facts.Add($"usure : {wear:0} %");
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", facts),
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            });

            if (disk.Detail is not null)
                panel.Children.Add(new TextBlock
                {
                    Text = disk.Detail,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                });

            card.Child = panel;
            DisksPanel.Children.Add(card);
        }
    }

    private void BuildBattery(HealthReport.BatteryInfo? battery)
    {
        if (battery is null)
            return; // ordinateur de bureau : la section reste masquée

        BatteryTitle.Visibility = Visibility.Visible;
        BatteryCard.Visibility = Visibility.Visible;

        var state = battery.Charging == true ? "En charge" : battery.Charging == false ? "Sur batterie" : "";
        BatteryStateText.Text = $"{state} · {battery.ChargePercent?.ToString() ?? "?"} %";

        BatteryWearText.Text = battery.WearPercent is { } wear
            ? wear < 10
                ? $"Batterie en bonne santé : capacité perdue {wear:0} %."
                : wear < 30
                    ? $"Batterie correcte : capacité perdue {wear:0} %. Elle tient moins longtemps qu'au neuf."
                    : $"Batterie usée : capacité perdue {wear:0} %. Pensez à la remplacer si l'autonomie ne vous convient plus."
            : "L'usure de la batterie n'est pas lisible sur ce modèle.";
    }

    private void BuildMemory(SystemSnapshot snapshot)
    {
        MemoryText.Text = $"{SystemMonitor.FormatBytes(snapshot.RamUsed)} utilisés sur {SystemMonitor.FormatBytes(snapshot.RamTotal)} ({snapshot.RamPercent:0} %)";
        MemoryAdviceText.Text = snapshot.RamPercent switch
        {
            >= 90 => "Mémoire presque saturée : fermez les programmes actifs les plus gourmands pour retrouver de la réactivité.",
            >= 75 => "Mémoire bastante chargée : si le PC ralentit, commencez par les programmes actifs les plus gourmands.",
            _ => "Mémoire confortable : rien à faire.",
        };
    }
}
