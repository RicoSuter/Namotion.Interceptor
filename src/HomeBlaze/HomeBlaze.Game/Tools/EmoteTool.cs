using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tools;

/// <summary>
/// Lets players clap, sit and jump; everyone nearby sees it.
/// </summary>
[Category("Game")]
[Description("Lets players clap, sit and jump")]
[DisplayName("Emote tool")]
[SubjectIcon("SignLanguage")]
[InterceptorSubject]
public partial class EmoteTool : GameTool, ITitleProvider, IIconProvider
{
    public string? Title => "Emote tool";

    public string? IconName => "SignLanguage";

    /// <summary>Claps once.</summary>
    /// <param name="caller">The acting player.</param>
    /// <exception cref="InvalidOperationException">There is no player or the tool is not part of a game.</exception>
    [Operation(Title = "Clap", Icon = "SignLanguage", Position = 1, Description = "Claps your hands once.")]
    public void Clap([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Clapping", GameVisualAnimationMode.Once);

    /// <summary>Sits down until the next step.</summary>
    /// <param name="caller">The acting player.</param>
    /// <exception cref="InvalidOperationException">There is no player or the tool is not part of a game.</exception>
    [Operation(Title = "Sit", Icon = "Chair", Position = 2, Description = "Sits down until you walk on.")]
    public void Sit([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Sitting", GameVisualAnimationMode.Loop);

    /// <summary>Jumps once.</summary>
    /// <param name="caller">The acting player.</param>
    /// <exception cref="InvalidOperationException">There is no player or the tool is not part of a game.</exception>
    [Operation(Title = "Jump", Icon = "Height", Position = 3, Description = "Jumps once.")]
    public void Jump([FromServices] GameCaller? caller) =>
        World.PlayAnimation(caller, "Jump", GameVisualAnimationMode.Once);
}
