namespace DutyAgentBridge.Models;

public sealed record BridgeConfigCandidate(
    string ConfigFolder,
    string Source,
    bool Exists,
    bool HasLiveMeta);

public sealed record BridgeConfigDiscovery(
    string ConfigFolder,
    string Source,
    IReadOnlyList<BridgeConfigCandidate> Candidates);
