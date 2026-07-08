using Whiskers.Models.Help;

namespace Whiskers.Services.Help;

/// <summary>Supplies the in-app user handbook content (chapters + sections) rendered by the Hilfe page.</summary>
public interface IHelpContentService
{
    /// <summary>All handbook chapters in reading order.</summary>
    IReadOnlyList<HelpChapter> GetChapters();
}
