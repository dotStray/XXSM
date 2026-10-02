using System.Text.Json.Serialization;

namespace Xxsm.Core.Mods;

/// <summary>What asked for a set of mods to be switched on and off.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModSwitchSource>))]
public enum ModSwitchSource
{
    /// <summary>A saved profile was applied.</summary>
    [JsonStringEnumMemberName("profile")]
    Profile,

    /// <summary>The randomiser chose one mod per character.</summary>
    [JsonStringEnumMemberName("randomiser")]
    Randomiser,

    /// <summary>Every mod was switched off at once, from the Mods page or <c>xxsm mod disable-all</c>.</summary>
    [JsonStringEnumMemberName("all-off")]
    AllOff,
}

/// <summary>One mod to switch on or off.</summary>
/// <param name="ModFolder">The mod folder as it is now, in either state.</param>
/// <param name="Enable">Whether it should end up switched on.</param>
public sealed record ModSwitch(string ModFolder, bool Enable);

/// <summary>What happened to one requested switch.</summary>
/// <param name="Switch">What was asked for.</param>
/// <param name="Result">The rename that was made, or <c>null</c> when it failed.</param>
/// <param name="Error">Why it failed, in the operating system's words, or <c>null</c>.</param>
public sealed record ModSwitchOutcome(ModSwitch Switch, ModOperationResult? Result, string? Error)
{
    /// <summary>Whether the mod is now in the state that was asked for.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Whether a folder was actually renamed.</summary>
    public bool Changed => Result?.Changed == true;

    /// <summary>The mod's folder name without any disabled prefix, for saying which mod this was.</summary>
    public string ModName => ModsFolderLayout.StripDisabledPrefix(Path.GetFileName(Switch.ModFolder));
}

/// <summary>The result of switching a set of mods in one go.</summary>
public sealed record ModSwitchRunResult
{
    /// <summary>The id to undo the whole set by.</summary>
    public required string RunId { get; init; }

    /// <summary>When it ran.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>One outcome per requested switch, in the order they were made.</summary>
    public required IReadOnlyList<ModSwitchOutcome> Outcomes { get; init; }

    /// <summary>How many folders were renamed.</summary>
    public int ChangedCount => Outcomes.Count(outcome => outcome.Changed);

    /// <summary>How many were switched on.</summary>
    public int EnabledCount => Outcomes.Count(outcome => outcome.Changed && outcome.Switch.Enable);

    /// <summary>How many were switched off.</summary>
    public int DisabledCount => Outcomes.Count(outcome => outcome.Changed && !outcome.Switch.Enable);

    /// <summary>The switches that could not be made.</summary>
    public IReadOnlyList<ModSwitchOutcome> Failures => [.. Outcomes.Where(outcome => !outcome.Succeeded)];
}

/// <summary>What happened to one switch when its run was undone.</summary>
/// <param name="From">Where the mod was left by the run, relative to the Mods folder.</param>
/// <param name="To">Where it is now, relative to the Mods folder.</param>
/// <param name="Restored">Whether it was put back.</param>
/// <param name="Reason">Why it was not, or <c>null</c>.</param>
public sealed record ModSwitchUndoOutcome(string From, string To, bool Restored, string? Reason);

/// <summary>The result of undoing a switch run.</summary>
public sealed record ModSwitchUndoResult
{
    /// <summary>The run that was undone.</summary>
    public required string RunId { get; init; }

    /// <summary>One outcome per switch the run made.</summary>
    public required IReadOnlyList<ModSwitchUndoOutcome> Outcomes { get; init; }

    /// <summary>How many mods were put back.</summary>
    public int RestoredCount => Outcomes.Count(outcome => outcome.Restored);

    /// <summary>The switches that could not be put back.</summary>
    public IReadOnlyList<ModSwitchUndoOutcome> NotRestored => [.. Outcomes.Where(outcome => !outcome.Restored)];
}

/// <summary>One past switch run, as a list of what can be undone shows it.</summary>
/// <param name="RunId">The run's id.</param>
/// <param name="At">When it ran.</param>
/// <param name="Source">What asked for it.</param>
/// <param name="Label">The profile's name, for a profile; otherwise <c>null</c>.</param>
/// <param name="SwitchCount">How many mods it switched.</param>
/// <param name="UndoneCount">How many of those have since been put back.</param>
public sealed record ModSwitchRunSummary(
    string RunId,
    DateTimeOffset At,
    ModSwitchSource Source,
    string? Label,
    int SwitchCount,
    int UndoneCount)
{
    /// <summary>Whether every switch in the run has been undone.</summary>
    public bool IsFullyUndone => SwitchCount > 0 && UndoneCount >= SwitchCount;
}
