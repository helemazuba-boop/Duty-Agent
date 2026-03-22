namespace DutyAgent.Models;

public record struct CoreRunProgress(string Phase, string Message, string? StreamChunk = null);

public class CoreRunResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? AiResponse { get; init; }

    public static CoreRunResult Ok(string message = "", string? aiResponse = null) => 
        new() { Success = true, Message = message, AiResponse = aiResponse };

    public static CoreRunResult Fail(string message = "", string? code = null) => 
        new() { Success = false, Message = message, Code = code };
}
