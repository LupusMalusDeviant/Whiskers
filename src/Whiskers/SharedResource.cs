namespace Whiskers;

/// <summary>
/// Marker type for the shared UI string table (<c>Resources/SharedResource.resx</c> = English /
/// neutral fallback, <c>SharedResource.de.resx</c> = German). Inject
/// <c>IStringLocalizer&lt;SharedResource&gt;</c> and look strings up by a <c>Page_Element_Purpose</c>
/// key. RoadToSAP/missingFeatures F2 — the i18n sweep is ongoing; new pages add their keys here.
/// </summary>
public sealed class SharedResource
{
}
