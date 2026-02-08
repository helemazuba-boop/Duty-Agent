using System.Diagnostics;
using System.Text;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyIsland.Services;

namespace DutyIsland.Views.SettingPages;

[FullWidthPage]
[HidePageTitle]
[SettingsPageInfo("duty.settings", "Duty-Agent", "\uE31E", "\uE31E")]
public partial class DutyMainSettingsPage : SettingsPageBase
{
    private DutyBackendService Service { get; } = IAppHost.GetService<DutyBackendService>();

    public DutyMainSettingsPage()
    {
        InitializeComponent();
        ApplyModeComboBox.SelectedIndex = 0;
        LoadData();
    }

    private async void OnRunAgentClick(object? sender, RoutedEventArgs e)
    {
        var instruction = InstructionBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(instruction))
        {
            StatusText.Text = "Please enter an instruction.";
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        var applyMode = "append";
        if (ApplyModeComboBox.SelectedIndex == 1) applyMode = "replace_future";
        if (ApplyModeComboBox.SelectedIndex == 2) applyMode = "replace_overlap";
        if (ApplyModeComboBox.SelectedIndex == 3) applyMode = "replace_all";

        try
        {
            RunAgentBtn.IsEnabled = false;
            StatusText.Text = "Running...";
            StatusText.Foreground = Brushes.Gray;

            var success = await Task.Run(() => Service.RunCoreAgent(instruction, applyMode));
            if (success)
            {
                StatusText.Text = "Schedule generated.";
                StatusText.Foreground = Brushes.Green;
                LoadData();
            }
            else
            {
                StatusText.Text = "Schedule generation failed.";
                StatusText.Foreground = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            RunAgentBtn.IsEnabled = true;
        }
    }

    private void LoadData()
    {
        var state = Service.LoadState();
        var builder = new StringBuilder();
        foreach (var item in state.SchedulePool)
        {
            var classroom = string.Join(", ", item.ClassroomStudents);
            var cleaning = string.Join(", ", item.CleaningAreaStudents);
            builder.AppendLine($"{item.Date} ({item.Day}): classroom:[{classroom}] cleaning:[{cleaning}]");
        }
        ScheduledStudentsBox.Text = builder.ToString();

        var stats = new Dictionary<string, int>();
        foreach (var item in state.SchedulePool)
        {
            foreach (var s in item.ClassroomStudents.Concat(item.CleaningAreaStudents))
            {
                if (!stats.ContainsKey(s))
                {
                    stats[s] = 0;
                }
                stats[s]++;
            }
        }

        var statBuilder = new StringBuilder();
        foreach (var kv in stats.OrderByDescending(x => x.Value))
        {
            statBuilder.AppendLine($"{kv.Key}: {kv.Value} times");
        }
        StudentStatsBox.Text = statBuilder.ToString();
    }

    private void OnOpenRosterClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Service.GetRosterPath();
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
        }
    }
}
