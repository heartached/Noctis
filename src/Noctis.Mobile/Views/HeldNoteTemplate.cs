using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Noctis.Models;

namespace Noctis.Mobile.Views;

/// <summary>
/// Builds <see cref="Template"/> for a held-note word only (<see cref="WordTiming.IsEmphasis"/>,
/// fixed when the line is parsed) and nothing for any other word.
///
/// The lyrics' held-note glow is a second word under the sung one. Built for every word and
/// hidden on all but the held notes, it still took every frame's progress and float through
/// its ten bindings and doubled the cost of building each line that scrolled in (S23, 11-14).
/// </summary>
public sealed class HeldNoteTemplate : IDataTemplate
{
    public IDataTemplate? Template { get; set; }

    // Every word matches, so a word that is not a held note gets no control rather than the
    // fallback text a ContentPresenter shows for data no template matched.
    public bool Match(object? data) => data is WordTiming;

    public Control? Build(object? param) => param is WordTiming { IsEmphasis: true } ? Template?.Build(param) : null;
}
