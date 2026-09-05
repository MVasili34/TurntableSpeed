using TurntableSpeed.App.Localization;
using TurntableSpeed.Core.Estimators;

namespace TurntableSpeed.App.Presentation;

/// <summary>How loudly the UI should say something.</summary>
public enum NoticeLevel
{
    /// <summary>Context the user needs, always true, not a fault.</summary>
    Information,

    /// <summary>The measurement is still usable but something is off.</summary>
    Caution,

    /// <summary>The result should not be trusted as it stands.</summary>
    Problem,
}

/// <param name="Title">Short enough for a chip or a banner heading.</param>
/// <param name="Detail">One or two sentences saying what to do about it.</param>
public sealed record Notice(NoticeLevel Level, string Title, string Detail);

/// <summary>
/// Turns estimator warning flags into what the user actually reads.
/// <para>
/// The text is resolved from the resource set at the moment the list is built, and the screens
/// rebuild their notices when the language changes, so nothing here has to be re-translated in
/// place. Two of these messages are required by the specification rather than merely helpful —
/// the phone's own weight in sensor mode (§3.6) and the record's own tuning in the pitch
/// estimator (§4.3) — and both are covered by tests in either language.
/// </para>
/// </summary>
public static class UserMessages
{
    /// <summary>Spec §3.6. Present on every sensor-mode result, without exception.</summary>
    public static string LoadCaveat => AppStrings.LoadCaveat;

    /// <summary>Spec §4.3. Present whenever a pitch-grid reading is shown.</summary>
    public static string PitchCaveat => AppStrings.PitchCaveat;

    public static IReadOnlyList<Notice> For(SensorWarning warnings)
    {
        var notices = new List<Notice>();

        // Ordered by how much they should worry the user, not by flag value.
        if (warnings.HasFlag(SensorWarning.NoMagnetometer))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeNoMagnetometerTitle, AppStrings.NoticeNoMagnetometerDetail));
        }

        if (warnings.HasFlag(SensorWarning.MethodsDisagree))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeSensorMethodsDisagreeTitle, AppStrings.NoticeSensorMethodsDisagreeDetail));
        }

        if (warnings.HasFlag(SensorWarning.MagneticDisturbance))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeMagneticDisturbanceTitle, AppStrings.NoticeMagneticDisturbanceDetail));
        }

        if (warnings.HasFlag(SensorWarning.PhoneNotFlat))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticePhoneNotFlatTitle, AppStrings.NoticePhoneNotFlatDetail));
        }

        if (warnings.HasFlag(SensorWarning.GyroZeroNotCalibrated))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticeGyroZeroTitle, AppStrings.NoticeGyroZeroDetail));
        }

        if (warnings.HasFlag(SensorWarning.NoGyroscope))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticeNoGyroscopeTitle, AppStrings.NoticeNoGyroscopeDetail));
        }

        if (warnings.HasFlag(SensorWarning.NotEnoughRevolutions))
        {
            notices.Add(new Notice(NoticeLevel.Information,
                AppStrings.NoticeNotEnoughRevolutionsTitle, AppStrings.NoticeNotEnoughRevolutionsDetail));
        }

        // Last in the list and always present: it is context, not a fault.
        if (warnings.HasFlag(SensorWarning.LoadAffectsMeasurement))
        {
            notices.Add(new Notice(NoticeLevel.Information, AppStrings.NoticeLoadTitle, LoadCaveat));
        }

        return notices;
    }

    public static IReadOnlyList<Notice> For(AudioWarning warnings)
    {
        var notices = new List<Notice>();

        if (warnings.HasFlag(AudioWarning.InputProcessingSuspected))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeInputProcessedTitle, AppStrings.NoticeInputProcessedDetail));
        }

        if (warnings.HasFlag(AudioWarning.Clipping))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeClippingTitle, AppStrings.NoticeClippingDetail));
        }

        if (warnings.HasFlag(AudioWarning.SignalTooQuiet))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeTooQuietTitle, AppStrings.NoticeTooQuietDetail));
        }

        if (warnings.HasFlag(AudioWarning.MethodsDisagree))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeAudioMethodsDisagreeTitle, AppStrings.NoticeAudioMethodsDisagreeDetail));
        }

        if (warnings.HasFlag(AudioWarning.NoClickPeriodicity))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticeNoClicksTitle, AppStrings.NoticeNoClicksDetail));
        }

        if (warnings.HasFlag(AudioWarning.NoNominalYet))
        {
            notices.Add(new Notice(NoticeLevel.Information,
                AppStrings.NoticeNoNominalTitle, AppStrings.NoticeNoNominalDetail));
        }

        if (warnings.HasFlag(AudioWarning.NotEnoughData))
        {
            notices.Add(new Notice(NoticeLevel.Information,
                AppStrings.NoticeCollectingTitle, AppStrings.NoticeCollectingDetail));
        }

        if (warnings.HasFlag(AudioWarning.PitchMixesRecordTuning))
        {
            notices.Add(new Notice(NoticeLevel.Information, AppStrings.NoticeRecordTuningTitle, PitchCaveat));
        }

        return notices;
    }

    /// <summary>Input diagnostics for the acoustic screen, at the level of detail §4.1 asks for.</summary>
    public static IReadOnlyList<Notice> For(AudioInputWarning warnings)
    {
        var notices = new List<Notice>();

        if (warnings.HasFlag(AudioInputWarning.NoiseGateSuspected))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeNoiseGateTitle, AppStrings.NoticeNoiseGateDetail));
        }

        if (warnings.HasFlag(AudioInputWarning.AutomaticGainControlSuspected))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticeAgcTitle, AppStrings.NoticeAgcDetail));
        }

        if (warnings.HasFlag(AudioInputWarning.ProcessingNotDisabled))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticeRawInputUnavailableTitle, AppStrings.NoticeRawInputUnavailableDetail));
        }

        if (warnings.HasFlag(AudioInputWarning.Clipping))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeInputClippingTitle, AppStrings.NoticeInputClippingDetail));
        }

        if (warnings.HasFlag(AudioInputWarning.SignalTooQuiet))
        {
            notices.Add(new Notice(NoticeLevel.Problem,
                AppStrings.NoticeInputTooQuietTitle, AppStrings.NoticeInputTooQuietDetail));
        }

        if (warnings.HasFlag(AudioInputWarning.DcOffset))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticeDcOffsetTitle, AppStrings.NoticeDcOffsetDetail));
        }

        if (warnings.HasFlag(AudioInputWarning.UnexpectedSampleRate))
        {
            notices.Add(new Notice(NoticeLevel.Caution,
                AppStrings.NoticeSampleRateTitle, AppStrings.NoticeSampleRateDetail));
        }

        return notices;
    }

    /// <summary>The worst level present, for colouring a single summary indicator.</summary>
    public static NoticeLevel WorstLevel(IReadOnlyList<Notice> notices)
    {
        var worst = NoticeLevel.Information;
        foreach (var notice in notices)
        {
            if (notice.Level > worst)
            {
                worst = notice.Level;
            }
        }

        return worst;
    }
}
