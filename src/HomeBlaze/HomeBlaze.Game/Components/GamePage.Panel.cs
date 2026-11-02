using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Components.Operations;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.View;
using HomeBlaze.Game.Voice;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Game.Components;

// The panel: the actions of the game and its tools, the targets the player looks at, Nearby, renaming and the quality.
public partial class GamePage
{
    private const string InsecureMessage = "Voice needs HTTPS";
    private const string AutoQuality = "auto";

    private string _quality = AutoQuality;

    private bool IsMicrophoneOn => _voice?.IsMicrophoneOn == true;

    private bool IsAudioOn => _voice?.IsAudioOn == true;

    // Stays enabled while a toggle runs, so the button keeps the focus; the toggle ignores clicks meanwhile. Without a
    // player, a button stays enabled only to turn off what is still on.
    private bool IsMicrophoneDisabled => _voice?.IsSupported != true || (_player is null && !IsMicrophoneOn);

    private bool IsAudioDisabled => _voice?.IsSupported != true || (_player is null && !IsAudioOn);

    private string? VoiceMessage => _voice?.Message;

    private string MicrophoneTooltip => (_voice?.IsSupported, IsMicrophoneOn) switch
    {
        (false, _) => InsecureMessage,
        (_, true) => "Turn the microphone off (M)",
        _ => "Turn the microphone on (M)"
    };

    private string AudioTooltip => (_voice?.IsSupported, IsAudioOn) switch
    {
        (false, _) => InsecureMessage,
        (_, true) => "Turn the speaker off (N)",
        _ => "Turn the speaker on (N)"
    };

    private async Task InvokeActionAsync(IInterceptorSubject owner, MethodMetadata method)
    {
        if (_isDisposed || _isInvoking || _player is not { } player)
        {
            return;
        }

        // The latest panel may no longer show the target that was rendered.
        if (owner is GameObject target && !IsTarget(target))
        {
            return;
        }

        _isInvoking = true;
        // A number key reaches this without the re-render a click gets.
        StateHasChanged();
        try
        {
            var services = new GameCallerServiceProvider(ServiceProvider, new GameCaller(player.ClientId));
            var key = $"{owner.GetType().FullName}.{method.PropertyName}";
            var result = await OperationInvoker.InvokeAsync(DialogService, services, method, _lifetime.Token,
                initialValues: _lastParameters.GetValueOrDefault(key));
            if (result.Parameters is { } parameters)
            {
                _lastParameters[key] = parameters;
            }
        }
        finally
        {
            _isInvoking = false;
        }

        await FlushAsync();
        if (!_isDisposed)
        {
            // A flush renders only what changed, and the actions are enabled again.
            StateHasChanged();
        }
    }

    private static string QualityLabel(string? choice) => choice switch
    {
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        _ => "Auto"
    };

    private async Task SetQualityAsync(string choice)
    {
        _quality = choice;
        if (_view is { } view)
        {
            await view.SetQualityAsync(choice);
        }
    }

    private async Task EditAsync()
    {
        if (_isDisposed || _isEditing || Game is not { } game || _player is not { } player)
        {
            return;
        }

        _isEditing = true;
        try
        {
            var caller = new GameCaller(player.ClientId);
            var edit = await GamePlayerDialog.ShowAsync(DialogService, player, game.GetClientName(caller));
            if (edit is not null && !_isDisposed)
            {
                await game.EditAsync(caller, edit.Name, edit.Character, edit.Color, CancellationToken.None);
            }
        }
        catch (Exception exception) when (!_isDisposed)
        {
            await MethodErrorDialog.ShowAsync(DialogService, EditTitle, exception);
        }
        finally
        {
            _isEditing = false;
        }
    }

    // The own player and the players it hears, by the same rule that mixes the voices. A new table with the same players
    // keeps the list, unless the view or a publish changed their toggles or titles. Returns whether the entries changed.
    private bool RefreshNearby(Game game, bool isChanged)
    {
        var publishVersion = game.PublishVersion;
        if (_player is not { } player)
        {
            var wasEmpty = _nearby.Count == 0;
            _nearby = [];
            _nearbyPlayer = null;
            _nearbyPlayerIds = [];
            return !wasEmpty;
        }

        var row = game.VolumeTable.GetRow(player.ClientId);
        if (!isChanged && HasSameNearbyPlayers(player, row))
        {
            return false;
        }

        var players = game.Players;
        var playerIds = new string[row.Length];
        var nearby = new List<NearbyEntry>(row.Length + 1) { CreateNearbyEntry(player, isSelf: true) };
        for (var index = 0; index < row.Length; index++)
        {
            playerIds[index] = row[index].PlayerId;
            if (players.TryGetValue(row[index].PlayerId, out var other))
            {
                nearby.Add(CreateNearbyEntry(other, isSelf: false));
            }
        }

        nearby.Sort(static (first, second) => first.IsSelf != second.IsSelf
            ? second.IsSelf.CompareTo(first.IsSelf)
            : StringComparer.CurrentCulture.Compare(first.Title, second.Title));
        var isEntriesChanged = !nearby.SequenceEqual(_nearby);
        if (isEntriesChanged)
        {
            _nearby = nearby;
        }

        _nearbyPlayer = player;
        _nearbyPlayerIds = playerIds;
        _nearbyPublishVersion = publishVersion;
        return isEntriesChanged;
    }

    private bool HasSameNearbyPlayers(GamePlayer player, ReadOnlySpan<GameVolumeEntry> row)
    {
        if (!ReferenceEquals(player, _nearbyPlayer) || row.Length != _nearbyPlayerIds.Length)
        {
            return false;
        }

        for (var index = 0; index < row.Length; index++)
        {
            if (row[index].PlayerId != _nearbyPlayerIds[index])
            {
                return false;
            }
        }

        return true;
    }

    private static NearbyEntry CreateNearbyEntry(GamePlayer player, bool isSelf) =>
        new(player.Title ?? string.Empty, GameColors.Resolve(player.Color, player.ClientId), isSelf, player.IsMicrophoneOn, player.IsAudioOn);

    // Keeps the former lists while they hold the same entries; returns whether they changed.
    private bool RefreshActions(Game game, GamePanelState panelState)
    {
        var gameActions = GetGameActions(game);
        var isChanged = !gameActions.SequenceEqual(_actions);
        if (isChanged)
        {
            _actions = gameActions;
        }

        if (_player is null)
        {
            isChanged |= _targets.Count > 0;
            _targets = [];
            return isChanged;
        }

        var key = 0;
        var targets = new List<TargetEntry>();
        foreach (var target in panelState.Targets)
        {
            var operations = GetOperations(target);
            var actions = new List<ActionEntry>(operations.Count);
            foreach (var method in operations)
            {
                actions.Add(new ActionEntry(key < 9 ? ++key : 0, method));
            }

            targets.Add(new TargetEntry(target, (target as ITitleProvider)?.Title ?? target.GetType().Name, actions));
        }

        if (targets.SequenceEqual(_targets, TargetEntryComparer.Instance))
        {
            return isChanged;
        }

        _targets = targets;
        return true;
    }

    private bool IsTarget(GameObject gameObject)
    {
        if (_view is not { } view)
        {
            return false;
        }

        var targets = view.PanelState.Targets;
        for (var index = 0; index < targets.Count; index++)
        {
            if (ReferenceEquals(targets[index], gameObject))
            {
                return true;
            }
        }

        return false;
    }

    // The operations of the game, then those of its tools ordered by name. Runs on every flush, so it sorts only
    // when there is more than one tool.
    private static List<GameAction> GetGameActions(Game game)
    {
        var actions = new List<GameAction>();
        AddActions(actions, game);
        var tools = game.Tools;
        if (tools.Count == 1)
        {
            foreach (var (_, tool) in tools)
            {
                AddActions(actions, tool);
            }
        }
        else if (tools.Count > 1)
        {
            foreach (var (_, tool) in tools.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AddActions(actions, tool);
            }
        }

        return actions;
    }

    private static void AddActions(List<GameAction> actions, IInterceptorSubject subject)
    {
        foreach (var method in GetOperations(subject))
        {
            actions.Add(new GameAction(subject, method));
        }
    }

    // Disabled operations are left out rather than shown disabled, so the number keys only reach operations that apply.
    private static IReadOnlyList<MethodMetadata> GetOperations(IInterceptorSubject subject)
    {
        if (subject.TryGetRegisteredSubject() is not { } registered)
        {
            return [];
        }

        return registered.GetAllMethods(ExclusionTarget.UserInterface)
            .Where(method => method.Kind == MethodKind.Operation && (registered.TryGetProperty(method.PropertyName)?.IsMethodEnabled() ?? true))
            .ToList();
    }

    private sealed record NearbyEntry(string Title, string Color, bool IsSelf, bool IsMicrophoneOn, bool IsAudioOn);

    private sealed record TargetEntry(IInterceptorSubject Subject, string Title, IReadOnlyList<ActionEntry> Actions);

    // Compares the actions by their entries, which the record compares by reference.
    private sealed class TargetEntryComparer : IEqualityComparer<TargetEntry>
    {
        public static TargetEntryComparer Instance { get; } = new();

        public bool Equals(TargetEntry? first, TargetEntry? second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            if (first is null || second is null)
            {
                return false;
            }

            return ReferenceEquals(first.Subject, second.Subject) && first.Title == second.Title && first.Actions.SequenceEqual(second.Actions);
        }

        public int GetHashCode(TargetEntry entry) => entry.Subject.GetHashCode();
    }

    private sealed record ActionEntry(int Key, MethodMetadata Method);

    private sealed record GameAction(IInterceptorSubject Subject, MethodMetadata Method);
}
