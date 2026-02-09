using System.Diagnostics;
using System.Text;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyIsland.Models;
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
        LoadConfigForm();
        LoadData();
    }

    private void OnSaveConfigClick(object? sender, RoutedEventArgs e)
    {
        if (!TryParseBoundedInt(CoverageDaysBox.Text, 1, 30, out var coverageDays))
        {
            SetStatus("Coverage Days must be an integer between 1 and 30.", Brushes.Orange);
            return;
        }

        if (!TryParseBoundedInt(PerDayBox.Text, 1, 30, out var perDay))
        {
            SetStatus("Per Area Per Day must be an integer between 1 and 30.", Brushes.Orange);
            return;
        }

        var autoRunTime = string.IsNullOrWhiteSpace(AutoRunTimeBox.Text) ? "08:00" : AutoRunTimeBox.Text.Trim();
        var componentRefreshTime = string.IsNullOrWhiteSpace(ComponentRefreshTimeBox.Text)
            ? "08:00"
            : ComponentRefreshTimeBox.Text.Trim();

        try
        {
            Service.SaveUserConfig(
                apiKey: ApiKeyBox.Text ?? string.Empty,
                baseUrl: BaseUrlBox.Text?.Trim() ?? string.Empty,
                model: ModelBox.Text?.Trim() ?? string.Empty,
                enableAutoRun: EnableAutoRunCheckBox.IsChecked == true,
                autoRunDay: GetSelectedAutoRunDay(),
                autoRunTime: autoRunTime,
                perDay: perDay,
                skipWeekends: SkipWeekendsCheckBox.IsChecked == true,
                dutyRule: DutyRuleBox.Text ?? string.Empty,
                startFromToday: StartFromTodayCheckBox.IsChecked == true,
                autoRunCoverageDays: coverageDays,
                componentRefreshTime: componentRefreshTime,
                pythonPath: PythonPathBox.Text?.Trim() ?? string.Empty);

            LoadConfigForm();
            SetStatus("Config saved.", Brushes.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}", Brushes.Red);
        }
    }

    private async void OnRunAgentClick(object? sender, RoutedEventArgs e)
    {
        var instruction = InstructionBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(instruction))
        {
            SetStatus("Please enter an instruction.", Brushes.Orange);
            return;
        }

        var applyMode = "append";
        if (ApplyModeComboBox.SelectedIndex == 1) applyMode = "replace_future";
        if (ApplyModeComboBox.SelectedIndex == 2) applyMode = "replace_overlap";
        if (ApplyModeComboBox.SelectedIndex == 3) applyMode = "replace_all";

        try
        {
            RunAgentBtn.IsEnabled = false;
            SetStatus("Running...", Brushes.Gray);

            var success = await Task.Run(() => Service.RunCoreAgent(instruction, applyMode));
            if (success)
            {
                SetStatus("Schedule generated.", Brushes.Green);
                LoadData();
            }
            else
            {
                SetStatus("Schedule generation failed.", Brushes.Red);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", Brushes.Red);
        }
        finally
        {
            RunAgentBtn.IsEnabled = true;
        }
    }

    private void LoadConfigForm()
    {
        Service.LoadConfig();
        var config = Service.Config;

        PythonPathBox.Text = config.PythonPath;
        ApiKeyBox.Text = config.DecryptedApiKey;
        BaseUrlBox.Text = config.BaseUrl;
        ModelBox.Text = config.Model;
        EnableAutoRunCheckBox.IsChecked = config.EnableAutoRun;
        AutoRunDayComboBox.SelectedIndex = GetAutoRunDayIndex(config.AutoRunDay);
        AutoRunTimeBox.Text = config.AutoRunTime;
        CoverageDaysBox.Text = config.AutoRunCoverageDays.ToString();
        PerDayBox.Text = config.PerDay.ToString();
        SkipWeekendsCheckBox.IsChecked = config.SkipWeekends;
        StartFromTodayCheckBox.IsChecked = config.StartFromToday;
        ComponentRefreshTimeBox.Text = config.ComponentRefreshTime;
        DutyRuleBox.Text = config.DutyRule;
    }

    private void LoadData()
    {
        var state = Service.LoadState();
        var builder = new StringBuilder();
        foreach (var item in state.SchedulePool)
        {
            var classroomStudents = GetClassroomStudents(item);
            var cleaningStudents = GetCleaningStudents(item);
            var classroom = classroomStudents.Count > 0 ? string.Join(", ", classroomStudents) : "-";
            var cleaning = cleaningStudents.Count > 0 ? string.Join(", ", cleaningStudents) : "-";
            builder.AppendLine($"{item.Date} ({item.Day}): classroom:[{classroom}] cleaning:[{cleaning}]");
        }

        ScheduledStudentsBox.Text = builder.ToString();

        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in state.SchedulePool)
        {
            foreach (var student in GetClassroomStudents(item).Concat(GetCleaningStudents(item)))
            {
                if (string.IsNullOrWhiteSpace(student))
                {
                    continue;
                }

                if (!stats.TryAdd(student, 1))
                {
                    stats[student]++;
                }
            }
        }

        var statBuilder = new StringBuilder();
        foreach (var kv in stats.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
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

    private static IReadOnlyList<string> GetClassroomStudents(SchedulePoolItem item)
    {
        return item.ClassroomStudents.Count > 0 ? item.ClassroomStudents : item.Students;
    }

    private static IReadOnlyList<string> GetCleaningStudents(SchedulePoolItem item)
    {
        return item.CleaningAreaStudents.Count > 0 ? item.CleaningAreaStudents : item.Students;
    }

    private static bool TryParseBoundedInt(string? text, int min, int max, out int value)
    {
        if (int.TryParse(text, out value))
        {
            return value >= min && value <= max;
        }

        value = min;
        return false;
    }

    private string GetSelectedAutoRunDay()
    {
        return AutoRunDayComboBox.SelectedIndex switch
        {
            0 => "Monday",
            1 => "Tuesday",
            2 => "Wednesday",
            3 => "Thursday",
            4 => "Friday",
            5 => "Saturday",
            6 => "Sunday",
            _ => "Monday"
        };
    }

    private static int GetAutoRunDayIndex(string? day)
    {
        var normalized = day?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "tue" or "tuesday" or "2" or "周二" or "星期二" or "鍛ㄤ簩" => 1,
            "wed" or "wednesday" or "3" or "周三" or "星期三" or "鍛ㄤ笁" => 2,
            "thu" or "thursday" or "4" or "周四" or "星期四" or "鍛ㄥ洓" => 3,
            "fri" or "friday" or "5" or "周五" or "星期五" or "鍛ㄤ簲" => 4,
            "sat" or "saturday" or "6" or "周六" or "星期六" or "鍛ㄥ叚" => 5,
            "sun" or "sunday" or "7" or "0" or "周日" or "周天" or "星期日" or "星期天" or "鍛ㄦ棩" => 6,
            _ => 0
        };
    }

    private void SetStatus(string text, IBrush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
    }
}
