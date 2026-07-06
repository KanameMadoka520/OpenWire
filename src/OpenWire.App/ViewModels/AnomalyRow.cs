using System.Globalization;
using OpenWire.App.Util;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>
/// Presentation wrapper around a <see cref="UsageAnomaly"/> for the Analytics list.
/// It regenerates the Title/Detail text app-side from the anomaly's structured fields
/// (Kind, AppName, CountryCode, Hour, ObservedBytes, BaselineBytes, Ratio) using
/// localized templates, so the anomaly cards are fully translated without any engine
/// change. <see cref="Kind"/> and <see cref="Severity"/> pass through unchanged so the
/// existing glyph (AnomGlyph) and severity-colour (SevBrush) converters keep working.
/// </summary>
public sealed class AnomalyRow
{
    public AnomalyKind Kind { get; }
    public AlertSeverity Severity { get; }
    public string Title { get; }
    public string Detail { get; }

    public AnomalyRow(UsageAnomaly a)
    {
        Kind = a.Kind;
        Severity = a.Severity;

        // Faithful to the engine's AnomalyDetector: ratio "0.#", hour zero-padded, and
        // byte counts via the shared formatter. Ratio/hour use the invariant culture so
        // the numeric shape stays "4.5"/"03" in every language, matching ByteFormatter.
        string ratio = a.Ratio.ToString("0.#", CultureInfo.InvariantCulture);
        string hour = a.Hour.ToString("00", CultureInfo.InvariantCulture);

        switch (a.Kind)
        {
            case AnomalyKind.VolumeSpike:
                Title = string.Format(Loc.S("L.Anom.SpikeTitleFmt"), a.AppName);
                Detail = string.Format(Loc.S("L.Anom.SpikeDetailFmt"),
                    a.AppName, ByteFormatter.Bytes(a.ObservedBytes), ratio, ByteFormatter.Bytes(a.BaselineBytes));
                break;

            case AnomalyKind.UploadHeavy:
                // In this Kind the detector stores ObservedBytes = bytes uploaded and
                // BaselineBytes = bytes downloaded (see AnomalyDetector).
                Title = string.Format(Loc.S("L.Anom.UploadTitleFmt"), a.AppName);
                Detail = string.Format(Loc.S("L.Anom.UploadDetailFmt"),
                    a.AppName, ByteFormatter.Bytes(a.ObservedBytes), ByteFormatter.Bytes(a.BaselineBytes), ratio);
                break;

            case AnomalyKind.NewCountry:
            {
                // Localized country/region name (keeps the one-China convention).
                string country = CountryName.Localized(a.CountryCode, a.CountryCode);
                Title = string.Format(Loc.S("L.Anom.NewCountryTitleFmt"), country);
                Detail = string.Format(Loc.S("L.Anom.NewCountryDetailFmt"), country);
                break;
            }

            case AnomalyKind.OddHour:
                Title = string.Format(Loc.S("L.Anom.OddHourTitleFmt"), hour);
                Detail = string.Format(Loc.S("L.Anom.OddHourDetailFmt"), ByteFormatter.Bytes(a.ObservedBytes), hour);
                break;

            default:
                // Unknown future Kind: keep the engine's English text rather than blank.
                Title = a.Title;
                Detail = a.Detail;
                break;
        }
    }
}
