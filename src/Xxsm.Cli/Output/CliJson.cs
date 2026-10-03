using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xxsm.Cli.Output;

/// <summary>Serialisation for <c>--json</c> output, source-generated so it is trimming-safe.</summary>
internal static class CliJson
{
    /// <summary>Serialises a value using the CLI's source-generated context.</summary>
    /// <typeparam name="T">The payload type. Must be declared on <see cref="CliJsonContext"/>.</typeparam>
    public static string Serialize<T>(T value) where T : notnull =>
        JsonSerializer.Serialize(value, typeof(T), CliJsonContext.Default);

    /// <summary>Writes a value to standard output as JSON and returns the exit code given.</summary>
    /// <typeparam name="T">The payload type. Must be declared on <see cref="CliJsonContext"/>.</typeparam>
    public static int Write<T>(T value, int exitCode = 0) where T : notnull
    {
        Console.Out.WriteLine(Serialize(value));
        return exitCode;
    }
}

/// <summary>The source-generated serialisation context for every CLI JSON payload.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DiagnosticsReport))]
[JsonSerializable(typeof(VersionReport))]
[JsonSerializable(typeof(ConfigResetReport))]
[JsonSerializable(typeof(ScanReport))]
[JsonSerializable(typeof(Xxsm.Core.Mods.TrashMoveOutResult))]
[JsonSerializable(typeof(Xxsm.Core.Mods.LeftoverTidyResult))]
[JsonSerializable(typeof(PackImportReport))]
[JsonSerializable(typeof(PackListReport))]
[JsonSerializable(typeof(PackCatalogReport))]
[JsonSerializable(typeof(PackOperationReport))]
[JsonSerializable(typeof(PackUpdateReport))]
[JsonSerializable(typeof(SkippedUpdatesRecordReport))]
[JsonSerializable(typeof(PackChangesReport))]
[JsonSerializable(typeof(PackAutoUpdateReport))]
[JsonSerializable(typeof(PackPinReport))]
[JsonSerializable(typeof(PackUseReport))]
[JsonSerializable(typeof(PackKeepReport))]
[JsonSerializable(typeof(PackPruneReport))]
[JsonSerializable(typeof(PackRemovalReport))]
[JsonSerializable(typeof(PackRestoreReport))]
[JsonSerializable(typeof(IniReport))]
[JsonSerializable(typeof(IniSetReport))]
[JsonSerializable(typeof(ModSignalReport))]
[JsonSerializable(typeof(List<ModPreviewReport>))]
[JsonSerializable(typeof(SortReport))]
[JsonSerializable(typeof(SortIndexReport))]
[JsonSerializable(typeof(ModsInventoryReport))]
[JsonSerializable(typeof(ModOperationReport))]
[JsonSerializable(typeof(ModFilingForgetReport))]
[JsonSerializable(typeof(ModPreviewSetReport))]
[JsonSerializable(typeof(ModPreviewClearReport))]
[JsonSerializable(typeof(ModDisplayNameReport))]
[JsonSerializable(typeof(ModDetailsReport))]
[JsonSerializable(typeof(ModNameProposalReport))]
[JsonSerializable(typeof(ModFetchReport))]
[JsonSerializable(typeof(ModUpdatesReport))]
[JsonSerializable(typeof(ModUpdateAcceptReport))]
[JsonSerializable(typeof(ModUpdatePretendReport))]
[JsonSerializable(typeof(ModLinkReport))]
[JsonSerializable(typeof(ModUpdatePlanReport))]
[JsonSerializable(typeof(ModDownloadsReport))]
[JsonSerializable(typeof(SortRunReport))]
[JsonSerializable(typeof(SortUndoReport))]
[JsonSerializable(typeof(SortRunsReport))]
[JsonSerializable(typeof(ProfileReport))]
[JsonSerializable(typeof(ProfileListReport))]
[JsonSerializable(typeof(ProfileSaveReport))]
[JsonSerializable(typeof(ProfileEditReport))]
[JsonSerializable(typeof(ProfileDeleteReport))]
[JsonSerializable(typeof(ProfileApplyReport))]
[JsonSerializable(typeof(DisableAllReport))]
[JsonSerializable(typeof(SwitchRunsReport))]
[JsonSerializable(typeof(SwitchUndoReport))]
[JsonSerializable(typeof(RandomiseReport))]
[JsonSerializable(typeof(ModKeysReport))]
[JsonSerializable(typeof(ModDefaultsReport))]
[JsonSerializable(typeof(ModExportReport))]
[JsonSerializable(typeof(ModImportReport))]
[JsonSerializable(typeof(ConfigReport))]
[JsonSerializable(typeof(List<CharacterReport>))]
[JsonSerializable(typeof(CharacterReport))]
[JsonSerializable(typeof(CharacterEditReport))]
[JsonSerializable(typeof(CharacterDeleteReport))]
[JsonSerializable(typeof(CharacterRestoreReport))]
[JsonSerializable(typeof(CharacterLearnReport))]
[JsonSerializable(typeof(HashReadReport))]
[JsonSerializable(typeof(ModAddReport))]
[JsonSerializable(typeof(List<StudioDraftReport>))]
[JsonSerializable(typeof(StudioDraftReport))]
[JsonSerializable(typeof(StudioDeleteReport))]
[JsonSerializable(typeof(StudioShowReport))]
[JsonSerializable(typeof(StudioCheckReport))]
[JsonSerializable(typeof(StudioImportReport))]
[JsonSerializable(typeof(StudioEditReport))]
[JsonSerializable(typeof(StudioSharedHashesReport))]
[JsonSerializable(typeof(StudioHashCarriersReport))]
[JsonSerializable(typeof(StudioIgnoredHashesReport))]
[JsonSerializable(typeof(StudioTryReport))]
[JsonSerializable(typeof(StudioExportReport))]
[JsonSerializable(typeof(StudioInstallReport))]
[JsonSerializable(typeof(StudioCaughtUpReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
