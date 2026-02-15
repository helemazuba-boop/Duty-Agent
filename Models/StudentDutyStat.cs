namespace DutyAgent.Models;

public class StudentDutyStat
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DutyCount { get; set; }
    public string NextDutyDate { get; set; } = string.Empty;
}
